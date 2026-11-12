using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;

namespace NexusOptimizer.Core.Automation;

public enum TriggerType
{
    AppStarted,
    GameStarted,
    GameExited,
    ProcessStarted,
    ProcessExited,
    AcConnected,
    BatteryMode,
    CpuHigh,
    MemoryPressure,
    StorageLow,
    ThermalHigh,
    Schedule,
    SystemIdle,
}

public sealed record AutomationTrigger
{
    public TriggerType Type { get; init; }
    /// <summary>Prozessname ohne Pfad für ProcessStarted/ProcessExited (z. B. „obs64.exe“). Leer = jeder Prozess.</summary>
    public string? ProcessName { get; init; }
    /// <summary>Schwellenwert in Prozent bzw. °C für Schwellen-Trigger.</summary>
    public double? Threshold { get; init; }
    /// <summary>Mindestdauer der Überschreitung in Sekunden.</summary>
    public int DurationSeconds { get; init; } = 60;
    /// <summary>Uhrzeit HH:mm für Schedule.</summary>
    public string? TimeOfDay { get; init; }
}

public enum ConditionType
{
    OnAcPower,
    OnBattery,
    ProcessRunning,
    ProcessNotRunning,
    TimeBetween,
    CpuBelow,
    NoGameRunning,
}

public sealed record AutomationCondition
{
    public ConditionType Type { get; init; }
    public string? Text { get; init; }
    public double? Number { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
}

public enum RuleActionType
{
    ApplyAction,
    ActivateProfile,
    /// <summary>Nimmt die Änderungen zurück, die eine andere Regel (z. B. „Spiel gestartet“) vorgenommen hat.</summary>
    RestoreRuleChanges,
    Notify,
}

public sealed record AutomationAction
{
    public RuleActionType Type { get; init; }
    public string? ActionId { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new();
    public string? ProfileId { get; init; }
    public string? RuleId { get; init; }
    public string? Message { get; init; }
}

public enum RuleErrorHandling
{
    Continue,
    Stop,
    RollbackOnError,
}

public sealed record AutomationRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public AutomationTrigger Trigger { get; init; } = new();
    public List<AutomationCondition> Conditions { get; init; } = new();
    public List<AutomationAction> Actions { get; init; } = new();
    /// <summary>0–100; bei Konflikten gewinnt die höhere Priorität.</summary>
    public int Priority { get; init; } = 50;
    public int CooldownSeconds { get; init; } = 300;
    /// <summary>Obergrenze dieser Regel; die globale Grenze für unbeaufsichtigte Ausführung gilt zusätzlich.</summary>
    public RiskLevel MaxRisk { get; init; } = RiskLevel.Low;
    public bool NotifyOnRun { get; init; } = true;
    public RuleErrorHandling ErrorHandling { get; init; } = RuleErrorHandling.Continue;
}

public sealed record AutomationEvent(TriggerType Type, DateTimeOffset Timestamp, string? ProcessName = null, double? Value = null);

public sealed record AutomationRunRecord(string RuleId, string RuleName, DateTimeOffset Timestamp, bool Success, bool Skipped, string Message, string? BatchId = null);

/// <summary>Momentaner Systemzustand für die Bedingungsprüfung.</summary>
public interface IAutomationContext
{
    bool? OnAcPower { get; }
    bool IsProcessRunning(string processName);
    double? CpuPercent { get; }
    bool IsGameRunning { get; }
}

public sealed record AutomationActionResult(bool Success, string Message, string? BatchId = null);

/// <summary>Führt die Aktionen einer Regel aus – immer über Planer, Risikoprüfung und Ausführungs-Engine.</summary>
public interface IAutomationActionExecutor
{
    Task<AutomationActionResult> ExecuteAsync(AutomationRule rule, AutomationAction action, CancellationToken cancellationToken);
    /// <summary>Nimmt die Änderungen eines früheren Regel-Laufs zurück.</summary>
    Task<AutomationActionResult> RestoreAsync(IReadOnlyList<string> batchIds, CancellationToken cancellationToken);
}

public interface IAutomationStore
{
    IReadOnlyList<AutomationRule> GetRules();
    void SaveRule(AutomationRule rule);
    void DeleteRule(string ruleId);
    void AppendHistory(AutomationRunRecord record);
    IReadOnlyList<AutomationRunRecord> GetHistory(string? ruleId = null, int limit = 200);
}

public sealed class InMemoryAutomationStore : IAutomationStore
{
    private readonly Dictionary<string, AutomationRule> _rules = new();
    private readonly List<AutomationRunRecord> _history = new();
    public IReadOnlyList<AutomationRule> GetRules() => _rules.Values.ToList();
    public void SaveRule(AutomationRule rule) => _rules[rule.Id] = rule;
    public void DeleteRule(string ruleId) => _rules.Remove(ruleId);
    public void AppendHistory(AutomationRunRecord record) => _history.Add(record);
    public IReadOnlyList<AutomationRunRecord> GetHistory(string? ruleId = null, int limit = 200)
        => _history.Where(h => ruleId is null || h.RuleId == ruleId).Reverse().Take(limit).ToList();
}

public sealed record RuleConflict(AutomationRule First, AutomationRule Second, string Resource, string Description);

public static class AutomationAnalysis
{
    /// <summary>Ressource, die eine Aktion verändert – Grundlage der Konflikterkennung. null = verändert nichts.</summary>
    public static string? ResourceKey(AutomationAction action) => action.Type switch
    {
        RuleActionType.ActivateProfile => "profile",
        RuleActionType.ApplyAction when action.ActionId == Power.PowerPlanActivateAction.ActionId => "power.plan",
        RuleActionType.ApplyAction when action.ActionId == Power.PowerSettingAction.ActionId => "power.setting." + action.Parameters.GetValueOrDefault("setting"),
        RuleActionType.ApplyAction when action.ActionId is not null => action.ActionId + "|" + string.Join(",", action.Parameters.Where(p => p.Key is "service" or "name" or "path" or "adapter" or "pid").Select(p => p.Value)),
        _ => null,
    };

