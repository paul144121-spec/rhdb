using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Registry;

namespace NexusOptimizer.Core.Optimization;

public enum OptimizationMode
{
    Safe,
    Balanced,
    Gaming,
    MaximumPerformance,
    Custom,
    Experimental,
}

public sealed record OptimizationModeInfo(OptimizationMode Mode, string Name, string Summary, IReadOnlyList<string> Points, RiskLevel HighestRisk, string? Warning = null)
{
    /// <summary>Experimentelle und benutzerdefinierte Läufe gelten als manuelle Ausführung mit Einzelbestätigung.</summary>
    public ExecutionSource Source => Mode is OptimizationMode.Experimental or OptimizationMode.Custom ? ExecutionSource.User : ExecutionSource.OneClick;
}

public static class OptimizationModes
{
    public static IReadOnlyList<OptimizationModeInfo> All { get; } = new[]
    {
        new OptimizationModeInfo(OptimizationMode.Safe, "Sichere Optimierung",
            "Nur konservative, vollständig umkehrbare Einstellungen mit sehr niedrigem Risiko.",
            new[] { "Keine Änderungen an Sicherheitsfunktionen", "Windows-Funktionen bleiben erhalten", "Jede Änderung einzeln rückgängig zu machen" }, RiskLevel.VeryLow),
        new OptimizationModeInfo(OptimizationMode.Balanced, "Ausgewogene Optimierung",
            "Moderate Anpassungen an Oberfläche, Hintergrundaktivität und Energieplan.",
            new[] { "Hintergrundaktivität und Vorschläge reduzieren", "Energieplan passend zur Stromversorgung", "Ausgewählte Windows-Einstellungen" }, RiskLevel.Low),
        new OptimizationModeInfo(OptimizationMode.Gaming, "Gaming-Optimierung",
            "Einstellungen für Spiele: Spielmodus, Aufzeichnungslast und – im Netzbetrieb – ein leistungsorientierter Energieplan.",
            new[] { "Spielmodus und Game-Bar-Einstellungen", "Leistungsorientierter Energieplan nur im Netzbetrieb und ohne thermische Drosselung", "Keine Zusage höherer Bildraten" }, RiskLevel.Low,
            "Ein Leistungsplan erhöht Stromverbrauch, Abwärme und Lüftergeräusch."),
        new OptimizationModeInfo(OptimizationMode.MaximumPerformance, "Maximale Leistung",
            "Weitergehende, unterstützte Leistungseinstellungen einschließlich des stärksten verfügbaren Energieplans.",
            new[] { "Stärkster verfügbarer Energieplan", "Optische Effekte reduzieren", "Sicherheitsfunktionen bleiben unangetastet" }, RiskLevel.Moderate,
            "Deutlich höherer Stromverbrauch, mehr Abwärme und Lüftergeräusch sowie kürzere Akkulaufzeit. Für Notebooks im Akkubetrieb nicht empfehlenswert."),
        new OptimizationModeInfo(OptimizationMode.Custom, "Benutzerdefinierte Optimierung",
            "Sie wählen jede Änderung einzeln aus – mit Erklärung, Risikoangabe und Rückgängig-Möglichkeit.",
            new[] { "Volle Kontrolle über jede Einstellung", "Risikostufe gemäß Ihrer Freigabe in den Einstellungen" }, RiskLevel.Moderate),
        new OptimizationModeInfo(OptimizationMode.Experimental, "Experimentelle Optimierung",
            "Fortgeschrittene Änderungen mit unbelegtem Nutzen oder möglichen Nebenwirkungen. Nur nach Freischaltung des Expertenlabors, mit Einzelbestätigung je Änderung und nie unbeaufsichtigt.",
            new[] { "Ausdrückliche Freischaltung erforderlich", "Jede Änderung wird einzeln bestätigt", "Keine automatische Ausführung" }, RiskLevel.High,
            "Diese Änderungen können Stabilität, Kompatibilität oder das Verhalten von Windows beeinträchtigen."),
    };

    public static OptimizationModeInfo Get(OptimizationMode mode) => All.First(m => m.Mode == mode);

