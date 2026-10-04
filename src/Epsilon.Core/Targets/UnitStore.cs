namespace Epsilon.Core.Targets;

/// <summary>
/// Source of truth for units, same pattern as <see cref="TargetStore"/>: the Units page and the map markers are
/// views of this store and look units up by <c>Id</c>. IDs are unique and never reused during a session.
/// Used from the UI thread; the lock only guards against accidental use from other threads.
/// </summary>
public sealed class UnitStore
{
    private readonly object _lock = new();
    private readonly List<UnitInfo> _units = new();
    private int _nextId = 1;

    /// <summary>Raised after any add / remove, and after a new <see cref="LastCalculation"/>.</summary>
    public event Action Changed;

    public IReadOnlyList<UnitInfo> Units { get { lock (_lock) return _units.ToArray(); } }

    public UnitInfo FindUnit(int id) { lock (_lock) return _units.FirstOrDefault(u => u.Id == id); }

    public UnitInfo AddUnit(double lat, double lon, string name = null, DateTime? time = null,
                            TargetSource source = TargetSource.CameraGeo)
    {
        if (!TargetStore.IsValidPosition(lat, lon)) throw new ArgumentOutOfRangeException(nameof(lat), "Invalid unit position.");
        UnitInfo u;
        lock (_lock)
        {
            int id = _nextId++;
            u = new UnitInfo
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? $"Unit {id}" : name.Trim(),
                Latitude = lat,
                Longitude = lon,
                DateTime = time ?? DateTime.Now,
                Source = source,
            };
            _units.Add(u);
        }
        Changed?.Invoke();
        return u;
    }

    /// <summary>Changes name / position of an existing unit. Returns false if the ID is unknown.</summary>
    public bool UpdateUnit(int id, string name, double lat, double lon)
    {
        if (!TargetStore.IsValidPosition(lat, lon)) throw new ArgumentOutOfRangeException(nameof(lat), "Invalid unit position.");
        lock (_lock)
        {
            var u = _units.FirstOrDefault(x => x.Id == id);
            if (u == null) return false;
            if (!string.IsNullOrWhiteSpace(name)) u.Name = name.Trim();
            u.Latitude = lat;
            u.Longitude = lon;
        }
        Changed?.Invoke();
        return true;
    }

    public bool RemoveUnit(int id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _units.RemoveAll(u => u.Id == id) > 0;
            if (removed && LastCalculation?.UnitId == id) LastCalculation = null;
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// The most recent Unit / Target / Splash calculation, so other views (the Correction section of the
    /// Targets page) can show it. Null until the operator presses Calculate on the Units page.
    /// </summary>
    public UnitFireCalculation LastCalculation { get; private set; }

    public void SetLastCalculation(UnitFireCalculation calculation)
    {
        LastCalculation = calculation;
        Changed?.Invoke();
    }
}
