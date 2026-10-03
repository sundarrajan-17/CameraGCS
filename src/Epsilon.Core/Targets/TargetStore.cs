namespace Epsilon.Core.Targets;

/// <summary>
/// The single source of truth for targets and splashes. The Targets page and the map markers are views of
/// this store; they look objects up by <c>Id</c>. IDs are unique and never reused during a session.
/// Intended to be used from the UI thread; the lock only protects against accidental use from other threads.
/// </summary>
public sealed class TargetStore
{
    private readonly object _lock = new();
    private readonly List<TargetInfo> _targets = new();
    private readonly List<SplashInfo> _splashes = new();
    private int _nextTargetId = 1, _nextSplashId = 1;

    /// <summary>Raised after any add / update / remove.</summary>
    public event Action Changed;

    public IReadOnlyList<TargetInfo> Targets { get { lock (_lock) return _targets.ToArray(); } }
    public IReadOnlyList<SplashInfo> Splashes { get { lock (_lock) return _splashes.ToArray(); } }

    public TargetInfo FindTarget(int id) { lock (_lock) return _targets.FirstOrDefault(t => t.Id == id); }
    public SplashInfo FindSplash(int id) { lock (_lock) return _splashes.FirstOrDefault(s => s.Id == id); }

    public static bool IsValidPosition(double lat, double lon) =>
        !double.IsNaN(lat) && !double.IsNaN(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180 &&
        !(lat == 0 && lon == 0);

    public TargetInfo AddTarget(double lat, double lon, TargetSource source, string name = null,
                                int? rangeM = null, bool rangeByLrf = false, DateTime? time = null)
    {
        if (!IsValidPosition(lat, lon)) throw new ArgumentOutOfRangeException(nameof(lat), "Invalid target position.");
        TargetInfo t;
        lock (_lock)
        {
            int id = _nextTargetId++;
            t = new TargetInfo
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? $"Target {id}" : name.Trim(),
                Latitude = lat,
                Longitude = lon,
                DateTime = time ?? DateTime.Now,
                Source = source,
                RangeM = rangeM,
                RangeMeasuredByLrf = rangeByLrf,
            };
            _targets.Add(t);
        }
        Changed?.Invoke();
        return t;
    }

    /// <summary>Updates name / position / status of an existing target. Returns false if the ID is unknown.</summary>
    public bool UpdateTarget(int id, string name, double lat, double lon, TargetStatus status, string notes)
    {
        if (!IsValidPosition(lat, lon)) throw new ArgumentOutOfRangeException(nameof(lat), "Invalid target position.");
        lock (_lock)
        {
            var t = _targets.FirstOrDefault(x => x.Id == id);
            if (t == null) return false;
            t.Name = string.IsNullOrWhiteSpace(name) ? t.Name : name.Trim();
            t.Latitude = lat;
            t.Longitude = lon;
            t.Status = status;
            t.Notes = notes ?? "";
        }
        Changed?.Invoke();
        return true;
    }

    public bool RemoveTarget(int id)
    {
        bool removed;
        lock (_lock) removed = _targets.RemoveAll(t => t.Id == id) > 0;
        if (removed) Changed?.Invoke();
        return removed;
    }

    public SplashInfo AddSplash(double lat, double lon, int? targetId = null, DateTime? time = null)
    {
        if (!IsValidPosition(lat, lon)) throw new ArgumentOutOfRangeException(nameof(lat), "Invalid splash position.");
        SplashInfo s;
        lock (_lock)
        {
            s = new SplashInfo
            {
                Id = _nextSplashId++,
                DateTime = time ?? DateTime.Now,
                Latitude = lat,
                Longitude = lon,
                TargetId = targetId,
            };
            _splashes.Add(s);
        }
        Changed?.Invoke();
        return s;
    }

    public bool RemoveSplash(int id)
    {
        bool removed;
        lock (_lock) removed = _splashes.RemoveAll(s => s.Id == id) > 0;
        if (removed) Changed?.Invoke();
        return removed;
    }
}
