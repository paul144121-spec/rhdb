using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Tests;

/// <summary>Konfigurierbare Testaktion mit einem einfachen Zustandswert im Arbeitsspeicher.</summary>
internal sealed class FakeAction : OptimizationActionBase
{
    public string State { get; set; } = "alt";
    public string Target { get; init; } = "neu";
    public bool FailApply { get; set; }
    public bool ThrowOnApply { get; set; }
    public bool CorruptOnApply { get; set; }
    public bool FailRollback { get; set; }
    public SupportResult Support { get; set; } = SupportResult.Supported;
    public PrivilegeLevel Privilege { get; init; } = PrivilegeLevel.User;
    public List<string> Calls { get; } = new();
    public override ActionMetadata Metadata { get; }

    public FakeAction(string id, RiskLevel risk = RiskLevel.VeryLow, bool reversible = true, bool experimental = false,
        string[]? dependencies = null, string[]? conflicts = null, ParameterSpec[]? parameters = null, bool excludeFromAutomation = false)
    {
        Metadata = new ActionMetadata
        {
            Id = id, Name = "Test " + id, Description = "Testaktion", Category = ActionCategory.Interface, Risk = risk,
            IsReversible = reversible, IsExperimental = experimental, RequiredPrivilege = Privilege,
            Dependencies = dependencies ?? Array.Empty<string>(), Conflicts = conflicts ?? Array.Empty<string>(),
            Parameters = parameters ?? Array.Empty<ParameterSpec>(), ExcludeFromAutomation = excludeFromAutomation,
        };
    }

    public override PrivilegeLevel GetRequiredPrivilege(ActionParameters parameters) => Privilege;

    public override Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default) => Task.FromResult(Support);

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(new ActionState { DisplayValue = State, IsApplied = State == Target });

    public override Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChangePreview { ActionId = Metadata.Id, Title = Metadata.Name, CurrentValue = State, NewValue = Target, WillChange = State != Target });

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        Calls.Add("backup");
        return Task.FromResult(new BackupData(Metadata.Id, State, State, Target));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls.Add("apply");
        if (ThrowOnApply) throw new InvalidOperationException("Simulierter Absturz");
        if (FailApply) return Task.FromResult(OperationResult.Fail("Simulierter Fehler"));
        State = CorruptOnApply ? "kaputt" : Target;
        return Task.FromResult(OperationResult.Ok());
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        Calls.Add("verify");
        return Task.FromResult(State == Target ? VerificationResult.Success(Target, State) : VerificationResult.Failure(Target, State));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        Calls.Add("rollback");
        if (!Metadata.IsReversible) return Task.FromResult(NotReversible());
        if (FailRollback) return Task.FromResult(RollbackResult.Fail("Simulierter Rollback-Fehler"));
        if (State == backup.PayloadJson) return Task.FromResult(RollbackResult.Ok("Bereits im Ursprungszustand."));
        if (!force && State != Target) return Task.FromResult(RollbackResult.Conflict("Zustand wurde zwischenzeitlich geändert."));
        State = backup.PayloadJson;
        return Task.FromResult(RollbackResult.Ok());
    }
}

internal sealed class EngineHarness
{
    public ActionCatalog Catalog { get; }
    public OptimizationPlanner Planner { get; }
    public InMemoryTransactionJournal Journal { get; } = new();
    public ExecutionGate Gate { get; } = new();
    public OptimizationExecutor Executor { get; }
    public RollbackService Rollback { get; }
    public SystemContext Context { get; init; } = new() { WindowsBuild = 22631 };

    public EngineHarness(params IOptimizationAction[] actions) : this(null, actions) { }

    public EngineHarness(Func<ActionCatalog, IActionRunner>? runnerFactory, params IOptimizationAction[] actions)
    {
        Catalog = new ActionCatalog(actions);
        Planner = new OptimizationPlanner(Catalog);
        var runner = runnerFactory?.Invoke(Catalog) ?? new LocalActionRunner(Catalog);
        Executor = new OptimizationExecutor(runner, Journal, Gate);
        Rollback = new RollbackService(Executor, Journal);
    }

    public static RiskPolicy Permissive { get; } = new() { MaxInteractiveRisk = RiskLevel.High, MaxUnattendedRisk = RiskLevel.Low, ExperimentalUnlocked = true };

    public Task<OptimizationPlan> PlanAsync(RiskPolicy? policy, ExecutionSource source, params string[] actionIds)
        => Planner.BuildAsync(actionIds.Select(id => new PlanRequest(id)), policy ?? Permissive, Context, source);
}
