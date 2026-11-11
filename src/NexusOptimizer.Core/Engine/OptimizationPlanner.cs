using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Engine;

public sealed record PlanRequest(string ActionId, ActionParameters Parameters, string? Reason = null)
{
    public PlanRequest(string actionId) : this(actionId, ActionParameters.Empty) { }
    public string Key => ActionId + "|" + Parameters.ToJson();
}

public enum RejectionKind
{
    UnknownAction,
    InvalidParameters,
    Unsupported,
    NotPermitted,
    Conflict,
    AlreadyApplied,
    DependencyFailed,
    StateUnavailable,
}

public sealed record RejectedItem(string ActionId, string Name, RejectionKind Kind, string Reason);

public sealed record PlanStep
{
    public required ActionMetadata Metadata { get; init; }
    public required ActionParameters Parameters { get; init; }
    public required ChangePreview Preview { get; init; }
    public required RiskAssessment Risk { get; init; }
    public required PrivilegeLevel Privilege { get; init; }
    public bool RequiresIndividualConfirmation { get; init; }
    public string? Reason { get; init; }
    public string Key => Metadata.Id + "|" + Parameters.ToJson();
}

/// <summary>Geprüfter, unveränderlicher Ausführungsplan. Kann nur vom <see cref="OptimizationPlanner"/> erzeugt werden.</summary>
public sealed class OptimizationPlan
{
    internal OptimizationPlan(ExecutionSource source, IReadOnlyList<PlanStep> steps, IReadOnlyList<RejectedItem> rejected)
    {
        Source = source;
        Steps = steps;
        Rejected = rejected;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ExecutionSource Source { get; }
    public IReadOnlyList<PlanStep> Steps { get; }
    public IReadOnlyList<RejectedItem> Rejected { get; }
    public bool IsEmpty => Steps.Count == 0;
    public bool RequiresElevation => Steps.Any(s => s.Privilege == PrivilegeLevel.Administrator);
    public RestartRequirement Restart => Steps.Count == 0 ? RestartRequirement.None : Steps.Max(s => s.Metadata.Restart);
    public RiskLevel HighestRisk => Steps.Count == 0 ? RiskLevel.VeryLow : Steps.Max(s => s.Risk.Level);
    public bool ContainsIrreversible => Steps.Any(s => !s.Metadata.IsReversible);

    /// <summary>Erzeugt einen Teilplan mit den vom Benutzer ausgewählten Schritten; Reihenfolge und Prüfungen bleiben erhalten.</summary>
    public OptimizationPlan Subset(IEnumerable<string> stepKeys)
    {
        var keys = new HashSet<string>(stepKeys, StringComparer.Ordinal);
        var kept = Steps.Where(s => keys.Contains(s.Key)).ToList();
        // Schritte, deren Abhängigkeit abgewählt wurde, entfallen ebenfalls.
        bool changed;
        do
        {
            changed = false;
            var ids = kept.Select(s => s.Metadata.Id).ToHashSet(StringComparer.Ordinal);
            var allIds = Steps.Select(s => s.Metadata.Id).ToHashSet(StringComparer.Ordinal);
            for (var i = kept.Count - 1; i >= 0; i--)
            {
                if (kept[i].Metadata.Dependencies.Any(d => allIds.Contains(d) && !ids.Contains(d)))
                {
                    kept.RemoveAt(i);
                    changed = true;
                }
            }
        } while (changed);
        return new OptimizationPlan(Source, kept, Rejected);
    }
}

/// <summary>
/// Plant einen Optimierungslauf (Abschnitt 33.1): Abhängigkeiten auflösen, Konflikte erkennen, Kompatibilität und
/// Risikofreigabe prüfen, Ausführungsreihenfolge bestimmen und für jeden Schritt eine Vorschau erzeugen.
/// </summary>
public sealed class OptimizationPlanner
{
    private readonly ActionCatalog _catalog;

    public OptimizationPlanner(ActionCatalog catalog) => _catalog = catalog;

    public async Task<OptimizationPlan> BuildAsync(IEnumerable<PlanRequest> requests, RiskPolicy policy, SystemContext context, ExecutionSource source, CancellationToken cancellationToken = default)
    {
        var rejected = new List<RejectedItem>();
        var accepted = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(request.Key)) continue;
            var candidate = await EvaluateAsync(request, policy, context, source, rejected, cancellationToken).ConfigureAwait(false);
            if (candidate is not null) accepted.Add(candidate);
        }

