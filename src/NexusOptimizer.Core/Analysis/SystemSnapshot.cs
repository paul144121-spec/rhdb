using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Startup;

namespace NexusOptimizer.Core.Analysis;

public sealed record MetricSummary(double? Average, double? Peak, int Samples)
{
    public static MetricSummary Empty { get; } = new(null, null, 0);

    public static MetricSummary From(IEnumerable<double> values)
    {
        var valid = values.Where(v => !double.IsNaN(v)).ToList();
        return valid.Count == 0 ? Empty : new MetricSummary(valid.Average(), valid.Max(), valid.Count);
    }
}

/// <summary>Verdichteter Systemzustand als Eingabe für Regelwerk, KI-Kontext und Ein-Klick-Optimierung. Enthält nur real gemessene Daten.</summary>
public sealed record SystemSnapshot
{
    public required SystemInfo Info { get; init; }
    public required SystemContext Context { get; init; }
    public SystemMetrics? Latest { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public MetricSummary Cpu { get; init; } = MetricSummary.Empty;
    public MetricSummary Memory { get; init; } = MetricSummary.Empty;
    public MetricSummary Commit { get; init; } = MetricSummary.Empty;
    public MetricSummary DiskActive { get; init; } = MetricSummary.Empty;
    public MetricSummary DiskLatency { get; init; } = MetricSummary.Empty;
    public MetricSummary Gpu { get; init; } = MetricSummary.Empty;
    public MetricSummary GpuMemory { get; init; } = MetricSummary.Empty;
    public MetricSummary Temperature { get; init; } = MetricSummary.Empty;
    public MetricSummary PerformanceLimit { get; init; } = MetricSummary.Empty;

    public IReadOnlyList<StartupEntry> StartupEntries { get; init; } = Array.Empty<StartupEntry>();
    public IReadOnlyList<ProcessSnapshot> TopCpuProcesses { get; init; } = Array.Empty<ProcessSnapshot>();
    public IReadOnlyList<ProcessSnapshot> TopMemoryProcesses { get; init; } = Array.Empty<ProcessSnapshot>();
    public IReadOnlyList<PowerPlanInfo> PowerPlans { get; init; } = Array.Empty<PowerPlanInfo>();
    /// <summary>Aktions-ID → liegt der Zielzustand bereits vor? (nur parameterlose Aktionen)</summary>
    public IReadOnlyDictionary<string, bool?> ActionStates { get; init; } = new Dictionary<string, bool?>();
    /// <summary>Vom Benutzer abgelehnte oder nach Messung als nachteilig eingestufte Aktionen.</summary>
    public IReadOnlySet<string> DeclinedActions { get; init; } = new HashSet<string>();

    public PowerPlanInfo? ActivePowerPlan => PowerPlans.FirstOrDefault(p => p.IsActive);
    public bool ThermalLimitObserved => PerformanceLimit.Average is < 97 || Temperature.Peak is >= 95;
    public TimeSpan Uptime => Latest?.Uptime ?? TimeSpan.Zero;
}

public enum FeedbackKind
{
    Accepted,
    Declined,
    VerificationFailed,
    Regressed,
    Helpful,
}

/// <summary>Speichert ausdrückliche Rückmeldungen und beobachtete Ergebnisse („KI-Lernmodus“). Ändert niemals Risikofreigaben.</summary>
public interface IFeedbackStore
{
    void Record(string actionId, FeedbackKind kind, string? note = null);
    /// <summary>Aktionen, die nicht erneut vorgeschlagen werden sollen (abgelehnt oder nachweislich nachteilig).</summary>
    IReadOnlySet<string> GetDeclined();
    IReadOnlyDictionary<string, int> GetCounts(FeedbackKind kind);
    void Clear();
}

public sealed class InMemoryFeedbackStore : IFeedbackStore
{
    private readonly List<(string ActionId, FeedbackKind Kind)> _items = new();

    public void Record(string actionId, FeedbackKind kind, string? note = null) { lock (_items) _items.Add((actionId, kind)); }

    public IReadOnlySet<string> GetDeclined()
    {
        lock (_items)
        {
            // Die jeweils letzte Rückmeldung zählt: Ein späteres „Angenommen“ hebt ein früheres „Abgelehnt“ auf.
            return _items.GroupBy(i => i.ActionId).Where(g => g.Last().Kind is FeedbackKind.Declined or FeedbackKind.Regressed).Select(g => g.Key).ToHashSet();
        }
    }

    public IReadOnlyDictionary<string, int> GetCounts(FeedbackKind kind)
    {
        lock (_items) return _items.Where(i => i.Kind == kind).GroupBy(i => i.ActionId).ToDictionary(g => g.Key, g => g.Count());
    }

