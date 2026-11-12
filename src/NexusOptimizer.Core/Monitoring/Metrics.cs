namespace NexusOptimizer.Core.Monitoring;

/// <summary>
/// Eine Momentaufnahme aller Messwerte. Jeder Wert ist nullable: null bedeutet „auf diesem System nicht verfügbar“ –
/// es werden niemals Ersatz- oder Schätzwerte eingetragen.
/// </summary>
public sealed record SystemMetrics
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    // CPU
    public double? CpuTotalPercent { get; init; }
    public IReadOnlyList<double> CpuPerCorePercent { get; init; } = Array.Empty<double>();
    public double? CpuFrequencyMhz { get; init; }
    /// <summary>„% Performance Limit“: Werte unter 100 zeigen an, dass Firmware/Windows die Prozessorleistung begrenzt (z. B. thermisch).</summary>
    public double? CpuPerformanceLimitPercent { get; init; }
    /// <summary>ACPI-Thermalzone – nicht zwingend die CPU-Kerntemperatur.</summary>
    public double? ThermalZoneCelsius { get; init; }
    public double? CpuPackageCelsius { get; init; }
    public double? ProcessorQueueLength { get; init; }
    public double? DpcTimePercent { get; init; }
    public double? InterruptTimePercent { get; init; }

    // GPU
    public double? GpuUsagePercent { get; init; }
    public double? GpuDedicatedUsedBytes { get; init; }
    public double? GpuDedicatedTotalBytes { get; init; }
    public double? GpuSharedUsedBytes { get; init; }
    public double? GpuTemperatureCelsius { get; init; }
    public double? GpuPowerWatts { get; init; }
    public double? GpuClockMhz { get; init; }

    // Arbeitsspeicher
    public ulong MemoryTotalBytes { get; init; }
    public ulong MemoryAvailableBytes { get; init; }
    public ulong CommitUsedBytes { get; init; }
    public ulong CommitLimitBytes { get; init; }
    public double? StandbyCacheBytes { get; init; }
    public double? ModifiedPageBytes { get; init; }
    public double? CachedBytes { get; init; }

    // Datenträger
    public double? DiskActivePercent { get; init; }
    public double? DiskReadBytesPerSecond { get; init; }
    public double? DiskWriteBytesPerSecond { get; init; }
    public double? DiskQueueLength { get; init; }
    public double? DiskLatencyMs { get; init; }

    // Netzwerk
    public double? NetworkDownBytesPerSecond { get; init; }
    public double? NetworkUpBytesPerSecond { get; init; }
    public double? LatencyMs { get; init; }

    // Energie
    public int? BatteryPercent { get; init; }
    public bool? OnAcPower { get; init; }
    /// <summary>Positiv = Laden, negativ = Entladen (Milliwatt).</summary>
    public double? BatteryRateMilliwatts { get; init; }

    // System
    public int? ProcessCount { get; init; }
    public int? ThreadCount { get; init; }
    public int? HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }

    public double? MemoryUsedPercent => MemoryTotalBytes == 0 ? null : 100.0 * (MemoryTotalBytes - Math.Min(MemoryAvailableBytes, MemoryTotalBytes)) / MemoryTotalBytes;
    public double? CommitPercent => CommitLimitBytes == 0 ? null : 100.0 * CommitUsedBytes / CommitLimitBytes;
    public double? GpuMemoryPercent => GpuDedicatedTotalBytes is > 0 && GpuDedicatedUsedBytes is not null ? 100.0 * GpuDedicatedUsedBytes / GpuDedicatedTotalBytes : null;
    public bool IsThermallyLimited => CpuPerformanceLimitPercent is < 99;
}

/// <summary>Sammelt Messwerte aus dem Betriebssystem. Die Implementierung muss mit fehlenden Sensoren umgehen können.</summary>
public interface IMetricsCollector : IDisposable
{
    SystemMetrics Collect();
    /// <summary>Kurzbeschreibung je Messgröße, warum sie nicht verfügbar ist (für die Anzeige).</summary>
    IReadOnlyDictionary<MetricId, string> UnavailableReasons { get; }
}

public enum MetricId
{
    CpuTotal,
    CpuFrequency,
    CpuPerformanceLimit,
    ThermalZone,
    GpuUsage,
    GpuMemoryUsed,
    GpuMemoryPercent,
    GpuTemperature,
    MemoryUsedPercent,
    MemoryAvailable,
    CommitPercent,
    DiskActive,
    DiskRead,
    DiskWrite,
    DiskLatency,
    NetworkDown,
    NetworkUp,
    Latency,
    BatteryPercent,
    ProcessCount,
    ThreadCount,
}

public enum MetricUnit
{
    Percent,
    Megahertz,
    Celsius,
    Bytes,
    BytesPerSecond,
    Milliseconds,
    Count,
}

public sealed record MetricDefinition(MetricId Id, string Name, MetricUnit Unit, Func<SystemMetrics, double?> Read)
{
    public string Format(double? value)
    {
        if (value is null || double.IsNaN(value.Value)) return "Nicht verfügbar";
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        return Unit switch
        {
            MetricUnit.Percent => value.Value.ToString("0.0", de) + " %",
            MetricUnit.Megahertz => (value.Value / 1000).ToString("0.00", de) + " GHz",
            MetricUnit.Celsius => value.Value.ToString("0", de) + " °C",
            MetricUnit.Bytes => Storage.ByteFormat.Format(value.Value),
            MetricUnit.BytesPerSecond => Storage.ByteFormat.Rate(value.Value),
            MetricUnit.Milliseconds => value.Value.ToString("0.0", de) + " ms",
            _ => value.Value.ToString("0", de),
        };
    }
}

