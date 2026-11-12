using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Benchmark;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Tuning;

/// <summary>Messgrößen, die in die Zielbewertung eingehen. Jede Größe ist optional – fehlende Sensoren werden ausgelassen.</summary>
public enum TuningDimension
{
    Throughput,
    FrameStability,
    Temperature,
    PowerDraw,
    BackgroundLoad,
    Responsiveness,
}

public sealed record DimensionChange(TuningDimension Dimension, double ChangePercent, bool HigherIsBetter, bool Significant);

public sealed record GoalScore(double Score, IReadOnlyList<string> Contributions, bool HasEvidence);

/// <summary>
/// Mehrziel-Bewertung: Dieselbe Messänderung wird je nach Benutzerziel unterschiedlich gewichtet. Wer „Leise“ wählt,
/// bewertet höhere Temperaturen stark negativ, auch wenn der Durchsatz steigt.
/// </summary>
public static class MultiObjectiveScorer
{
    private static readonly Dictionary<OptimizationGoal, Dictionary<TuningDimension, double>> Weights = new()
    {
        [OptimizationGoal.MaximumFps] = new() { [TuningDimension.Throughput] = 1.0, [TuningDimension.FrameStability] = 0.5, [TuningDimension.Temperature] = 0.1, [TuningDimension.PowerDraw] = 0.0, [TuningDimension.BackgroundLoad] = 0.2, [TuningDimension.Responsiveness] = 0.3 },
        [OptimizationGoal.StableFrametimes] = new() { [TuningDimension.Throughput] = 0.4, [TuningDimension.FrameStability] = 1.0, [TuningDimension.Temperature] = 0.2, [TuningDimension.PowerDraw] = 0.0, [TuningDimension.BackgroundLoad] = 0.4, [TuningDimension.Responsiveness] = 0.5 },
        [OptimizationGoal.LowNoise] = new() { [TuningDimension.Throughput] = 0.2, [TuningDimension.FrameStability] = 0.1, [TuningDimension.Temperature] = 1.0, [TuningDimension.PowerDraw] = 0.8, [TuningDimension.BackgroundLoad] = 0.3, [TuningDimension.Responsiveness] = 0.1 },
        [OptimizationGoal.LowTemperatures] = new() { [TuningDimension.Throughput] = 0.2, [TuningDimension.FrameStability] = 0.1, [TuningDimension.Temperature] = 1.0, [TuningDimension.PowerDraw] = 0.6, [TuningDimension.BackgroundLoad] = 0.2, [TuningDimension.Responsiveness] = 0.1 },
        [OptimizationGoal.BatteryLife] = new() { [TuningDimension.Throughput] = 0.1, [TuningDimension.FrameStability] = 0.0, [TuningDimension.Temperature] = 0.4, [TuningDimension.PowerDraw] = 1.0, [TuningDimension.BackgroundLoad] = 0.6, [TuningDimension.Responsiveness] = 0.1 },
        [OptimizationGoal.FastStartup] = new() { [TuningDimension.Throughput] = 0.3, [TuningDimension.FrameStability] = 0.0, [TuningDimension.Temperature] = 0.1, [TuningDimension.PowerDraw] = 0.1, [TuningDimension.BackgroundLoad] = 1.0, [TuningDimension.Responsiveness] = 0.6 },
        [OptimizationGoal.Productivity] = new() { [TuningDimension.Throughput] = 0.6, [TuningDimension.FrameStability] = 0.0, [TuningDimension.Temperature] = 0.3, [TuningDimension.PowerDraw] = 0.2, [TuningDimension.BackgroundLoad] = 0.5, [TuningDimension.Responsiveness] = 1.0 },
        [OptimizationGoal.LowBackgroundUsage] = new() { [TuningDimension.Throughput] = 0.2, [TuningDimension.FrameStability] = 0.0, [TuningDimension.Temperature] = 0.2, [TuningDimension.PowerDraw] = 0.4, [TuningDimension.BackgroundLoad] = 1.0, [TuningDimension.Responsiveness] = 0.4 },
        [OptimizationGoal.Stability] = new() { [TuningDimension.Throughput] = 0.2, [TuningDimension.FrameStability] = 0.6, [TuningDimension.Temperature] = 0.6, [TuningDimension.PowerDraw] = 0.2, [TuningDimension.BackgroundLoad] = 0.3, [TuningDimension.Responsiveness] = 0.4 },
        [OptimizationGoal.BalancedPerformance] = new() { [TuningDimension.Throughput] = 0.6, [TuningDimension.FrameStability] = 0.5, [TuningDimension.Temperature] = 0.5, [TuningDimension.PowerDraw] = 0.4, [TuningDimension.BackgroundLoad] = 0.5, [TuningDimension.Responsiveness] = 0.6 },
    };