    public void Clear() { lock (_items) _items.Clear(); }
}

/// <summary>Erstellt Momentaufnahmen aus den echten Datenquellen des Systems.</summary>
public sealed class SystemSnapshotService
{
    private readonly IHardwareInfoProvider _hardware;
    private readonly MonitoringService _monitoring;
    private readonly IStartupInventory _startup;
    private readonly IProcessMonitor _processes;
    private readonly IPowerPolicy _power;
    private readonly ActionCatalog _catalog;
    private readonly IFeedbackStore _feedback;

    public SystemSnapshotService(IHardwareInfoProvider hardware, MonitoringService monitoring, IStartupInventory startup, IProcessMonitor processes,
        IPowerPolicy power, ActionCatalog catalog, IFeedbackStore feedback)
    {
        _hardware = hardware;
        _monitoring = monitoring;
        _startup = startup;
        _processes = processes;
        _power = power;
        _catalog = catalog;
        _feedback = feedback;
    }

    public async Task<SystemContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var info = await _hardware.GetSystemInfoAsync(false, cancellationToken).ConfigureAwait(false);
        var latest = _monitoring.Latest;
        var limit = MetricSummary.From(_monitoring.GetLive(MetricId.CpuPerformanceLimit));
        return info.ToContext(_hardware.IsProcessElevated, latest?.OnAcPower == false, limit.Average is < 97);
    }

    public async Task<SystemSnapshot> CaptureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Hardware wird erkannt …");
        var info = await _hardware.GetSystemInfoAsync(false, cancellationToken).ConfigureAwait(false);

        progress?.Report("Leistungsdaten werden ausgewertet …");
        var latest = _monitoring.Latest ?? _monitoring.SampleOnce();
        var limit = MetricSummary.From(_monitoring.GetLive(MetricId.CpuPerformanceLimit));
        var context = info.ToContext(_hardware.IsProcessElevated, latest?.OnAcPower == false, limit.Average is < 97);

        progress?.Report("Autostart und Prozesse werden analysiert …");
        var (startup, processes, plans) = await Task.Run(() =>
        {
            var s = Safe(() => _startup.GetEntries(), Array.Empty<StartupEntry>());
            var p = Safe(() => _processes.GetProcesses(), Array.Empty<ProcessSnapshot>());
            var pl = Safe(() => _power.GetPlans(), Array.Empty<PowerPlanInfo>());
            return (s, p, pl);
        }, cancellationToken).ConfigureAwait(false);

        progress?.Report("Windows-Konfiguration wird gelesen …");
        var states = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var action in _catalog.All.Where(a => a.Metadata.Parameters.Count == 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var support = await action.IsSupportedAsync(context, ActionParameters.Empty, cancellationToken).ConfigureAwait(false);
                if (!support.IsSupported) continue;
                var state = await action.DetectCurrentStateAsync(ActionParameters.Empty, cancellationToken).ConfigureAwait(false);
                states[action.Metadata.Id] = state.IsAvailable ? state.IsApplied : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                states[action.Metadata.Id] = null;
            }
        }

        return new SystemSnapshot
        {
            Info = info,
            Context = context,
            Latest = latest,
            Cpu = MetricSummary.From(_monitoring.GetLive(MetricId.CpuTotal)),
            Memory = MetricSummary.From(_monitoring.GetLive(MetricId.MemoryUsedPercent)),
            Commit = MetricSummary.From(_monitoring.GetLive(MetricId.CommitPercent)),
            DiskActive = MetricSummary.From(_monitoring.GetLive(MetricId.DiskActive)),
            DiskLatency = MetricSummary.From(_monitoring.GetLive(MetricId.DiskLatency)),
            Gpu = MetricSummary.From(_monitoring.GetLive(MetricId.GpuUsage)),
            GpuMemory = MetricSummary.From(_monitoring.GetLive(MetricId.GpuMemoryPercent)),
            Temperature = MetricSummary.From(_monitoring.GetLive(MetricId.ThermalZone)),
            PerformanceLimit = limit,
            StartupEntries = startup,
            TopCpuProcesses = processes.Where(p => p.ProcessId > 4).OrderByDescending(p => p.CpuPercent).Take(8).ToList(),
            TopMemoryProcesses = processes.Where(p => p.ProcessId > 4).OrderByDescending(p => p.WorkingSetBytes).Take(8).ToList(),
            PowerPlans = plans,
            ActionStates = states,
            DeclinedActions = _feedback.GetDeclined(),
        };
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }
}
