namespace NexusOptimizer.Core.Monitoring;

public enum AlertComparison
{
    Above,
    Below,
}

/// <summary>Benutzerdefinierte Warnregel mit Mindestdauer, Hysterese und Abklingzeit.</summary>
public sealed record AlertRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required MetricId Metric { get; init; }
    public AlertComparison Comparison { get; init; } = AlertComparison.Above;
    public required double Threshold { get; init; }
    /// <summary>Der Wert muss die Schwelle so lange ununterbrochen verletzen, bevor gewarnt wird.</summary>
    public int SustainSeconds { get; init; } = 10;
    /// <summary>Abstand zur Schwelle, der erreicht sein muss, bevor die Warnung als erledigt gilt.</summary>
    public double Hysteresis { get; init; } = 5;
    /// <summary>Frühester Abstand zwischen zwei Warnungen derselben Regel.</summary>
    public int CooldownSeconds { get; init; } = 300;
    public bool Enabled { get; init; } = true;
}

public sealed record AlertEvent(AlertRule Rule, DateTimeOffset Timestamp, double Value, bool IsRecovery)
{
    public string Message
    {
        get
        {
            var definition = MetricCatalog.Get(Rule.Metric);
            return IsRecovery
                ? $"{Rule.Name}: wieder im Normalbereich ({definition.Format(Value)})."
                : $"{Rule.Name}: {definition.Name} liegt bei {definition.Format(Value)} (Schwelle {(Rule.Comparison == AlertComparison.Above ? "über" : "unter")} {definition.Format(Rule.Threshold)}).";
        }
    }
}

/// <summary>
/// Wertet Warnregeln gegen den Messwertstrom aus. Zustandsautomat je Regel:
/// Ruhe → Beobachtung (Schwelle verletzt) → Aktiv (nach Mindestdauer gemeldet) → Ruhe (erst nach Unterschreiten von Schwelle − Hysterese).
/// Die Abklingzeit verhindert Benachrichtigungsfluten bei schwankenden Werten.
/// </summary>
public sealed class AlertEngine
{
    private sealed class State
    {
        public DateTimeOffset? ViolatingSince;
        public bool Active;
        public bool Announced;
        public DateTimeOffset? LastFired;
    }

    private readonly Dictionary<string, State> _states = new();
    private IReadOnlyList<AlertRule> _rules = Array.Empty<AlertRule>();
    private readonly object _gate = new();

    public static IReadOnlyList<AlertRule> DefaultRules { get; } = new[]
    {
        new AlertRule { Id = "default-cpu", Name = "Anhaltend hohe CPU-Auslastung", Metric = MetricId.CpuTotal, Threshold = 95, SustainSeconds = 60, Hysteresis = 15, CooldownSeconds = 900 },
        new AlertRule { Id = "default-ram", Name = "Arbeitsspeicher fast voll", Metric = MetricId.MemoryUsedPercent, Threshold = 92, SustainSeconds = 30, Hysteresis = 7, CooldownSeconds = 900 },
        new AlertRule { Id = "default-temp", Name = "Hohe Systemtemperatur", Metric = MetricId.ThermalZone, Threshold = 90, SustainSeconds = 20, Hysteresis = 8, CooldownSeconds = 600 },
        new AlertRule { Id = "default-gpu-temp", Name = "Hohe GPU-Temperatur", Metric = MetricId.GpuTemperature, Threshold = 88, SustainSeconds = 20, Hysteresis = 8, CooldownSeconds = 600 },
        new AlertRule { Id = "default-throttle", Name = "Prozessorleistung wird begrenzt", Metric = MetricId.CpuPerformanceLimit, Comparison = AlertComparison.Below, Threshold = 90, SustainSeconds = 30, Hysteresis = 8, CooldownSeconds = 1800 },
    };

    public IReadOnlyList<AlertRule> Rules
    {
        get { lock (_gate) return _rules; }
        set
        {
            lock (_gate)
            {
                _rules = value.ToList();
                foreach (var stale in _states.Keys.Except(_rules.Select(r => r.Id)).ToList()) _states.Remove(stale);
            }
        }
    }

    public IReadOnlyList<AlertEvent> Evaluate(SystemMetrics metrics)
    {
        var events = new List<AlertEvent>();
        lock (_gate)
        {
            foreach (var rule in _rules)
            {
                if (!rule.Enabled) continue;
                var value = MetricCatalog.Get(rule.Metric).Read(metrics);
                if (value is null || double.IsNaN(value.Value)) continue; // nicht verfügbare Sensoren lösen nie Warnungen aus

                if (!_states.TryGetValue(rule.Id, out var state)) _states[rule.Id] = state = new State();
                var now = metrics.Timestamp;
                var violating = rule.Comparison == AlertComparison.Above ? value > rule.Threshold : value < rule.Threshold;
                var recovered = rule.Comparison == AlertComparison.Above ? value <= rule.Threshold - rule.Hysteresis : value >= rule.Threshold + rule.Hysteresis;

                if (state.Active)
                {
                    if (!recovered) continue;
                    state.Active = false;
                    state.ViolatingSince = null;
                    // Entwarnung nur, wenn die Warnung auch tatsächlich gemeldet wurde.
                    if (state.Announced) events.Add(new AlertEvent(rule, now, value.Value, IsRecovery: true));
                    state.Announced = false;
                    continue;
                }

                if (!violating)
                {
                    state.ViolatingSince = null;
                    continue;
                }

                state.ViolatingSince ??= now;
                if ((now - state.ViolatingSince.Value).TotalSeconds < rule.SustainSeconds) continue;
                if (state.LastFired is { } last && (now - last).TotalSeconds < rule.CooldownSeconds)
                {
                    // Innerhalb der Abklingzeit: als aktiv führen, aber nicht erneut melden.
                    state.Active = true;
                    continue;
                }

                state.Active = true;
                state.Announced = true;
                state.LastFired = now;
                events.Add(new AlertEvent(rule, now, value.Value, IsRecovery: false));
            }
        }
        return events;
    }

    public void Reset() { lock (_gate) _states.Clear(); }
}
