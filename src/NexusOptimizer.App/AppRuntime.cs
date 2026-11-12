using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Automation;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Gaming;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Logging;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Orchestration;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Windows.Monitoring;
using NexusOptimizer.Windows.SystemAccess;

namespace NexusOptimizer.App;

/// <summary>
/// Unbeaufsichtigter Lauf des KI-Automatikmodus. Er verwendet ausschließlich die deterministischen Regel-Empfehlungen und
/// die Quelle „AiAuto“ – der Planer lässt dafür nur ausdrücklich freigegebene, risikoarme und umkehrbare Aktionen zu.
/// Texte eines Sprachmodells werden hier nie ausgeführt.
/// </summary>
public sealed class AutoPilot
{
    private readonly SettingsService _settings;
    private readonly SystemSnapshotService _snapshots;
    private readonly RuleBasedAdvisor _advisor;
    private readonly OptimizationPlanner _planner;
    private readonly OptimizationExecutor _executor;
    private readonly NotificationCenter _notifications;
    private readonly ILogger<AutoPilot> _logger;

    public AutoPilot(SettingsService settings, SystemSnapshotService snapshots, RuleBasedAdvisor advisor, OptimizationPlanner planner, OptimizationExecutor executor,
        NotificationCenter notifications, ILogger<AutoPilot> logger)
    {
        _settings = settings;
        _snapshots = snapshots;
        _advisor = advisor;
        _planner = planner;
        _executor = executor;
        _notifications = notifications;
        _logger = logger;
    }

    public DateTimeOffset? LastRun { get; private set; }
    public string LastResult { get; private set; } = "Noch kein Lauf in dieser Sitzung.";
    public bool IsEnabled => _settings.Current is { AiAutoEnabled: true, AiMode: AiMode.Auto };

    public async Task<string> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!IsEnabled) return LastResult = "Der Automatikmodus ist ausgeschaltet.";

        try
        {
            var snapshot = await _snapshots.CaptureAsync(null, cancellationToken).ConfigureAwait(false);
            var requests = _advisor.Analyze(snapshot, settings.PrimaryGoal).SelectMany(r => r.Actions).DistinctBy(r => r.Key).ToList();
            LastRun = DateTimeOffset.Now;
            if (requests.Count == 0) return LastResult = "Analyse abgeschlossen – keine Maßnahme nötig.";

            var plan = await _planner.BuildAsync(requests, settings.ToRiskPolicy(), snapshot.Context, ExecutionSource.AiAuto, cancellationToken).ConfigureAwait(false);
            if (plan.IsEmpty)
                return LastResult = $"Analyse abgeschlossen – {requests.Count} Empfehlung(en) erfordern Ihre Bestätigung und wurden nicht automatisch ausgeführt.";

            var result = await _executor.ExecuteAsync(plan, new ExecutionOptions { SourceLabel = "KI-Automatikmodus" }, cancellationToken).ConfigureAwait(false);
            if (result.Busy) return LastResult = "Übersprungen – es lief bereits ein anderer Vorgang.";

            LastResult = $"{result.SucceededCount} von {result.Steps.Count} automatisch zulässigen Änderung(en) angewendet und verifiziert.";
            _notifications.Publish(result.FailedCount == 0 ? NotificationKind.AiRecommendation : NotificationKind.OptimizationFailed, "KI-Automatikmodus", LastResult,
                result.FailedCount == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning, "history");
            return LastResult;
        }
        catch (OperationCanceledException)
        {
            return LastResult = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatiklauf fehlgeschlagen.");
            return LastResult = "Fehlgeschlagen: " + ex.Message;
        }
    }
}

/// <summary>Hintergrundabläufe der Anwendung: Überwachung, Warnungen, Automatisierung, Spielerkennung und Reaktion auf Einstellungsänderungen.</summary>
public sealed class AppRuntime : IDisposable
{
    private static readonly TimeSpan AutoPilotInterval = TimeSpan.FromMinutes(30);

    private readonly SettingsService _settings;
    private readonly MonitoringService _monitoring;
    private readonly WindowsMetricsCollector _collector;
    private readonly AlertEngine _alerts;
    private readonly NotificationCenter _notifications;
    private readonly ToastService _toasts;
    private readonly RollbackService _rollback;
    private readonly AutomationEngine _automation;
    private readonly AutomationEventPump _pump;
    private readonly GamingSessionManager _gaming;
    private readonly IHardwareInfoProvider _hardware;
    private readonly SystemInfoCache _infoCache;
    private readonly ThemeService _theme;
    private readonly FileLoggerProvider _logProvider;
    private readonly AutoPilot _autoPilot;
    private readonly StartupOptions _options;
    private readonly ILogger<AppRuntime> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private DispatcherTimer? _slowTimer;
    private DateTimeOffset _lastAutoPilot = DateTimeOffset.Now;
    private DateTimeOffset _lastCollectionFailure = DateTimeOffset.MinValue;
    private int _slowBusy;
    private bool _windowVisible = true;