        await ResolveDependenciesAsync(accepted, rejected, seen, policy, context, source, cancellationToken).ConfigureAwait(false);
        RemoveConflicts(accepted, rejected);
        // Nach dem Entfernen von Konflikten können Abhängigkeiten weggefallen sein.
        RemoveOrphans(accepted, rejected);

        var ordered = TopologicalOrder(accepted);
        var steps = ordered.Select(c => new PlanStep
        {
            Metadata = c.Action.Metadata,
            Parameters = c.Request.Parameters,
            Preview = c.Preview,
            Risk = c.Risk,
            Privilege = c.Action.GetRequiredPrivilege(c.Request.Parameters),
            RequiresIndividualConfirmation = c.Decision.RequiresIndividualConfirmation,
            Reason = c.Request.Reason,
        }).ToList();

        return new OptimizationPlan(source, steps, rejected);
    }

    private sealed record Candidate(PlanRequest Request, IOptimizationAction Action, ChangePreview Preview, RiskAssessment Risk, PermissionDecision Decision, bool AlreadyApplied);

    private async Task<Candidate?> EvaluateAsync(PlanRequest request, RiskPolicy policy, SystemContext context, ExecutionSource source, List<RejectedItem> rejected, CancellationToken cancellationToken, bool keepIfApplied = false)
    {
        if (!ActionCatalog.IsValidActionId(request.ActionId) || _catalog.Find(request.ActionId) is not { } action)
        {
            rejected.Add(new RejectedItem(Shorten(request.ActionId), "Unbekannte Aktion", RejectionKind.UnknownAction, "Diese Aktion ist nicht implementiert und kann nicht ausgeführt werden."));
            return null;
        }

        var metadata = action.Metadata;
        var validation = action.ValidateParameters(request.Parameters);
        if (!validation.IsValid)
        {
            rejected.Add(new RejectedItem(metadata.Id, metadata.Name, RejectionKind.InvalidParameters, validation.ErrorText));
            return null;
        }

        var support = await action.IsSupportedAsync(context, request.Parameters, cancellationToken).ConfigureAwait(false);
        if (!support.IsSupported)
        {
            rejected.Add(new RejectedItem(metadata.Id, metadata.Name, RejectionKind.Unsupported, support.Reason));
            return null;
        }

        var risk = action.EvaluateRisk(request.Parameters, context);
        var decision = policy.Evaluate(metadata, risk.Level, source);
        if (!decision.Allowed)
        {
            rejected.Add(new RejectedItem(metadata.Id, metadata.Name, RejectionKind.NotPermitted, decision.Reason));
            return null;
        }

        ChangePreview preview;
        try
        {
            preview = await action.PreviewAsync(request.Parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rejected.Add(new RejectedItem(metadata.Id, metadata.Name, RejectionKind.StateUnavailable, "Der aktuelle Zustand konnte nicht ermittelt werden: " + ex.Message));
            return null;
        }

        if (!preview.WillChange)
        {
            if (keepIfApplied) return new Candidate(request, action, preview, risk, decision, AlreadyApplied: true);
            rejected.Add(new RejectedItem(metadata.Id, metadata.Name, RejectionKind.AlreadyApplied, "Der Zielzustand liegt bereits vor – keine Änderung nötig."));
            return null;
        }

        return new Candidate(request, action, preview, risk, decision, AlreadyApplied: false);
    }

    private async Task ResolveDependenciesAsync(List<Candidate> accepted, List<RejectedItem> rejected, HashSet<string> seen, RiskPolicy policy, SystemContext context, ExecutionSource source, CancellationToken cancellationToken)
    {
        var satisfied = new HashSet<string>(StringComparer.Ordinal);
        var guard = 0;
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in accepted.ToList())
            {
                foreach (var dependencyId in candidate.Action.Metadata.Dependencies)
                {
                    if (satisfied.Contains(dependencyId) || accepted.Any(c => c.Action.Metadata.Id == dependencyId)) continue;

                    var dependencyRequest = new PlanRequest(dependencyId, ActionParameters.Empty, $"Voraussetzung für „{candidate.Action.Metadata.Name}“");
                    seen.Add(dependencyRequest.Key);
                    var scratch = new List<RejectedItem>();
                    var dependency = await EvaluateAsync(dependencyRequest, policy, context, source, scratch, cancellationToken, keepIfApplied: true).ConfigureAwait(false);

                    if (dependency is null)
                    {
                        accepted.Remove(candidate);
                        var why = scratch.FirstOrDefault()?.Reason ?? "unbekannter Grund";
                        rejected.Add(new RejectedItem(candidate.Action.Metadata.Id, candidate.Action.Metadata.Name, RejectionKind.DependencyFailed,
                            $"Die Voraussetzung „{dependencyId}“ kann nicht erfüllt werden: {why}"));
                        changed = true;
                        break;
                    }

                    if (dependency.AlreadyApplied) satisfied.Add(dependencyId);
                    else
                    {
                        accepted.Add(dependency);
                        changed = true;
                    }
                }
            }
        } while (changed && ++guard < 64);
    }

    private static void RemoveConflicts(List<Candidate> accepted, List<RejectedItem> rejected)
    {
        for (var i = 0; i < accepted.Count; i++)
        {
            for (var j = accepted.Count - 1; j > i; j--)
            {
                var first = accepted[i].Action.Metadata;
                var second = accepted[j].Action.Metadata;
                var sameTarget = first.Id == second.Id; // gleiche Aktion mit unterschiedlichen Parametern
                var declared = first.Conflicts.Contains(second.Id, StringComparer.Ordinal) || second.Conflicts.Contains(first.Id, StringComparer.Ordinal);
                if ((sameTarget && TargetsOverlap(accepted[i], accepted[j])) || declared)
                {
                    rejected.Add(new RejectedItem(second.Id, second.Name, RejectionKind.Conflict,
                        sameTarget ? "Dieselbe Einstellung wird in diesem Plan bereits mit anderen Werten geändert." : $"Steht im Konflikt mit „{first.Name}“."));
                    accepted.RemoveAt(j);
                }
            }
        }
    }

    /// <summary>Zwei Anforderungen derselben Aktion überschneiden sich, wenn sie dasselbe Ziel (alle „Schlüssel“-Parameter) betreffen.</summary>
    private static bool TargetsOverlap(Candidate a, Candidate b)
    {
        var targetParameters = a.Action.Metadata.Parameters.Where(p => p.IsTargetKey).Select(p => p.Name).ToList();
        if (targetParameters.Count == 0) return true;
        return targetParameters.All(name => string.Equals(a.Request.Parameters.GetString(name), b.Request.Parameters.GetString(name), StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveOrphans(List<Candidate> accepted, List<RejectedItem> rejected)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in accepted.ToList())
            {
                var missing = candidate.Action.Metadata.Dependencies.FirstOrDefault(d => rejected.Any(r => r.ActionId == d && r.Kind is RejectionKind.Conflict or RejectionKind.DependencyFailed)
                                                                                        && accepted.All(c => c.Action.Metadata.Id != d));
                if (missing is null) continue;
                accepted.Remove(candidate);
                rejected.Add(new RejectedItem(candidate.Action.Metadata.Id, candidate.Action.Metadata.Name, RejectionKind.DependencyFailed, $"Die Voraussetzung „{missing}“ wurde aus dem Plan entfernt."));
                changed = true;
            }
        } while (changed);
    }

    /// <summary>Stabile topologische Sortierung: Voraussetzungen zuerst, sonst Anforderungsreihenfolge.</summary>
    private static List<Candidate> TopologicalOrder(List<Candidate> candidates)
    {
        var result = new List<Candidate>(candidates.Count);
        var state = new Dictionary<Candidate, int>();

        void Visit(Candidate candidate)
        {
            if (state.TryGetValue(candidate, out var s))
            {
                if (s == 1) throw new InvalidOperationException($"Zyklische Abhängigkeit bei Aktion '{candidate.Action.Metadata.Id}'.");
                return;
            }
            state[candidate] = 1;
            foreach (var dependencyId in candidate.Action.Metadata.Dependencies)
            {
                foreach (var dependency in candidates.Where(c => c.Action.Metadata.Id == dependencyId)) Visit(dependency);
            }
            state[candidate] = 2;
            result.Add(candidate);
        }

        foreach (var candidate in candidates) Visit(candidate);
        return result;
    }

    private static string Shorten(string? value) => string.IsNullOrEmpty(value) ? "(leer)" : value.Length <= 60 ? value : value[..60] + "…";
}