    public static double Weight(OptimizationGoal goal, TuningDimension dimension) => Weights[goal][dimension];

    public static string ToGerman(TuningDimension dimension) => dimension switch
    {
        TuningDimension.Throughput => "Durchsatz",
        TuningDimension.FrameStability => "Frametime-Stabilität",
        TuningDimension.Temperature => "Temperatur",
        TuningDimension.PowerDraw => "Leistungsaufnahme",
        TuningDimension.BackgroundLoad => "Hintergrundlast",
        TuningDimension.Responsiveness => "Reaktionsverhalten",
        _ => dimension.ToString(),
    };

    /// <summary>
    /// Bewertet eine Menge gemessener Änderungen für ein oder mehrere Ziele. Nicht signifikante Änderungen zählen als 0 –
    /// Messrauschen kann die Bewertung also weder verbessern noch verschlechtern. Positive Werte sprechen für das Beibehalten.
    /// </summary>
    public static GoalScore Score(IReadOnlyList<DimensionChange> changes, IReadOnlyList<OptimizationGoal> goals)
    {
        if (goals.Count == 0) goals = new[] { OptimizationGoal.BalancedPerformance };
        var contributions = new List<string>();
        double total = 0, weightSum = 0;
        var evidence = false;

        foreach (var change in changes)
        {
            var weight = goals.Average(g => Weight(g, change.Dimension));
            if (weight <= 0) continue;
            weightSum += weight;
            if (!change.Significant) continue;

            evidence = true;
            var benefit = change.HigherIsBetter ? change.ChangePercent : -change.ChangePercent;
            total += weight * benefit;
            contributions.Add($"{ToGerman(change.Dimension)}: {change.ChangePercent:+0.0;-0.0} % → {(benefit >= 0 ? "günstig" : "ungünstig")} (Gewicht {weight:0.0})");
        }

        return new GoalScore(weightSum == 0 ? 0 : total / weightSum, contributions, evidence);
    }
}

public enum TuningRecommendation
{
    Keep,
    Revert,
    NoMeasurableEffect,
    Inconclusive,
}

public sealed record TuningExperimentResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required DateTimeOffset Timestamp { get; init; }
    public required string ActionId { get; init; }
    public required string ActionName { get; init; }
    public required string Workload { get; init; }
    public required IReadOnlyList<double> Baseline { get; init; }
    public required IReadOnlyList<double> Candidate { get; init; }
    public required string Unit { get; init; }
    public required MeasurementComparison Comparison { get; init; }
    public required TuningRecommendation Recommendation { get; init; }
    public required string Summary { get; init; }
    public bool Reverted { get; init; }
    public string? BatchId { get; init; }
}

public interface ITuningStore
{
    void Save(TuningExperimentResult result);
    IReadOnlyList<TuningExperimentResult> GetHistory(int limit = 100);
}

/// <summary>Eine wiederholbare Messung unter vergleichbaren Bedingungen; liefert einen Messwert je Aufruf.</summary>
public sealed record TuningWorkload(string Name, string Unit, bool HigherIsBetter, TuningDimension Dimension, Func<CancellationToken, Task<double>> Measure);

/// <summary>
/// Messgetriebenes Tuning (Abschnitt 22.2): Ausgangswert messen → genau eine umkehrbare Änderung anwenden → erneut messen →
/// mit Unsicherheitsabschätzung vergleichen → Beibehalten oder Zurücknehmen empfehlen → Ergebnis festhalten.
/// </summary>
public sealed class TuningExperimentRunner
{
    private readonly OptimizationPlanner _planner;
    private readonly OptimizationExecutor _executor;
    private readonly RollbackService _rollback;
    private readonly IFeedbackStore _feedback;
    private readonly ITuningStore? _store;

    public TuningExperimentRunner(OptimizationPlanner planner, OptimizationExecutor executor, RollbackService rollback, IFeedbackStore feedback, ITuningStore? store = null)
    {
        _planner = planner;
        _executor = executor;
        _rollback = rollback;
        _feedback = feedback;
        _store = store;
    }