public static class MetricCatalog
{
    public static IReadOnlyList<MetricDefinition> All { get; } = new[]
    {
        new MetricDefinition(MetricId.CpuTotal, "CPU-Auslastung", MetricUnit.Percent, m => m.CpuTotalPercent),
        new MetricDefinition(MetricId.CpuFrequency, "CPU-Takt", MetricUnit.Megahertz, m => m.CpuFrequencyMhz),
        new MetricDefinition(MetricId.CpuPerformanceLimit, "CPU-Leistungsgrenze", MetricUnit.Percent, m => m.CpuPerformanceLimitPercent),
        new MetricDefinition(MetricId.ThermalZone, "Temperatur (ACPI-Thermalzone)", MetricUnit.Celsius, m => m.CpuPackageCelsius ?? m.ThermalZoneCelsius),
        new MetricDefinition(MetricId.GpuUsage, "GPU-Auslastung", MetricUnit.Percent, m => m.GpuUsagePercent),
        new MetricDefinition(MetricId.GpuMemoryUsed, "Grafikspeicher belegt", MetricUnit.Bytes, m => m.GpuDedicatedUsedBytes),
        new MetricDefinition(MetricId.GpuMemoryPercent, "Grafikspeicher-Auslastung", MetricUnit.Percent, m => m.GpuMemoryPercent),
        new MetricDefinition(MetricId.GpuTemperature, "GPU-Temperatur", MetricUnit.Celsius, m => m.GpuTemperatureCelsius),
        new MetricDefinition(MetricId.MemoryUsedPercent, "RAM-Auslastung", MetricUnit.Percent, m => m.MemoryUsedPercent),
        new MetricDefinition(MetricId.MemoryAvailable, "Verfügbarer RAM", MetricUnit.Bytes, m => m.MemoryTotalBytes == 0 ? null : m.MemoryAvailableBytes),
        new MetricDefinition(MetricId.CommitPercent, "Zugesicherter Speicher", MetricUnit.Percent, m => m.CommitPercent),
        new MetricDefinition(MetricId.DiskActive, "Datenträgeraktivität", MetricUnit.Percent, m => m.DiskActivePercent),
        new MetricDefinition(MetricId.DiskRead, "Datenträger lesen", MetricUnit.BytesPerSecond, m => m.DiskReadBytesPerSecond),
        new MetricDefinition(MetricId.DiskWrite, "Datenträger schreiben", MetricUnit.BytesPerSecond, m => m.DiskWriteBytesPerSecond),
        new MetricDefinition(MetricId.DiskLatency, "Datenträger-Antwortzeit", MetricUnit.Milliseconds, m => m.DiskLatencyMs),
        new MetricDefinition(MetricId.NetworkDown, "Download", MetricUnit.BytesPerSecond, m => m.NetworkDownBytesPerSecond),
        new MetricDefinition(MetricId.NetworkUp, "Upload", MetricUnit.BytesPerSecond, m => m.NetworkUpBytesPerSecond),
        new MetricDefinition(MetricId.Latency, "Netzwerklatenz", MetricUnit.Milliseconds, m => m.LatencyMs),
        new MetricDefinition(MetricId.BatteryPercent, "Akkustand", MetricUnit.Percent, m => m.BatteryPercent),
        new MetricDefinition(MetricId.ProcessCount, "Prozesse", MetricUnit.Count, m => m.ProcessCount),
        new MetricDefinition(MetricId.ThreadCount, "Threads", MetricUnit.Count, m => m.ThreadCount),
    };

    private static readonly Dictionary<MetricId, MetricDefinition> ById = All.ToDictionary(d => d.Id);
    public static MetricDefinition Get(MetricId id) => ById[id];
}

public readonly record struct MetricPoint(DateTimeOffset Timestamp, double Value);

/// <summary>Dauerhafte, verdichtete Messwerthistorie mit konfigurierbarer Aufbewahrung.</summary>
public interface IMetricHistoryStore
{
    void Append(DateTimeOffset timestamp, IReadOnlyDictionary<MetricId, double> values);
    IReadOnlyList<MetricPoint> Query(MetricId metric, DateTimeOffset from, DateTimeOffset to, int maxPoints = 600);
    int Prune(DateTimeOffset olderThan);
    void DeleteAll();
}

/// <summary>Ringpuffer fester Größe für Live-Diagramme; threadsicher für einen Schreiber und mehrere Leser.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly object _gate = new();
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get { lock (_gate) return _count; } }

    public void Add(T item)
    {
        lock (_gate)
        {
            _items[(_start + _count) % _items.Length] = item;
            if (_count < _items.Length) _count++;
            else _start = (_start + 1) % _items.Length;
        }
    }

    public T[] ToArray()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (var i = 0; i < _count; i++) result[i] = _items[(_start + i) % _items.Length];
            return result;
        }
    }

    public void Clear() { lock (_gate) { _start = 0; _count = 0; } }
}