    /// <summary>
    /// Stellt die Kandidaten eines Modus zusammen. Ob ein Kandidat tatsächlich ausgeführt werden darf und ob er überhaupt
    /// etwas ändern würde, entscheidet anschließend ausschließlich der Planer.
    /// </summary>
    public static IReadOnlyList<PlanRequest> BuildCandidates(OptimizationMode mode, SystemSnapshot snapshot, IReadOnlyList<Recommendation> recommendations, out IReadOnlyList<string> notes)
    {
        var requests = new List<PlanRequest>();
        var remarks = new List<string>();

        IEnumerable<RegistryTweakDefinition> Tagged(params string[] tags)
            => RegistryTweakCatalog.All.Where(t => !t.Experimental && t.Tags.Any(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));

        void AddTweaks(IEnumerable<RegistryTweakDefinition> tweaks, RiskLevel max)
        {
            foreach (var tweak in tweaks.Where(t => t.Risk <= max))
            {
                if (snapshot.DeclinedActions.Contains(tweak.ActionId)) continue;
                requests.Add(new PlanRequest(tweak.ActionId, ActionParameters.Empty, tweak.ExpectedBehavior));
            }
        }

        void AddPowerPlan(params Guid[] preferred)
        {
            if (snapshot.DeclinedActions.Contains(PowerPlanActivateAction.ActionId)) return;
            if (snapshot.Context.OnBatteryPower)
            {
                remarks.Add("Das Gerät läuft im Akkubetrieb – ein leistungsorientierter Energieplan wird deshalb nicht vorgeschlagen.");
                return;
            }
            if (snapshot.ThermalLimitObserved)
            {
                remarks.Add("Es wurde eine Begrenzung der Prozessorleistung beobachtet – ein leistungsorientierter Energieplan wird deshalb nicht vorgeschlagen.");
                return;
            }
            var plan = preferred.Select(id => snapshot.PowerPlans.FirstOrDefault(p => p.Id == id)).FirstOrDefault(p => p is not null);
            if (plan is null)
            {
                remarks.Add("Dieses System stellt keinen leistungsorientierten Energieplan bereit (typisch für Geräte mit Modern Standby). Der Energieplan bleibt unverändert.");
                return;
            }
            requests.Add(new PlanRequest(PowerPlanActivateAction.ActionId, ActionParameters.Of(("plan", plan.Id.ToString())), "Leistungsorientierter Energieplan im Netzbetrieb"));
        }

        void AddRecommended(RiskLevel max)
        {
            foreach (var request in recommendations.SelectMany(r => r.Actions))
            {
                var tweak = RegistryTweakCatalog.Find(request.ActionId);
                if (tweak is not null && (tweak.Risk > max || tweak.Experimental)) continue;
                requests.Add(request);
            }
        }

        switch (mode)
        {
            case OptimizationMode.Safe:
                AddTweaks(Tagged(RegistryTweakCatalog.TagSafe), RiskLevel.VeryLow);
                break;

            case OptimizationMode.Balanced:
                AddRecommended(RiskLevel.Low);
                AddTweaks(Tagged(RegistryTweakCatalog.TagSafe, RegistryTweakCatalog.TagBalanced), RiskLevel.Low);
                break;

            case OptimizationMode.Gaming:
                AddTweaks(Tagged(RegistryTweakCatalog.TagGaming).Where(t => !t.Tags.Contains(RegistryTweakCatalog.TagExpert)), RiskLevel.Low);
                AddPowerPlan(WellKnownPowerPlans.HighPerformance, WellKnownPowerPlans.UltimatePerformance);
                break;

            case OptimizationMode.MaximumPerformance:
                AddPowerPlan(WellKnownPowerPlans.UltimatePerformance, WellKnownPowerPlans.HighPerformance);
                AddTweaks(Tagged(RegistryTweakCatalog.TagPerformance, RegistryTweakCatalog.TagGaming, RegistryTweakCatalog.TagLowEnd), RiskLevel.Moderate);
                break;

            case OptimizationMode.Custom:
                AddTweaks(RegistryTweakCatalog.All.Where(t => !t.Experimental), RiskLevel.High);
                break;

            case OptimizationMode.Experimental:
                foreach (var tweak in RegistryTweakCatalog.All.Where(t => t.Experimental))
                    requests.Add(new PlanRequest(tweak.ActionId, ActionParameters.Empty, tweak.ExpectedBehavior));
                break;
        }

        notes = remarks;
        return requests.GroupBy(r => r.Key).Select(g => g.First()).ToList();
    }
}

public sealed record OneClickAnalysis
{
    public required OptimizationMode Mode { get; init; }
    public required SystemSnapshot Snapshot { get; init; }
    public required OptimizationPlan Plan { get; init; }
    public required IReadOnlyList<Recommendation> Recommendations { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Ablauf der Ein-Klick-Optimierung (Abschnitt 5.1): erst analysieren und planen, dann – nach Bestätigung – ausführen.</summary>
public sealed class OneClickOptimizer
{
    private readonly SystemSnapshotService _snapshots;
    private readonly RuleBasedAdvisor _advisor;
    private readonly OptimizationPlanner _planner;

    public OneClickOptimizer(SystemSnapshotService snapshots, RuleBasedAdvisor advisor, OptimizationPlanner planner)
    {
        _snapshots = snapshots;
        _advisor = advisor;
        _planner = planner;
    }

    public async Task<OneClickAnalysis> AnalyzeAsync(OptimizationMode mode, RiskPolicy policy, OptimizationGoal goal, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshots.CaptureAsync(progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("Optimierungsmöglichkeiten werden ermittelt …");
        var recommendations = _advisor.Analyze(snapshot, mode == OptimizationMode.Gaming ? OptimizationGoal.MaximumFps : goal);
        var candidates = OptimizationModes.BuildCandidates(mode, snapshot, recommendations, out var notes);

        progress?.Report("Kompatibilität und Risiken werden bewertet …");
        var plan = await _planner.BuildAsync(candidates, policy, snapshot.Context, OptimizationModes.Get(mode).Source, cancellationToken).ConfigureAwait(false);

        return new OneClickAnalysis { Mode = mode, Snapshot = snapshot, Plan = plan, Recommendations = recommendations, Notes = notes };
    }
}
