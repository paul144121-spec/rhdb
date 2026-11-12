using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Automation;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Gaming;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.SystemAccess;

namespace NexusOptimizer.Core.Orchestration;

/// <summary>Führt Regelaktionen aus – immer über Planer, Risikofreigabe (Quelle „Automatisierung“) und Ausführungs-Engine.</summary>
public sealed class AutomationActionExecutor : IAutomationActionExecutor
{
    private readonly OptimizationPlanner _planner;
    private readonly OptimizationExecutor _executor;
    private readonly RollbackService _rollback;
    private readonly ProfileManager _profiles;
    private readonly SettingsService _settings;
    private readonly SystemSnapshotService _snapshots;
    private readonly NotificationCenter _notifications;

    public AutomationActionExecutor(OptimizationPlanner planner, OptimizationExecutor executor, RollbackService rollback, ProfileManager profiles,
        SettingsService settings, SystemSnapshotService snapshots, NotificationCenter notifications)
    {
        _planner = planner;
        _executor = executor;
        _rollback = rollback;
        _profiles = profiles;
        _settings = settings;
        _snapshots = snapshots;
        _notifications = notifications;
    }

    private RiskPolicy PolicyFor(AutomationRule rule)
    {
        var policy = _settings.Current.ToRiskPolicy();
        // Es gilt die strengere Grenze aus Regel und globaler Einstellung.
        return policy with { MaxUnattendedRisk = (RiskLevel)Math.Min((int)policy.MaxUnattendedRisk, (int)rule.MaxRisk) };
    }

    public async Task<AutomationActionResult> ExecuteAsync(AutomationRule rule, AutomationAction action, CancellationToken cancellationToken)
    {
        switch (action.Type)
        {
            case RuleActionType.Notify:
                _notifications.Publish(NotificationKind.AutomationExecuted, rule.Name, action.Message ?? "", NotificationSeverity.Info, "automation");
                return new AutomationActionResult(true, "Benachrichtigung gesendet.");

            case RuleActionType.ApplyAction:
            {
                var context = await _snapshots.GetContextAsync(cancellationToken).ConfigureAwait(false);
                var request = new PlanRequest(action.ActionId ?? "", new ActionParameters(action.Parameters), $"Regel „{rule.Name}“");
                var plan = await _planner.BuildAsync(new[] { request }, PolicyFor(rule), context, ExecutionSource.Automation, cancellationToken).ConfigureAwait(false);
                if (plan.IsEmpty)
                {
                    var rejection = plan.Rejected.FirstOrDefault();
                    // „Liegt bereits vor“ ist kein Fehler – die Regel hat ihr Ziel schon erreicht.
                    return rejection?.Kind == RejectionKind.AlreadyApplied
                        ? new AutomationActionResult(true, $"„{rejection.Name}“: bereits im Zielzustand.")
                        : new AutomationActionResult(false, $"Nicht ausgeführt: {rejection?.Reason ?? "unbekannter Grund"}");
                }
                var batch = await _executor.ExecuteAsync(plan, new ExecutionOptions { SourceLabel = rule.Name }, cancellationToken).ConfigureAwait(false);
                if (batch.Busy) return new AutomationActionResult(false, "Übersprungen: Es läuft bereits ein Optimierungsvorgang.");
                return batch.AllSucceeded
                    ? new AutomationActionResult(true, $"„{plan.Steps[0].Preview.Title}“ angewendet und verifiziert.", batch.BatchId)
                    : new AutomationActionResult(false, batch.Steps.FirstOrDefault()?.Message ?? "Fehlgeschlagen.", batch.SucceededCount > 0 ? batch.BatchId : null);
            }

            case RuleActionType.ActivateProfile:
            {
                var profile = _profiles.Find(action.ProfileId ?? "");
                if (profile is null) return new AutomationActionResult(false, "Das Profil der Regel existiert nicht mehr.");
                var context = await _snapshots.GetContextAsync(cancellationToken).ConfigureAwait(false);
                var result = await _profiles.ActivateAsync(profile, PolicyFor(rule), context, ExecutionSource.Automation, null, cancellationToken).ConfigureAwait(false);
                return new AutomationActionResult(result.Success, result.Message, result.Batch?.BatchId);
            }

            default:
                return new AutomationActionResult(false, "Unbekannter Aktionstyp.");
        }
    }

    public async Task<AutomationActionResult> RestoreAsync(IReadOnlyList<string> batchIds, CancellationToken cancellationToken)
    {
        var outcomes = new List<RollbackOutcome>();
        foreach (var batchId in batchIds.Reverse()) outcomes.AddRange(await _rollback.RollbackBatchAsync(batchId).ConfigureAwait(false));
        if (outcomes.Count == 0) return new AutomationActionResult(true, "Es war nichts wiederherzustellen.");
        var failed = outcomes.Count(o => !o.Success);
        return failed == 0
            ? new AutomationActionResult(true, $"{outcomes.Count} Änderung(en) wiederhergestellt.")
            : new AutomationActionResult(false, $"{outcomes.Count - failed} wiederhergestellt, {failed} nicht: {outcomes.First(o => !o.Success).Message}");
    }
}