    public async Task<TuningExperimentResult> RunAsync(PlanRequest change, TuningWorkload workload, RiskPolicy policy, SystemContext context, OptimizationGoal goal,
        int runs = 5, bool revertWhenNotBeneficial = true, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        runs = Math.Clamp(runs, MeasurementComparer.MinimumSamples, 15);

        var plan = await _planner.BuildAsync(new[] { change }, policy, context, ExecutionSource.Tuning, cancellationToken).ConfigureAwait(false);
        if (plan.IsEmpty)
        {
            var reason = plan.Rejected.FirstOrDefault()?.Reason ?? "Die Änderung kann nicht geplant werden.";
            throw new InvalidOperationException(reason);
        }
        var step = plan.Steps[0];
        if (!step.Metadata.IsReversible) throw new InvalidOperationException("Für Tuning-Experimente sind nur umkehrbare Änderungen zulässig.");
        if (step.Metadata.Restart != RestartRequirement.None)
            throw new InvalidOperationException("Diese Änderung wird erst nach Neustart oder Neuanmeldung wirksam und kann deshalb nicht in einem laufenden Experiment gemessen werden.");

        async Task<List<double>> MeasureAsync(string phase)
        {
            var values = new List<double>(runs);
            for (var i = 0; i < runs; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"{phase}: Messung {i + 1} von {runs} …");
                values.Add(await workload.Measure(cancellationToken).ConfigureAwait(false));
            }
            return values;
        }

        var baseline = await MeasureAsync("Ausgangswert").ConfigureAwait(false);

        progress?.Report("Änderung wird angewendet …");
        var batch = await _executor.ExecuteAsync(plan, new ExecutionOptions { SourceLabel = "Tuning-Experiment", IndividuallyConfirmed = plan.Steps.Select(s => s.Key).ToHashSet() }, cancellationToken).ConfigureAwait(false);
        if (!batch.AllSucceeded)
            throw new InvalidOperationException("Die Änderung konnte nicht angewendet werden: " + (batch.Steps.FirstOrDefault()?.Message ?? "Es läuft bereits ein Vorgang."));

        List<double> candidate;
        try
        {
            candidate = await MeasureAsync("Nach der Änderung").ConfigureAwait(false);
        }
        catch
        {
            // Abgebrochene oder fehlgeschlagene Messung: Änderung nicht ungeprüft bestehen lassen.
            await _rollback.RollbackBatchAsync(batch.BatchId).ConfigureAwait(false);
            throw;
        }

        var comparison = MeasurementComparer.Compare(baseline, candidate, workload.HigherIsBetter);
        var score = MultiObjectiveScorer.Score(new[]
        {
            new DimensionChange(workload.Dimension, comparison.ChangePercent, workload.HigherIsBetter, comparison.Verdict is ComparisonVerdict.Improved or ComparisonVerdict.Regressed),
        }, new[] { goal });

        var recommendation = comparison.Verdict switch
        {
            ComparisonVerdict.InsufficientData => TuningRecommendation.Inconclusive,
            ComparisonVerdict.NoSignificantChange => TuningRecommendation.NoMeasurableEffect,
            _ => score.Score > 0 ? TuningRecommendation.Keep : TuningRecommendation.Revert,
        };

        var reverted = false;
        if (revertWhenNotBeneficial && recommendation != TuningRecommendation.Keep)
        {
            progress?.Report("Änderung wird zurückgenommen …");
            var outcomes = await _rollback.RollbackBatchAsync(batch.BatchId).ConfigureAwait(false);
            reverted = outcomes.Count > 0 && outcomes.All(o => o.Success);
        }

        if (recommendation == TuningRecommendation.Revert) _feedback.Record(step.Metadata.Id, FeedbackKind.Regressed, comparison.Explanation);
        else if (recommendation == TuningRecommendation.Keep) _feedback.Record(step.Metadata.Id, FeedbackKind.Helpful, comparison.Explanation);

        var summary = recommendation switch
        {
            TuningRecommendation.Keep => $"Beibehalten empfohlen. {comparison.Explanation}",
            TuningRecommendation.Revert => $"Zurücknehmen empfohlen. {comparison.Explanation}",
            TuningRecommendation.NoMeasurableEffect => $"Kein messbarer Effekt. {comparison.Explanation}" + (reverted ? " Die Änderung wurde zurückgenommen, da sie keinen nachweisbaren Nutzen bringt." : ""),
            _ => comparison.Explanation,
        };

        var result = new TuningExperimentResult
        {
            Timestamp = DateTimeOffset.UtcNow,
            ActionId = step.Metadata.Id,
            ActionName = step.Preview.Title,
            Workload = workload.Name,
            Baseline = baseline,
            Candidate = candidate,
            Unit = workload.Unit,
            Comparison = comparison,
            Recommendation = recommendation,
            Summary = summary,
            Reverted = reverted,
            BatchId = batch.BatchId,
        };
        _store?.Save(result);
        return result;
    }
}
