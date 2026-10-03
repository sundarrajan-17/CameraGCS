using System.IO;
using System.Windows;
using System.Windows.Controls;
using Epsilon.Core.Protocol;
using EpsilonGCS.Services;
using Microsoft.Win32;

namespace EpsilonGCS.Flyouts;

/// <summary>
/// "Video Player" side tab: plays recordings (e.g. files copied from the gimbal SD card) in the main
/// video view using the same LibVLC player as the live stream. The live UDP receiver and the RTSP
/// restream keep running while a recording plays; LIVE switches back.
/// </summary>
public sealed class VideoPlayerPage : FlyoutPage
{
    public override string Title => "Video Player";

    private readonly TextBlock _source;
    private readonly TextBlock _time;
    private readonly ListBox _recent = new() { Height = 150, Margin = new Thickness(0, 4, 0, 4) };
    private readonly ChoiceField _rate;
    private string _folder;

    public VideoPlayerPage()
    {
        F.Section("Source");
        _source = F.Value("Now playing");
        _time = F.Value("Position");
        F.Buttons(("Open file...", OpenFile), ("Play / Pause", () => Gcs.Video.TogglePause()),
                  ("Stop", () => Gcs.Video.StopPlayback()), ("LIVE", () => Gcs.Video.Start()));
        _rate = F.Choice("Speed", 3, (1, "0.25x"), (2, "0.5x"), (3, "1x"), (4, "2x"), (5, "4x"));
        _rate.Box.SelectionChanged += (_, _) => Gcs.Video.SetRate(RateValue());
        F.Note("Use the timeline under the video to seek. The live stream and RTSP restream keep running in the background.");

        F.Section("Recordings folder");
        F.Note("Copy recordings from the gimbal SD card to a folder on this PC, then pick the folder here.");
        F.Buttons(("Choose folder...", ChooseFolder), ("Refresh", RefreshList), ("Open in Explorer", OpenFolder));
        _recent.MouseDoubleClick += (_, _) => PlaySelected();
        F.Add(_recent);
        F.Buttons(("Play selected", PlaySelected));

        F.Section("Gimbal recording");
        F.Buttons(("Start REC", () => Send(Cmd.VideoRecording(1))), ("Stop REC", () => Send(Cmd.VideoRecording(2))),
                  ("Snapshot (gimbal)", () => Send(Cmd.DoSnapshot())));

        _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "EpsilonGCS");
        RefreshList();
    }

    private float RateValue() => _rate.Value switch { 1 => 0.25f, 2 => 0.5f, 4 => 2f, 5 => 4f, _ => 1f };

    public override void OnOpened() => UpdateInfo();

    /// <summary>Called by the 1 s UI timer while the page is open.</summary>
    public override void OnStatus(GlobalStatus s) => UpdateInfo();

    public void UpdateInfo()
    {
        var v = Gcs.Video;
        _source.Text = v.IsPlayback ? $"{v.CurrentSource}  ({v.PlayerState})" : $"LIVE - {v.CurrentSource}";
        _time.Text = v.IsPlayback ? $"{v.Time:hh\\:mm\\:ss} / {v.Length:hh\\:mm\\:ss}" : "-";
    }

    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open recording",
            Filter = "Video files|*.ts;*.mp4;*.mkv;*.avi;*.mov;*.264;*.h264;*.265;*.h265|All files|*.*",
            InitialDirectory = Directory.Exists(_folder) ? _folder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dlg.ShowDialog() != true) return;
        _folder = Path.GetDirectoryName(dlg.FileName);
        RefreshList();
        Play(dlg.FileName);
    }

    private void Play(string file)
    {
        Gcs.Video.PlayFile(file);
        Gcs.Video.SetRate(RateValue());
        UpdateInfo();
    }

    private void ChooseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Recordings folder" };
        if (Directory.Exists(_folder)) dlg.InitialDirectory = _folder;
        if (dlg.ShowDialog() != true) return;
        _folder = dlg.FolderName;
        RefreshList();
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            System.Diagnostics.Process.Start("explorer.exe", _folder);
        }
        catch (Exception ex) { AppLog.Write("Cannot open folder: " + ex.Message); }
    }

    private void RefreshList()
    {
        _recent.Items.Clear();
        try
        {
            if (!Directory.Exists(_folder)) return;
            string[] ext = { ".ts", ".mp4", ".mkv", ".avi", ".mov", ".264", ".h264", ".265", ".h265" };
            foreach (var f in new DirectoryInfo(_folder).GetFiles()
                         .Where(f => ext.Contains(f.Extension.ToLowerInvariant()))
                         .OrderByDescending(f => f.LastWriteTime).Take(200))
                _recent.Items.Add(new ListBoxItem { Content = $"{f.Name}   ({f.Length / 1048576.0:0.0} MB)", Tag = f.FullName });
        }
        catch (Exception ex) { AppLog.Write("Cannot list recordings: " + ex.Message); }
    }

    private void PlaySelected()
    {
        if (_recent.SelectedItem is ListBoxItem item && item.Tag is string file) Play(file);
    }
}