/// <summary>Wendet ein Spielprofil temporär an. Hintergrundprogramme werden nur beendet, wenn der Benutzer sie im Profil ausdrücklich eingetragen hat.</summary>
public sealed class GameProfileApplier : IGameProfileApplier
{
    private readonly OptimizationPlanner _planner;
    private readonly OptimizationExecutor _executor;
    private readonly RollbackService _rollback;
    private readonly ProfileManager _profiles;
    private readonly SettingsService _settings;
    private readonly SystemSnapshotService _snapshots;
    private readonly IProcessMonitor _processes;

    public GameProfileApplier(OptimizationPlanner planner, OptimizationExecutor executor, RollbackService rollback, ProfileManager profiles,
        SettingsService settings, SystemSnapshotService snapshots, IProcessMonitor processes)
    {
        _planner = planner;
        _executor = executor;
        _rollback = rollback;
        _profiles = profiles;
        _settings = settings;
        _snapshots = snapshots;
        _processes = processes;
    }

    public IReadOnlyList<PlanRequest> BuildRequests(GameProfile profile, int? processId, out IReadOnlyList<string> notes)
    {
        var requests = new List<PlanRequest>();
        var remarks = new List<string>();
        var reason = $"Spielprofil „{profile.GameName}“";

        if (_profiles.ResolvePowerPlan(profile.PowerPlan, out var planNote) is { } plan)
            requests.Add(new PlanRequest(PowerPlanActivateAction.ActionId, ActionParameters.Of(("plan", plan.Id.ToString())), reason));
        if (planNote is not null) remarks.Add(planNote);

        if (profile.ProcessPriority is { Length: > 0 } priority && processId is { } pid)
            requests.Add(new PlanRequest(ProcessPriorityAction.ActionId, ActionParameters.Of(("pid", pid.ToString()), ("priority", priority)), reason));

        if (profile.GpuPreference is { Length: > 0 } gpu && profile.ExecutablePath is { Length: > 0 } path)
            requests.Add(new PlanRequest(GpuPreferenceAction.ActionId, ActionParameters.Of(("path", path), ("preference", gpu)), reason));

        notes = remarks;
        return requests;
    }

    public async Task<GameProfileApplyResult> ApplyAsync(GameProfile profile, int processId, CancellationToken cancellationToken)
    {
        var context = await _snapshots.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var requests = BuildRequests(profile, processId, out var notes);
        var messages = new List<string>(notes);

        // Nur ausdrücklich freigegebene, nicht geschützte Programme werden beendet.
        foreach (var name in profile.CloseProcesses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ProcessProtection.IsProtected(name)) { messages.Add($"„{name}“ ist geschützt und wurde nicht beendet."); continue; }
            foreach (var (id, processName) in _processes.GetRunningProcessNames().Where(p => string.Equals(Path.GetFileNameWithoutExtension(p.Name), Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)))
            {
                if (id == processId) continue;
                var result = _processes.Terminate(id);
                messages.Add(result.Success ? $"„{processName}“ wurde wie im Profil festgelegt beendet." : $"„{processName}“ konnte nicht beendet werden: {result.Message}");
            }
        }

        if (requests.Count == 0) return new GameProfileApplyResult(false, null, string.Join(" ", messages.DefaultIfEmpty("Das Profil enthält keine Änderungen.")));

        var plan = await _planner.BuildAsync(requests, _settings.Current.ToRiskPolicy(), context, ExecutionSource.Gaming, cancellationToken).ConfigureAwait(false);
        foreach (var rejected in plan.Rejected.Where(r => r.Kind != RejectionKind.AlreadyApplied)) messages.Add($"{rejected.Name}: {rejected.Reason}");
        if (plan.IsEmpty) return new GameProfileApplyResult(false, null, string.Join(" ", messages.DefaultIfEmpty("Alle Profileinstellungen lagen bereits vor.")));

        var batch = await _executor.ExecuteAsync(plan, new ExecutionOptions { SourceLabel = profile.GameName }, cancellationToken).ConfigureAwait(false);
        if (batch.Busy) return new GameProfileApplyResult(false, null, "Profil nicht aktiviert: Es läuft bereits ein Optimierungsvorgang.");
        messages.Insert(0, $"Profil aktiviert: {batch.SucceededCount} Änderung(en) angewendet{(batch.FailedCount > 0 ? $", {batch.FailedCount} fehlgeschlagen" : "")}.");
        return new GameProfileApplyResult(batch.SucceededCount > 0, batch.SucceededCount > 0 ? batch.BatchId : null, string.Join(" ", messages));
    }

    public async Task<string> RestoreAsync(string batchId, CancellationToken cancellationToken)
    {
        var outcomes = await _rollback.RollbackBatchAsync(batchId).ConfigureAwait(false);
        var failed = outcomes.Count(o => !o.Success);
        return failed == 0
            ? $"Vorherige Einstellungen wiederhergestellt ({outcomes.Count} Änderung(en))."
            : $"{outcomes.Count - failed} Einstellung(en) wiederhergestellt, {failed} nicht: {outcomes.First(o => !o.Success).Message}";
    }
}

