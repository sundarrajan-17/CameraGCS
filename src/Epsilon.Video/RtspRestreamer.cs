using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Epsilon.Video;

public enum RestreamMode
{
    /// <summary>This PC runs an RTSP server (MediaMTX). Clients pull rtsp://this-pc:port/path.</summary>
    Serve,
    /// <summary>Push (RTSP ANNOUNCE/RECORD) to an RTSP server running at the destination.</summary>
    Push,
}

public enum RestreamState { Stopped, Starting, Running, Restarting, Error }

public sealed class RestreamSettings
{
    public RestreamMode Mode { get; set; } = RestreamMode.Serve;
    public int RtspPort { get; set; } = 8554;
    public string Path { get; set; } = "epsilon";
    /// <summary>
    /// Serve mode: the URL clients use, e.g. rtsp://192.168.1.10:8554/live. Its host must be an address
    /// (or host name) of this PC; its port and path override <see cref="RtspPort"/> and <see cref="Path"/>.
    /// Empty = automatic (rtsp://&lt;best local IP&gt;:RtspPort/Path).
    /// </summary>
    public string CustomUrl { get; set; } = "";
    /// <summary>Serve mode: only this IP (or comma separated IPs / CIDRs) may play. Empty = anyone.</summary>
    public string AllowedIps { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Push mode destination, e.g. rtsp://192.168.1.50:8554/epsilon</summary>
    public string PushUrl { get; set; } = "rtsp://192.168.1.50:8554/epsilon";
    public bool PushUseTcp { get; set; } = true;
    /// <summary>Also forward the KLV data track (experimental, depends on FFmpeg/receiver support).</summary>
    public bool IncludeKlv { get; set; }
    public bool AutoStart { get; set; }
}

/// <summary>
/// Restreams the gimbal video over RTSP without re-encoding.
/// Serve mode: FFmpeg (copy) -> MediaMTX on this PC -> RTSP clients.
/// Push mode:  FFmpeg (copy) -> remote RTSP server.
/// Requires tools\ffmpeg.exe and (Serve mode) tools\mediamtx.exe - run tools\get-tools.ps1 once.
/// </summary>
public sealed class RtspRestreamer : IDisposable
{
    private readonly string _toolsDir;
    private readonly string _workDir;
    private CancellationTokenSource _cts;
    private Process _mediamtx;
    private Process _ffmpeg;
    private RestreamState _state = RestreamState.Stopped;

    public RtspRestreamer(string toolsDir, string workDir)
    {
        _toolsDir = toolsDir;
        _workDir = workDir;
        Directory.CreateDirectory(_workDir);
    }

    public RestreamState State => _state;
    public string StatusText { get; private set; } = "Stopped";
    public string OutputUrl { get; private set; } = "";
    public string FfmpegPath => System.IO.Path.Combine(_toolsDir, "ffmpeg.exe");
    public string MediaMtxPath => System.IO.Path.Combine(_toolsDir, "mediamtx.exe");

    public event Action<string> Log;
    public event Action<RestreamState> StateChanged;

    public bool ToolsAvailable(RestreamMode mode, out string missing)
    {
        var list = new List<string>();
        if (!File.Exists(FfmpegPath)) list.Add(FfmpegPath);
        if (mode == RestreamMode.Serve && !File.Exists(MediaMtxPath)) list.Add(MediaMtxPath);
        missing = string.Join(", ", list);
        return list.Count == 0;
    }

    /// <param name="inputUrl">FFmpeg input, e.g. udp://127.0.0.1:15011 or an RTSP URL.</param>
    public void Start(RestreamSettings s, string inputUrl)
    {
        Stop();
        if (!ToolsAvailable(s.Mode, out var missing))
        {
            SetState(RestreamState.Error, "Missing tools: " + missing + "  (run tools\\get-tools.ps1)");
            return;
        }
        var ep = new ServeEndpoint(BestLocalIp(), s.RtspPort, s.Path);
        if (s.Mode == RestreamMode.Serve && !string.IsNullOrWhiteSpace(s.CustomUrl))
        {
            if (!TryParseServeUrl(s.CustomUrl, s.RtspPort, out ep, out var error))
            {
                SetState(RestreamState.Error, error);
                return;
            }
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => Supervise(s, ep, inputUrl, ct));
    }