    public AppRuntime(SettingsService settings, MonitoringService monitoring, WindowsMetricsCollector collector, AlertEngine alerts, NotificationCenter notifications, ToastService toasts,
        RollbackService rollback, AutomationEngine automation, AutomationEventPump pump, GamingSessionManager gaming, IHardwareInfoProvider hardware, SystemInfoCache infoCache,
        ThemeService theme, FileLoggerProvider logProvider, AutoPilot autoPilot, StartupOptions options, ILogger<AppRuntime> logger)
    {
        _settings = settings;
        _monitoring = monitoring;
        _collector = collector;
        _alerts = alerts;
        _notifications = notifications;
        _toasts = toasts;
        _rollback = rollback;
        _automation = automation;
        _pump = pump;
        _gaming = gaming;
        _hardware = hardware;
        _infoCache = infoCache;
        _theme = theme;
        _logProvider = logProvider;
        _autoPilot = autoPilot;
        _options = options;
        _logger = logger;
    }

    /// <summary>Beim letzten Lauf unterbrochene Änderungen (für den Hinweis nach dem Start).</summary>
    public IReadOnlyList<TransactionRecord> Interrupted { get; private set; } = Array.Empty<TransactionRecord>();

    public void Start()
    {
        ApplySettings(_settings.Current, initial: true);
        _settings.Changed += (_, settings) => Ui.Post(() => ApplySettings(settings, initial: false));

        try
        {
            Interrupted = _rollback.RecoverInterrupted();
            if (Interrupted.Count > 0)
            {
                _notifications.Publish(NotificationKind.InterruptedOperation, "Unterbrochener Vorgang erkannt",
                    $"{Interrupted.Count} Änderung(en) wurden beim letzten Lauf nicht abgeschlossen. Der Verlauf zeigt den Stand; gesicherte Zustände lassen sich dort wiederherstellen.",
                    NotificationSeverity.Warning, "history");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Die Prüfung auf unterbrochene Vorgänge ist fehlgeschlagen.");
        }

        _monitoring.MetricsUpdated += OnMetrics;
        _monitoring.CollectionFailed += OnCollectionFailed;
        _gaming.SessionStarted += OnGameStarted;
        _gaming.SessionEnded += OnGameEnded;
        _automation.RuleExecuted += OnRuleExecuted;
        _monitoring.Start();

        _ = LoadSystemInfoAsync();

        if (!_options.IsSmokeTest)
        {
            _slowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _slowTimer.Tick += (_, _) => _ = SlowTickAsync();
            _slowTimer.Start();
        }
    }

    private async Task LoadSystemInfoAsync()
    {
        try
        {
            var info = await _hardware.GetSystemInfoAsync(false, _shutdown.Token).ConfigureAwait(false);
            _infoCache.Current = info;
            _collector.CpuBaseFrequencyMhz = info.CpuMaxClockMhz;
            _collector.GpuDedicatedTotalBytes = info.Gpus.Where(g => !g.IsIntegrated).Select(g => g.DedicatedMemoryBytes).FirstOrDefault(b => b is > 0)
                                                ?? info.Gpus.Select(g => g.DedicatedMemoryBytes).FirstOrDefault(b => b is > 0);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Die Hardwareerkennung ist fehlgeschlagen.");
        }
    }

    private void ApplySettings(AppSettings settings, bool initial)
    {
        _theme.Apply(settings);
        _notifications.Enabled = settings.NotificationsEnabled;
        _logProvider.MinimumLevel = Enum.TryParse<LogLevel>(settings.LogLevel, out var level) ? level : LogLevel.Information;

        _monitoring.Options = new MonitoringOptions
        {
            Interval = MonitoringOptions.ClampInterval(TimeSpan.FromSeconds(settings.MonitoringIntervalSeconds)),
            Retention = TimeSpan.FromDays(settings.HistoryRetentionDays),
            // Leere Menge: es wird nichts dauerhaft gespeichert, die Live-Anzeige bleibt erhalten.
            PersistedMetrics = settings.PersistHistory ? null : new HashSet<MetricId>(),
        };
        _collector.NvidiaSensorsEnabled = settings.NvidiaSensorsEnabled;
        _collector.LatencyEndpoints = settings.LatencyProbeEnabled ? settings.LatencyEndpoints : Array.Empty<string>();
        _alerts.Rules = settings.AlertRules;
        _automation.Enabled = settings.AutomationEnabled;
        _gaming.AutoModeEnabled = settings.AutoGamingMode;

        if (!initial && !_options.IsSmokeTest)
        {
            try
            {
                var executable = Environment.ProcessPath;
                if (executable is not null && ApplicationAutostart.IsEnabled() != settings.StartWithWindows)
                    ApplicationAutostart.Set(settings.StartWithWindows, executable, settings.StartMinimized);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Der Autostart-Eintrag der Anwendung konnte nicht aktualisiert werden.");
            }
        }
    }

    /// <summary>Vom Hauptfenster gemeldet: sichtbar oder minimiert/im Hintergrund. Im Hintergrund wird deutlich seltener gemessen.</summary>
    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        UpdateMonitoringMode();
    }