/// <summary>Liefert den Bedingungen der Automatisierung den aktuellen Systemzustand.</summary>
public sealed class LiveAutomationContext : IAutomationContext
{
    private readonly MonitoringService _monitoring;
    private readonly IProcessMonitor _processes;
    private readonly Func<bool> _gameRunning;

    public LiveAutomationContext(MonitoringService monitoring, IProcessMonitor processes, Func<bool> gameRunning)
    {
        _monitoring = monitoring;
        _processes = processes;
        _gameRunning = gameRunning;
    }

    public bool? OnAcPower => _monitoring.Latest?.OnAcPower;
    public double? CpuPercent => _monitoring.Latest?.CpuTotalPercent;
    public bool IsGameRunning => _gameRunning();

    public bool IsProcessRunning(string processName)
    {
        var wanted = Path.GetFileNameWithoutExtension(processName);
        return _processes.GetRunningProcessNames().Any(p => string.Equals(Path.GetFileNameWithoutExtension(p.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Setzt Systemereignisse in Automatisierungsereignisse um: Prozessstart/-ende (durch Vergleich der Prozesslisten),
/// Wechsel der Stromversorgung, Schwellenwerte aus dem Messwertstrom und Uhrzeiten.
/// </summary>
public sealed class AutomationEventPump
{
    private readonly AutomationEngine _engine;
    private readonly IAutomationStore _store;
    private readonly IProcessMonitor _processes;
    private readonly ThresholdEventSource _thresholds = new();
    private readonly TimeProvider _time;
    private HashSet<string>? _knownProcesses;
    private bool? _lastAc;
    private string? _lastMinute;

    public AutomationEventPump(AutomationEngine engine, IAutomationStore store, IProcessMonitor processes, TimeProvider? time = null)
    {
        _engine = engine;
        _store = store;
        _processes = processes;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Bei jedem Messwert aufrufen.</summary>
    public async Task OnMetricsAsync(SystemMetrics metrics, CancellationToken cancellationToken = default)
    {
        if (!_engine.Enabled) return;
        var rules = _store.GetRules();
        if (rules.Count == 0) return;

        if (metrics.OnAcPower is { } ac)
        {
            if (_lastAc is not null && _lastAc != ac)
                await _engine.HandleEventAsync(new AutomationEvent(ac ? TriggerType.AcConnected : TriggerType.BatteryMode, metrics.Timestamp), cancellationToken).ConfigureAwait(false);
            _lastAc = ac;
        }

        foreach (var thresholdEvent in _thresholds.Evaluate(metrics, rules))
            await _engine.HandleEventAsync(thresholdEvent, cancellationToken).ConfigureAwait(false);

        var minute = _time.GetLocalNow().ToString("HH:mm");
        if (minute != _lastMinute)
        {
            _lastMinute = minute;
            if (rules.Any(r => r.Enabled && r.Trigger.Type == TriggerType.Schedule && r.Trigger.TimeOfDay == minute))
                await _engine.HandleEventAsync(new AutomationEvent(TriggerType.Schedule, _time.GetLocalNow()), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>In größeren Abständen (einige Sekunden) aufrufen – nur wenn Regeln mit Prozess-Auslösern existieren.</summary>
    public async Task PollProcessesAsync(CancellationToken cancellationToken = default)
    {
        if (!_engine.Enabled) return;
        var rules = _store.GetRules();
        if (!rules.Any(r => r.Enabled && r.Trigger.Type is TriggerType.ProcessStarted or TriggerType.ProcessExited))
        {
            _knownProcesses = null;
            return;
        }

        var current = _processes.GetRunningProcessNames().Select(p => Path.GetFileNameWithoutExtension(p.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_knownProcesses is not null)
        {
            var now = _time.GetUtcNow();
            foreach (var started in current.Except(_knownProcesses, StringComparer.OrdinalIgnoreCase))
                await _engine.HandleEventAsync(new AutomationEvent(TriggerType.ProcessStarted, now, started), cancellationToken).ConfigureAwait(false);
            foreach (var exited in _knownProcesses.Except(current, StringComparer.OrdinalIgnoreCase))
                await _engine.HandleEventAsync(new AutomationEvent(TriggerType.ProcessExited, now, exited), cancellationToken).ConfigureAwait(false);
        }
        _knownProcesses = current;
    }

    public Task RaiseAsync(TriggerType type, string? processName = null) => _engine.HandleEventAsync(new AutomationEvent(type, _time.GetUtcNow(), processName));
}