    /// <summary>Host / port / path the RTSP server is advertised on (Serve mode).</summary>
    public readonly record struct ServeEndpoint(string Host, int Port, string Path)
    {
        public string Url => $"rtsp://{Host}:{Port}/{Path}";
    }

    /// <summary>
    /// Validates a custom Serve-mode URL such as rtsp://192.168.1.10:8554/live.
    /// The host must be an IPv4 address of this PC or a host name; the port defaults to <paramref name="defaultPort"/>.
    /// </summary>
    public static bool TryParseServeUrl(string url, int defaultPort, out ServeEndpoint endpoint, out string error)
    {
        endpoint = default;
        error = null;
        url = (url ?? "").Trim();
        if (!url.Contains("://")) url = "rtsp://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(uri.Host))
        {
            error = "Invalid stream URL. Use the form rtsp://<this PC IP>:<port>/<path>, e.g. rtsp://192.168.1.10:8554/epsilon";
            return false;
        }
        string host = uri.Host;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "Only IPv4 addresses are supported in the stream URL.";
                return false;
            }
            bool local = System.Net.IPAddress.IsLoopback(ip) || host == "0.0.0.0" || LocalIPv4Addresses().Contains(host);
            if (!local)
            {
                error = $"{host} is not an address of this PC (this PC has: {string.Join(", ", LocalIPv4Addresses())}). " +
                        "In Serve mode the URL must use one of this PC's addresses. " +
                        "To send the video to a server on another computer, use Push mode.";
                return false;
            }
        }
        int port = uri.IsDefaultPort || uri.Port <= 0 ? defaultPort : uri.Port;
        string path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (path.Length == 0) path = "epsilon";
        if (!path.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '~'))
        {
            error = "The stream path may only contain letters, digits and - _ . / ~";
            return false;
        }
        endpoint = new ServeEndpoint(host, port, path);
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        Kill(ref _ffmpeg);
        Kill(ref _mediamtx);
        if (_state != RestreamState.Stopped)
            SetState(RestreamState.Stopped, "Stopped");
        OutputUrl = "";
    }

    private async Task Supervise(RestreamSettings s, ServeEndpoint ep, string inputUrl, CancellationToken ct)
    {
        SetState(RestreamState.Starting, "Starting...");
        string publishUrl;
        if (s.Mode == RestreamMode.Serve)
        {
            string yml = WriteMediaMtxConfig(s, ep.Port, ep.Path);
            OutputUrl = ep.Url;
            publishUrl = $"rtsp://127.0.0.1:{ep.Port}/{ep.Path}";
            _mediamtx = StartProcess(MediaMtxPath, Quote(yml), "mediamtx");
            if (_mediamtx == null) { SetState(RestreamState.Error, "Could not start MediaMTX"); return; }
            try { await Task.Delay(800, ct); } catch (OperationCanceledException) { return; }
        }
        else
        {
            OutputUrl = s.PushUrl;
            publishUrl = s.PushUrl;
        }

        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (s.Mode == RestreamMode.Serve && (_mediamtx == null || _mediamtx.HasExited))
            {
                Log?.Invoke("[restream] MediaMTX exited, restarting");
                _mediamtx = StartProcess(MediaMtxPath, Quote(System.IO.Path.Combine(_workDir, "mediamtx.yml")), "mediamtx");
                try { await Task.Delay(800, ct); } catch (OperationCanceledException) { break; }
            }

            var args = BuildFfmpegArgs(inputUrl, publishUrl, s);
            Log?.Invoke("[restream] ffmpeg " + args);
            var started = DateTime.UtcNow;
            var ffmpeg = StartProcess(FfmpegPath, args, "ffmpeg");
            _ffmpeg = ffmpeg;
            if (ffmpeg == null) { SetState(RestreamState.Error, "Could not start FFmpeg"); break; }

            SetState(RestreamState.Running, s.Mode == RestreamMode.Serve ? "Serving " + OutputUrl : "Pushing to " + OutputUrl);
            try { await ffmpeg.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { break; }

            if (ct.IsCancellationRequested) break;
            if ((DateTime.UtcNow - started).TotalSeconds > 30) attempt = 0;
            attempt++;
            int delay = Math.Min(10, attempt * 2);
            SetState(RestreamState.Restarting, $"FFmpeg stopped (no video?) - retry in {delay}s");
            try { await Task.Delay(delay * 1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static string BuildFfmpegArgs(string input, string output, RestreamSettings s)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -nostdin ");
        sb.Append("-fflags +genpts+discardcorrupt ");
        if (input.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            string sep = input.Contains('?') ? "&" : "?";
            sb.Append("-i ").Append(Quote(input + sep + "fifo_size=1000000&overrun_nonfatal=1&timeout=10000000")).Append(' ');
        }
        else
        {
            sb.Append("-rtsp_transport tcp -i ").Append(Quote(input)).Append(' ');
        }
        sb.Append("-map 0:v:0 ");
        if (s.IncludeKlv) sb.Append("-map 0:d? ");
        sb.Append("-c copy ");
        sb.Append("-f rtsp ");
        if (s.Mode == RestreamMode.Serve || s.PushUseTcp) sb.Append("-rtsp_transport tcp ");
        sb.Append(Quote(output));
        return sb.ToString();
    }

    private string WriteMediaMtxConfig(RestreamSettings s, int port, string streamPath)
    {
        string ips = string.Join(", ",
            (s.AllowedIps ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ip => "'" + (ip.Contains('/') ? ip : ip + "/32") + "'"));
        string user = string.IsNullOrWhiteSpace(s.Username) ? "any" : s.Username;
        string pass = string.IsNullOrWhiteSpace(s.Username) ? "" : (s.Password ?? "");
        string passEscaped = pass.Replace("'", "''");

        var yml = $@"# Generated by Epsilon GCS - do not edit, it is rewritten on every start.
logLevel: warn
rtspAddress: :{port}
rtmp: no
hls: no
webrtc: no
srt: no
authInternalUsers:
  - user: any
    pass:
    ips: ['127.0.0.1/32', '::1/128']
    permissions:
      - action: publish
      - action: read
  - user: {user}
    pass: '{passEscaped}'
    ips: [{ips}]
    permissions:
      - action: read
paths:
  {streamPath}:
    source: publisher
";
        string path = System.IO.Path.Combine(_workDir, "mediamtx.yml");
        File.WriteAllText(path, yml);
        return path;
    }

    private Process StartProcess(string exe, string args, string tag)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = _workDir,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke($"[{tag}] {e.Data}"); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke($"[{tag}] {e.Data}"); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[{tag}] failed to start: {ex.Message}");
            return null;
        }
    }

    private static void Kill(ref Process p)
    {
        var proc = p;
        p = null;
        if (proc == null) return;
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        proc.Dispose();
    }

    private void SetState(RestreamState state, string text)
    {
        _state = state;
        StatusText = text;
        Log?.Invoke("[restream] " + text);
        StateChanged?.Invoke(state);
    }

    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>
    /// Address advertised in the automatic Serve URL. Prefers an active adapter that has a default gateway,
    /// then any routable address; link-local 169.254.x.x (adapter without DHCP/static IP) is used only as a last resort.
    /// </summary>
    public static string BestLocalIp()
    {
        string withGateway = null, routable = null, linkLocal = null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string a = ua.Address.ToString();
                if (IsLinkLocal(a)) linkLocal ??= a;
                else if (hasGateway) withGateway ??= a;
                else routable ??= a;
            }
        }
        return withGateway ?? routable ?? linkLocal ?? "127.0.0.1";
    }

    private static bool IsLinkLocal(string ip) => ip.StartsWith("169.254.", StringComparison.Ordinal);

    public static IEnumerable<string> LocalIPv4Addresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return ua.Address.ToString();
        }
    }

    public void Dispose() => Stop();
}
