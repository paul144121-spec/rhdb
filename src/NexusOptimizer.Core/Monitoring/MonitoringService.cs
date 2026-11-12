using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NexusOptimizer.Core.Monitoring;

public enum MonitoringMode
{
    /// <summary>Anwendung sichtbar: Abtastung im eingestellten Intervall.</summary>
    Foreground,
    /// <summary>Anwendung minimiert oder im Hintergrund: stark reduzierte Abtastung.</summary>
    Background,
    /// <summary>Ein Spiel läuft: reduzierte Abtastung, sofern keine Sitzungsaufzeichnung angefordert wurde.</summary>
    Gaming,
}

public sealed record MonitoringOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PersistInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
    public int LiveSamples { get; init; } = 300;
    public IReadOnlySet<MetricId>? PersistedMetrics { get; init; }

    public static TimeSpan ClampInterval(TimeSpan interval)
        => interval < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : interval > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : interval;
}

/// <summary>
/// Zentrale Messschleife. Läuft auf einem Hintergrundthread, passt die Abtastrate an den Modus an (adaptives Polling),
/// hält Live-Ringpuffer vor und schreibt verdichtete Mittelwerte in die Historie.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly IMetricsCollector _collector;
    private readonly IMetricHistoryStore? _history;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Dictionary<MetricId, RingBuffer<double>> _live = new();
    private readonly Dictionary<MetricId, (double Sum, int Count)> _pending = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private MonitoringOptions _options = new();
    private DateTimeOffset _lastPersist;
    private DateTimeOffset _lastPrune;
    private int _consecutiveFailures;

    public MonitoringService(IMetricsCollector collector, IMetricHistoryStore? history = null, ILogger<MonitoringService>? logger = null, TimeProvider? time = null)
    {
        _collector = collector;
        _history = history;
        _logger = logger ?? NullLogger<MonitoringService>.Instance;
        _time = time ?? TimeProvider.System;
        foreach (var definition in MetricCatalog.All) _live[definition.Id] = new RingBuffer<double>(_options.LiveSamples);
    }

    public event EventHandler<SystemMetrics>? MetricsUpdated;
    /// <summary>Wird ausgelöst, wenn die Erfassung wiederholt fehlschlägt („Hardware monitoring failure“).</summary>
    public event EventHandler<string>? CollectionFailed;

    public SystemMetrics? Latest { get; private set; }
    public MonitoringMode Mode { get; set; } = MonitoringMode.Foreground;
    public bool IsRunning => _loop is { IsCompleted: false };
    public IReadOnlyDictionary<MetricId, string> UnavailableReasons => _collector.UnavailableReasons;

    public MonitoringOptions Options
    {
        get { lock (_gate) return _options; }
        set { lock (_gate) _options = value with { Interval = MonitoringOptions.ClampInterval(value.Interval) }; }
    }

    /// <summary>Wirksames Abtastintervall für den aktuellen Modus.</summary>
    public TimeSpan EffectiveInterval
    {
        get
        {
            var interval = Options.Interval;
            return Mode switch
            {
                MonitoringMode.Background => TimeSpan.FromTicks(Math.Max(interval.Ticks * 5, TimeSpan.FromSeconds(5).Ticks)),
                MonitoringMode.Gaming => TimeSpan.FromTicks(Math.Max(interval.Ticks * 2, TimeSpan.FromSeconds(2).Ticks)),
                _ => interval,
            };
        }
    }

    public double[] GetLive(MetricId metric) => _live[metric].ToArray();

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _lastPersist = _lastPrune = _time.GetUtcNow();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => RunAsync(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        FlushPending();
        _cts.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SampleOnce();
            try { await Task.Delay(EffectiveInterval, _time, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Führt genau eine Messung aus. Öffentlich für Tests und für „Jetzt aktualisieren“.</summary>
    public SystemMetrics? SampleOnce()
    {
        SystemMetrics metrics;
        try
        {
            metrics = _collector.Collect();
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "Messwerterfassung fehlgeschlagen ({Count} in Folge).", _consecutiveFailures);
            if (_consecutiveFailures == 5) CollectionFailed?.Invoke(this, "Die Messwerterfassung ist wiederholt fehlgeschlagen: " + ex.Message);
            return null;
        }

        Latest = metrics;
        lock (_gate)
        {
            foreach (var definition in MetricCatalog.All)
            {
                var value = definition.Read(metrics);
                _live[definition.Id].Add(value ?? double.NaN);
                if (value is null || double.IsNaN(value.Value)) continue;
                var (sum, count) = _pending.GetValueOrDefault(definition.Id);
                _pending[definition.Id] = (sum + value.Value, count + 1);
            }
        }

        var now = _time.GetUtcNow();
        if (now - _lastPersist >= Options.PersistInterval)
        {
            FlushPending();
            _lastPersist = now;
        }
        if (_history is not null && now - _lastPrune >= TimeSpan.FromHours(1))
        {
            try { _history.Prune(now - Options.Retention); }
            catch (Exception ex) { _logger.LogWarning(ex, "Bereinigung der Messwerthistorie fehlgeschlagen."); }
            _lastPrune = now;
        }

        try { MetricsUpdated?.Invoke(this, metrics); }
        catch (Exception ex) { _logger.LogError(ex, "Fehler in einem Messwert-Abonnenten."); }
        return metrics;
    }

    private void FlushPending()
    {
        if (_history is null) return;
        Dictionary<MetricId, double> averages;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            var filter = _options.PersistedMetrics;
            averages = _pending.Where(p => p.Value.Count > 0 && (filter is null || filter.Contains(p.Key))).ToDictionary(p => p.Key, p => p.Value.Sum / p.Value.Count);
            _pending.Clear();
        }
        if (averages.Count == 0) return;
        try { _history.Append(_time.GetUtcNow(), averages); }
        catch (Exception ex) { _logger.LogWarning(ex, "Messwerthistorie konnte nicht geschrieben werden."); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts?.Dispose();
        _collector.Dispose();
    }
}