    private static string ValueKey(AutomationAction action) => action.Type == RuleActionType.ActivateProfile
        ? action.ProfileId ?? ""
        : string.Join(";", action.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    private static bool TriggersOverlap(AutomationTrigger a, AutomationTrigger b)
    {
        if (a.Type != b.Type) return false;
        if (a.Type is TriggerType.ProcessStarted or TriggerType.ProcessExited)
            return string.IsNullOrEmpty(a.ProcessName) || string.IsNullOrEmpty(b.ProcessName) || string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
        if (a.Type == TriggerType.Schedule) return a.TimeOfDay == b.TimeOfDay;
        return true;
    }

    private static bool ConditionsExclusive(AutomationRule a, AutomationRule b)
    {
        bool Has(AutomationRule r, ConditionType t) => r.Conditions.Any(c => c.Type == t);
        if ((Has(a, ConditionType.OnAcPower) && Has(b, ConditionType.OnBattery)) || (Has(a, ConditionType.OnBattery) && Has(b, ConditionType.OnAcPower))) return true;
        foreach (var running in a.Conditions.Where(c => c.Type == ConditionType.ProcessRunning))
            if (b.Conditions.Any(c => c.Type == ConditionType.ProcessNotRunning && string.Equals(c.Text, running.Text, StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var running in b.Conditions.Where(c => c.Type == ConditionType.ProcessRunning))
            if (a.Conditions.Any(c => c.Type == ConditionType.ProcessNotRunning && string.Equals(c.Text, running.Text, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    /// <summary>Findet Regelpaare, die beim selben Ereignis dieselbe Ressource unterschiedlich setzen würden.</summary>
    public static IReadOnlyList<RuleConflict> DetectConflicts(IReadOnlyList<AutomationRule> rules)
    {
        var conflicts = new List<RuleConflict>();
        var active = rules.Where(r => r.Enabled).ToList();
        for (var i = 0; i < active.Count; i++)
        {
            for (var j = i + 1; j < active.Count; j++)
            {
                var a = active[i];
                var b = active[j];
                if (!TriggersOverlap(a.Trigger, b.Trigger) || ConditionsExclusive(a, b)) continue;
                foreach (var actionA in a.Actions)
                {
                    var resource = ResourceKey(actionA);
                    if (resource is null) continue;
                    var actionB = b.Actions.FirstOrDefault(x => ResourceKey(x) == resource);
                    if (actionB is null || ValueKey(actionA) == ValueKey(actionB)) continue;
                    var winner = a.Priority >= b.Priority ? a : b;
                    conflicts.Add(new RuleConflict(a, b, resource,
                        $"„{a.Name}“ und „{b.Name}“ reagieren auf dasselbe Ereignis und setzen dieselbe Einstellung unterschiedlich. Zur Laufzeit gewinnt „{winner.Name}“ (höhere Priorität)."));
                    break;
                }
            }
        }

        // Paare, die sich gegenseitig auslösen könnten (Schwellen-Trigger in beide Richtungen), führen zu Pendeln.
        foreach (var rule in active.Where(r => r.CooldownSeconds < 60 && r.Actions.Any(a => ResourceKey(a) is not null)))
        {
            conflicts.Add(new RuleConflict(rule, rule, "cooldown",
                $"„{rule.Name}“ hat eine Abklingzeit unter 60 Sekunden. Das begünstigt schnelles Hin- und Herschalten; die Engine erzwingt mindestens 60 Sekunden."));
        }
        return conflicts;
    }

    public static IReadOnlyList<string> Validate(AutomationRule rule)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 80) errors.Add("Die Regel benötigt einen Namen (höchstens 80 Zeichen).");
        if (rule.Actions.Count == 0) errors.Add("Die Regel benötigt mindestens eine Aktion.");
        if (rule.Priority is < 0 or > 100) errors.Add("Die Priorität muss zwischen 0 und 100 liegen.");
        if (rule.MaxRisk > RiskLevel.Low) errors.Add("Automatisierungsregeln dürfen höchstens die Risikostufe „Niedrig“ verwenden.");
        if (rule.Trigger.Type == TriggerType.Schedule && !TimeOnly.TryParseExact(rule.Trigger.TimeOfDay, "HH:mm", out _)) errors.Add("Für zeitgesteuerte Regeln ist eine Uhrzeit im Format HH:mm erforderlich.");
        if (rule.Trigger.Type is TriggerType.CpuHigh or TriggerType.MemoryPressure or TriggerType.StorageLow or TriggerType.ThermalHigh && rule.Trigger.Threshold is not (> 0 and <= 110))
            errors.Add("Für diesen Auslöser ist ein Schwellenwert zwischen 1 und 110 erforderlich.");
        foreach (var action in rule.Actions)
        {
            switch (action.Type)
            {
                case RuleActionType.ApplyAction when string.IsNullOrWhiteSpace(action.ActionId): errors.Add("Eine Aktion hat keine Aktions-ID."); break;
                case RuleActionType.ActivateProfile when string.IsNullOrWhiteSpace(action.ProfileId): errors.Add("Eine Profilaktion hat kein Profil."); break;
                case RuleActionType.RestoreRuleChanges when string.IsNullOrWhiteSpace(action.RuleId): errors.Add("Eine Wiederherstellungsaktion nennt keine Regel."); break;
                case RuleActionType.Notify when string.IsNullOrWhiteSpace(action.Message) || action.Message.Length > 300: errors.Add("Eine Benachrichtigung benötigt einen Text (höchstens 300 Zeichen)."); break;
            }
        }
        return errors;
    }
}

/// <summary>
/// Wertet Ereignisse gegen die Regeln aus. Schutzmechanismen: Abklingzeit je Regel (mindestens 60 s), Prioritätsentscheidung
/// bei gleichzeitigen Regeln auf derselben Ressource und eine Pendelsperre, wenn eine Ressource zu häufig umgeschaltet wird.
/// </summary>
public sealed class AutomationEngine
{
    public const int MinimumCooldownSeconds = 60;
    public const int OscillationWindowMinutes = 10;
    public const int OscillationMaxChanges = 4;
    public const int OscillationLockMinutes = 30;

    private readonly IAutomationStore _store;
    private readonly IAutomationActionExecutor _executor;
    private readonly IAutomationContext _context;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _resourceChanges = new();
    private readonly Dictionary<string, DateTimeOffset> _resourceLockedUntil = new();
    private readonly Dictionary<string, List<string>> _ruleBatches = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AutomationEngine(IAutomationStore store, IAutomationActionExecutor executor, IAutomationContext context, TimeProvider? time = null, ILogger<AutomationEngine>? logger = null)
    {
        _store = store;
        _executor = executor;
        _context = context;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AutomationEngine>.Instance;
    }

    public bool Enabled { get; set; } = true;
    public event EventHandler<AutomationRunRecord>? RuleExecuted;

    public async Task<IReadOnlyList<AutomationRunRecord>> HandleEventAsync(AutomationEvent automationEvent, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return Array.Empty<AutomationRunRecord>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = automationEvent.Timestamp;
            var matching = _store.GetRules()
                .Where(r => r.Enabled && Matches(r.Trigger, automationEvent) && r.Conditions.All(c => Evaluate(c, now)))
                .OrderByDescending(r => r.Priority).ThenBy(r => r.Name, StringComparer.Ordinal)
                .ToList();

            var records = new List<AutomationRunRecord>();
            var claimed = new Dictionary<string, AutomationRule>();
            foreach (var rule in matching)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await RunRuleAsync(rule, now, claimed, cancellationToken).ConfigureAwait(false);
                if (record is null) continue;
                records.Add(record);
                _store.AppendHistory(record);
                RuleExecuted?.Invoke(this, record);
            }
            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutomationRunRecord?> RunRuleAsync(AutomationRule rule, DateTimeOffset now, Dictionary<string, AutomationRule> claimed, CancellationToken cancellationToken)
    {
        var cooldown = Math.Max(MinimumCooldownSeconds, rule.CooldownSeconds);
        if (_lastRun.TryGetValue(rule.Id, out var last) && (now - last).TotalSeconds < cooldown) return null; // still, ohne Verlaufseintrag

        var resources = rule.Actions.Select(AutomationAnalysis.ResourceKey).Where(k => k is not null).Select(k => k!).Distinct().ToList();

        var conflict = resources.Select(r => claimed.GetValueOrDefault(r)).FirstOrDefault(r => r is not null);
        if (conflict is not null)
            return new AutomationRunRecord(rule.Id, rule.Name, now, false, true, $"Übersprungen: Konflikt mit der höher priorisierten Regel „{conflict.Name}“.");

        var locked = resources.FirstOrDefault(r => _resourceLockedUntil.TryGetValue(r, out var until) && until > now);
        if (locked is not null)
            return new AutomationRunRecord(rule.Id, rule.Name, now, false, true, "Übersprungen: Pendelsperre aktiv, weil diese Einstellung zuletzt zu häufig automatisch umgeschaltet wurde.");

        foreach (var resource in resources)
        {
            claimed[resource] = rule;
            if (!_resourceChanges.TryGetValue(resource, out var changes)) _resourceChanges[resource] = changes = new List<DateTimeOffset>();
            changes.RemoveAll(t => (now - t).TotalMinutes > OscillationWindowMinutes);
            if (changes.Count >= OscillationMaxChanges)
            {
                _resourceLockedUntil[resource] = now.AddMinutes(OscillationLockMinutes);
                changes.Clear();
                return new AutomationRunRecord(rule.Id, rule.Name, now, false, true,
                    $"Pendeln erkannt: Die Einstellung wurde {OscillationMaxChanges}-mal in {OscillationWindowMinutes} Minuten umgeschaltet. Automatische Änderungen daran sind für {OscillationLockMinutes} Minuten gesperrt.");
            }
            changes.Add(now);
        }

        _lastRun[rule.Id] = now;
        var messages = new List<string>();
        var batches = new List<string>();
        var success = true;

        foreach (var action in rule.Actions)
        {
            AutomationActionResult result;
            try
            {
                if (action.Type == RuleActionType.RestoreRuleChanges)
                {
                    var targets = action.RuleId is not null && _ruleBatches.Remove(action.RuleId, out var stored) ? stored : new List<string>();
                    result = targets.Count == 0
                        ? new AutomationActionResult(true, "Keine Änderungen zum Wiederherstellen vorhanden.")
                        : await _executor.RestoreAsync(targets, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await _executor.ExecuteAsync(rule, action, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Aktion der Regel {Rule} ist fehlgeschlagen.", rule.Name);
                result = new AutomationActionResult(false, "Unerwarteter Fehler: " + ex.Message);
            }

            messages.Add(result.Message);
            if (result.BatchId is not null) batches.Add(result.BatchId);
            if (result.Success) continue;

            success = false;
            if (rule.ErrorHandling == RuleErrorHandling.Continue) continue;
            if (rule.ErrorHandling == RuleErrorHandling.RollbackOnError && batches.Count > 0)
            {
                var restore = await _executor.RestoreAsync(batches, cancellationToken).ConfigureAwait(false);
                messages.Add("Wegen des Fehlers zurückgenommen: " + restore.Message);
                batches.Clear();
            }
            break;
        }

        if (batches.Count > 0)
        {
            if (!_ruleBatches.TryGetValue(rule.Id, out var stored)) _ruleBatches[rule.Id] = stored = new List<string>();
            stored.AddRange(batches);
        }

        return new AutomationRunRecord(rule.Id, rule.Name, now, success, false, string.Join(" ", messages.Where(m => !string.IsNullOrWhiteSpace(m))), batches.LastOrDefault());
    }

    private static bool Matches(AutomationTrigger trigger, AutomationEvent e)
    {
        if (trigger.Type != e.Type) return false;
        return trigger.Type switch
        {
            TriggerType.ProcessStarted or TriggerType.ProcessExited => string.IsNullOrEmpty(trigger.ProcessName)
                || string.Equals(Path.GetFileNameWithoutExtension(trigger.ProcessName), Path.GetFileNameWithoutExtension(e.ProcessName), StringComparison.OrdinalIgnoreCase),
            TriggerType.Schedule => trigger.TimeOfDay == e.Timestamp.ToLocalTime().ToString("HH:mm"),
            _ => true,
        };
    }

    private bool Evaluate(AutomationCondition condition, DateTimeOffset now) => condition.Type switch
    {
        ConditionType.OnAcPower => _context.OnAcPower != false, // Desktop ohne Akku gilt als Netzbetrieb
        ConditionType.OnBattery => _context.OnAcPower == false,
        ConditionType.ProcessRunning => condition.Text is { Length: > 0 } name && _context.IsProcessRunning(name),
        ConditionType.ProcessNotRunning => condition.Text is { Length: > 0 } name && !_context.IsProcessRunning(name),
        ConditionType.CpuBelow => _context.CpuPercent is { } cpu && condition.Number is { } limit && cpu < limit,
        ConditionType.NoGameRunning => !_context.IsGameRunning,
        ConditionType.TimeBetween => IsBetween(TimeOnly.FromDateTime(now.LocalDateTime), condition.From, condition.To),
        _ => false,
    };

    internal static bool IsBetween(TimeOnly time, string? from, string? to)
    {
        if (!TimeOnly.TryParseExact(from, "HH:mm", out var start) || !TimeOnly.TryParseExact(to, "HH:mm", out var end)) return false;
        return start <= end ? time >= start && time < end : time >= start || time < end; // Zeitraum über Mitternacht
    }
}

/// <summary>
/// Erzeugt Schwellen-Ereignisse (CPU, RAM, Temperatur) aus dem Messwertstrom. Ein Ereignis entsteht erst nach der Mindestdauer
/// und erneut erst, nachdem der Wert zwischenzeitlich deutlich unter die Schwelle gefallen ist (Hysterese).
/// </summary>
public sealed class ThresholdEventSource
{
    private sealed class State { public DateTimeOffset? Since; public bool Fired; }
    private readonly Dictionary<string, State> _states = new();

    public IReadOnlyList<AutomationEvent> Evaluate(SystemMetrics metrics, IReadOnlyList<AutomationRule> rules)
    {
        var events = new List<AutomationEvent>();
        foreach (var group in rules.Where(r => r.Enabled && r.Trigger.Threshold is not null).GroupBy(r => (r.Trigger.Type, r.Trigger.Threshold, r.Trigger.DurationSeconds)))
        {
            double? value = group.Key.Type switch
            {
                TriggerType.CpuHigh => metrics.CpuTotalPercent,
                TriggerType.MemoryPressure => metrics.MemoryUsedPercent,
                TriggerType.ThermalHigh => metrics.CpuPackageCelsius ?? metrics.ThermalZoneCelsius,
                _ => null,
            };
            if (value is null) continue;

            var key = $"{group.Key.Type}|{group.Key.Threshold}|{group.Key.DurationSeconds}";
            if (!_states.TryGetValue(key, out var state)) _states[key] = state = new State();
            var threshold = group.Key.Threshold!.Value;

            if (value >= threshold)
            {
                state.Since ??= metrics.Timestamp;
                if (!state.Fired && (metrics.Timestamp - state.Since.Value).TotalSeconds >= group.Key.DurationSeconds)
                {
                    state.Fired = true;
                    events.Add(new AutomationEvent(group.Key.Type, metrics.Timestamp, Value: value));
                }
            }
            else
            {
                state.Since = null;
                if (value < threshold * 0.9) state.Fired = false;
            }
        }
        return events;
    }
}