    private void UpdateMonitoringMode()
        => _monitoring.Mode = _gaming.Current is not null ? MonitoringMode.Gaming : _windowVisible ? MonitoringMode.Foreground : MonitoringMode.Background;

    private void OnMetrics(object? sender, SystemMetrics metrics)
    {
        try
        {
            foreach (var alert in _alerts.Evaluate(metrics))
            {
                var severity = alert.IsRecovery ? NotificationSeverity.Info : NotificationSeverity.Warning;
                var kind = alert.Rule.Metric is MetricId.ThermalZone or MetricId.GpuTemperature ? NotificationKind.HighTemperature : NotificationKind.MonitoringAlert;
                _notifications.Publish(kind, alert.Rule.Name, alert.Message, severity, "monitor");
                if (!alert.IsRecovery && _settings.Current.NotificationsEnabled) _toasts.Show(alert.Rule.Name, alert.Message, severity, "monitor");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Die Auswertung der Warnregeln ist fehlgeschlagen.");
        }

        if (_automation.Enabled) _ = PumpAsync(metrics);
    }

    private async Task PumpAsync(SystemMetrics metrics)
    {
        try { await _pump.OnMetricsAsync(metrics, _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Die Automatisierung konnte ein Messereignis nicht verarbeiten."); }
    }

    private void OnCollectionFailed(object? sender, string message)
    {
        // Höchstens einmal je zehn Minuten melden – ein dauerhaft fehlender Zähler soll keine Flut auslösen.
        var now = DateTimeOffset.Now;
        if (now - _lastCollectionFailure < TimeSpan.FromMinutes(10)) return;
        _lastCollectionFailure = now;
        _notifications.Publish(NotificationKind.MonitoringFailure, "Messwerterfassung gestört", message, NotificationSeverity.Warning, "monitor");
    }

    private async Task SlowTickAsync()
    {
        if (Interlocked.Exchange(ref _slowBusy, 1) == 1) return;
        try
        {
            await Task.Run(async () =>
            {
                if (_automation.Enabled) await _pump.PollProcessesAsync(_shutdown.Token).ConfigureAwait(false);
                await _gaming.TickAsync(_shutdown.Token).ConfigureAwait(false);
            }).ConfigureAwait(true);

            if (_autoPilot.IsEnabled && DateTimeOffset.Now - _lastAutoPilot >= AutoPilotInterval)
            {
                _lastAutoPilot = DateTimeOffset.Now;
                await Task.Run(() => _autoPilot.RunOnceAsync(_shutdown.Token)).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ein Hintergrundlauf ist fehlgeschlagen.");
        }
        finally
        {
            Interlocked.Exchange(ref _slowBusy, 0);
        }
    }

    private void OnGameStarted(object? sender, ActiveGameSession session)
    {
        Ui.Post(UpdateMonitoringMode);
        _notifications.Publish(NotificationKind.ProfileActivated, "Spiel erkannt: " + session.Profile.GameName, session.Note, NotificationSeverity.Info, "gaming");
        if (_automation.Enabled) _ = _pump.RaiseAsync(TriggerType.GameStarted, session.ProcessName);
    }

    private void OnGameEnded(object? sender, GameSessionRecord record)
    {
        Ui.Post(UpdateMonitoringMode);
        var text = $"Sitzungsdauer {record.Duration:hh\\:mm\\:ss}." + (record.ProfileApplied ? " Die Profiländerungen wurden zurückgenommen, sofern so eingestellt." : "");
        _notifications.Publish(NotificationKind.ProfileRestored, "Spiel beendet: " + record.GameName, text, NotificationSeverity.Info, "gaming");
        if (_automation.Enabled) _ = _pump.RaiseAsync(TriggerType.GameExited, null);
    }

    private void OnRuleExecuted(object? sender, AutomationRunRecord record)
    {
        if (record.Skipped) return;
        _notifications.Publish(NotificationKind.AutomationExecuted, "Automatisierung: " + record.RuleName, record.Message,
            record.Success ? NotificationSeverity.Info : NotificationSeverity.Warning, "automation");
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _slowTimer?.Stop();
        _monitoring.MetricsUpdated -= OnMetrics;
        try { _monitoring.StopAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        _shutdown.Dispose();
    }
}
