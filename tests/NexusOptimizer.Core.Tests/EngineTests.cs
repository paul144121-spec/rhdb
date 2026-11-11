using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Tests;

public class RiskPolicyTests
{
    private static ActionMetadata Meta(RiskLevel risk, bool reversible = true, bool experimental = false, bool excluded = false) => new()
    {
        Id = "t.a", Name = "T", Description = "D", Category = ActionCategory.Interface, Risk = risk, IsReversible = reversible, IsExperimental = experimental, ExcludeFromAutomation = excluded,
    };

    [Theory]
    [InlineData(RiskLevel.VeryLow, RiskLevel.Low, true)]
    [InlineData(RiskLevel.Low, RiskLevel.Low, true)]
    [InlineData(RiskLevel.Moderate, RiskLevel.Low, false)]
    [InlineData(RiskLevel.High, RiskLevel.Moderate, false)]
    [InlineData(RiskLevel.High, RiskLevel.High, true)]
    public void Interactive_execution_respects_user_limit(RiskLevel risk, RiskLevel limit, bool expected)
    {
        var policy = new RiskPolicy { MaxInteractiveRisk = limit };
        Assert.Equal(expected, policy.Evaluate(Meta(risk), risk, ExecutionSource.User).Allowed);
    }

    [Theory]
    [InlineData(ExecutionSource.Automation)]
    [InlineData(ExecutionSource.AiAuto)]
    [InlineData(ExecutionSource.Gaming)]
    public void Unattended_sources_never_exceed_low_risk_even_if_configured_higher(ExecutionSource source)
    {
        var policy = new RiskPolicy { MaxInteractiveRisk = RiskLevel.Critical, MaxUnattendedRisk = RiskLevel.Critical, ExperimentalUnlocked = true };
        Assert.True(policy.Evaluate(Meta(RiskLevel.Low), RiskLevel.Low, source).Allowed);
        Assert.False(policy.Evaluate(Meta(RiskLevel.Moderate), RiskLevel.Moderate, source).Allowed);
        Assert.False(policy.Evaluate(Meta(RiskLevel.VeryLow, reversible: false), RiskLevel.VeryLow, source).Allowed);
        Assert.False(policy.Evaluate(Meta(RiskLevel.VeryLow, experimental: true), RiskLevel.VeryLow, source).Allowed);
        Assert.False(policy.Evaluate(Meta(RiskLevel.VeryLow, excluded: true), RiskLevel.VeryLow, source).Allowed);
    }

    [Theory]
    [InlineData(ExecutionSource.OneClick)]
    [InlineData(ExecutionSource.Profile)]
    [InlineData(ExecutionSource.AiAssisted)]
    [InlineData(ExecutionSource.Chat)]
    [InlineData(ExecutionSource.Automation)]
    [InlineData(ExecutionSource.AiAuto)]
    public void Experimental_actions_are_only_available_manually(ExecutionSource source)
    {
        var policy = new RiskPolicy { MaxInteractiveRisk = RiskLevel.Critical, ExperimentalUnlocked = true };
        Assert.False(policy.Evaluate(Meta(RiskLevel.Moderate, experimental: true), RiskLevel.Moderate, source).Allowed);
    }

    [Fact]
    public void Experimental_actions_require_unlock_and_individual_confirmation()
    {
        var meta = Meta(RiskLevel.Critical, experimental: true);
        Assert.False(new RiskPolicy { MaxInteractiveRisk = RiskLevel.Critical }.Evaluate(meta, RiskLevel.Critical, ExecutionSource.User).Allowed);

        var decision = new RiskPolicy { MaxInteractiveRisk = RiskLevel.Critical, ExperimentalUnlocked = true }.Evaluate(meta, RiskLevel.Critical, ExecutionSource.User);
        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresIndividualConfirmation);
    }

    [Fact]
    public void Effective_risk_can_raise_but_never_lower_the_declared_risk()
    {
        var policy = new RiskPolicy { MaxInteractiveRisk = RiskLevel.Low };
        Assert.False(policy.Evaluate(Meta(RiskLevel.Low), RiskLevel.High, ExecutionSource.User).Allowed);
        Assert.False(policy.Evaluate(Meta(RiskLevel.High), RiskLevel.VeryLow, ExecutionSource.User).Allowed);
    }

    [Fact]
    public void Blocked_actions_are_denied_and_allowlist_is_enforced_for_unattended()
    {
        var blocked = new RiskPolicy { Blocked = new HashSet<string> { "t.a" } };
        Assert.False(blocked.Evaluate(Meta(RiskLevel.VeryLow), RiskLevel.VeryLow, ExecutionSource.User).Allowed);

        var listed = new RiskPolicy { MaxUnattendedRisk = RiskLevel.Low, UnattendedAllowList = new HashSet<string> { "other" } };
        Assert.False(listed.Evaluate(Meta(RiskLevel.VeryLow), RiskLevel.VeryLow, ExecutionSource.Automation).Allowed);
    }
}

public class PlannerTests
{
    [Fact]
    public async Task Unknown_and_malformed_action_ids_are_rejected()
    {
        var harness = new EngineHarness(new FakeAction("a.one"));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.one", "does.not.exist", "rm -rf /; echo", "");

        Assert.Single(plan.Steps);
        Assert.Equal(3, plan.Rejected.Count(r => r.Kind == RejectionKind.UnknownAction));
    }

    [Fact]
    public async Task Dependencies_are_added_and_ordered_first()
    {
        var harness = new EngineHarness(new FakeAction("a.dependent", dependencies: new[] { "a.base" }), new FakeAction("a.base"));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.dependent");

        Assert.Equal(new[] { "a.base", "a.dependent" }, plan.Steps.Select(s => s.Metadata.Id));
    }

    [Fact]
    public async Task Already_satisfied_dependency_is_not_re_applied()
    {
        var harness = new EngineHarness(new FakeAction("a.dependent", dependencies: new[] { "a.base" }), new FakeAction("a.base") { State = "neu" });
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.dependent");

        Assert.Equal(new[] { "a.dependent" }, plan.Steps.Select(s => s.Metadata.Id));
    }

    [Fact]
    public async Task Step_is_rejected_when_its_dependency_is_not_permitted()
    {
        var harness = new EngineHarness(new FakeAction("a.dependent", dependencies: new[] { "a.risky" }), new FakeAction("a.risky", RiskLevel.High));
        var plan = await harness.PlanAsync(new RiskPolicy { MaxInteractiveRisk = RiskLevel.Low }, ExecutionSource.User, "a.dependent");

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Rejected, r => r.ActionId == "a.dependent" && r.Kind == RejectionKind.DependencyFailed);
    }

    [Fact]
    public async Task Conflicting_actions_keep_only_the_first_requested()
    {
        var harness = new EngineHarness(new FakeAction("a.one", conflicts: new[] { "a.two" }), new FakeAction("a.two"));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.two", "a.one");

        Assert.Equal(new[] { "a.two" }, plan.Steps.Select(s => s.Metadata.Id));
        Assert.Contains(plan.Rejected, r => r.ActionId == "a.one" && r.Kind == RejectionKind.Conflict);
    }

    [Fact]
    public async Task Same_action_with_different_values_for_same_target_is_a_conflict()
    {
        var specs = new[] { new ParameterSpec("ziel", ParameterType.Text, "Ziel", IsTargetKey: true), new ParameterSpec("wert", ParameterType.Integer, "Wert") };
        var harness = new EngineHarness(new FakeAction("a.param", parameters: specs));
        var requests = new[]
        {
            new PlanRequest("a.param", ActionParameters.Of(("ziel", "x"), ("wert", "1"))),
            new PlanRequest("a.param", ActionParameters.Of(("ziel", "x"), ("wert", "2"))),
            new PlanRequest("a.param", ActionParameters.Of(("ziel", "y"), ("wert", "2"))),
        };

        var plan = await harness.Planner.BuildAsync(requests, EngineHarness.Permissive, harness.Context, ExecutionSource.User);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Single(plan.Rejected, r => r.Kind == RejectionKind.Conflict);
    }

    [Fact]
    public async Task Unsupported_and_already_applied_actions_are_not_planned()
    {
        var harness = new EngineHarness(
            new FakeAction("a.unsupported") { Support = SupportResult.NotSupported("Nicht auf diesem System.") },
            new FakeAction("a.done") { State = "neu" });
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.unsupported", "a.done");

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Rejected, r => r.Kind == RejectionKind.Unsupported);
        Assert.Contains(plan.Rejected, r => r.Kind == RejectionKind.AlreadyApplied);
    }

    [Fact]
    public async Task Invalid_parameters_are_rejected_before_execution()
    {
        var specs = new[] { new ParameterSpec("wert", ParameterType.Integer, "Wert", Min: 0, Max: 100) };
        var harness = new EngineHarness(new FakeAction("a.param", parameters: specs));
        var requests = new[]
        {
            new PlanRequest("a.param", ActionParameters.Of(("wert", "101"))),
            new PlanRequest("a.param", ActionParameters.Of(("wert", "5"), ("extra", "x"))),
            new PlanRequest("a.param", ActionParameters.Empty),
        };

        var plan = await harness.Planner.BuildAsync(requests, EngineHarness.Permissive, harness.Context, ExecutionSource.User);

        Assert.True(plan.IsEmpty);
        Assert.Equal(3, plan.Rejected.Count(r => r.Kind == RejectionKind.InvalidParameters));
    }

    [Fact]
    public async Task Automation_cannot_plan_risky_or_irreversible_actions()
    {
        var harness = new EngineHarness(new FakeAction("a.safe"), new FakeAction("a.moderate", RiskLevel.Moderate), new FakeAction("a.irreversible", reversible: false));
        var plan = await harness.PlanAsync(null, ExecutionSource.Automation, "a.safe", "a.moderate", "a.irreversible");

        Assert.Equal(new[] { "a.safe" }, plan.Steps.Select(s => s.Metadata.Id));
        Assert.Equal(2, plan.Rejected.Count(r => r.Kind == RejectionKind.NotPermitted));
    }

    [Fact]
    public async Task Subset_drops_steps_whose_dependency_was_deselected()
    {
        var harness = new EngineHarness(new FakeAction("a.dependent", dependencies: new[] { "a.base" }), new FakeAction("a.base"), new FakeAction("a.other"));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.dependent", "a.other");

        var subset = plan.Subset(plan.Steps.Where(s => s.Metadata.Id != "a.base").Select(s => s.Key));

        Assert.Equal(new[] { "a.other" }, subset.Steps.Select(s => s.Metadata.Id));
    }
}

public class ExecutorTests
{
    [Fact]
    public async Task Successful_step_is_backed_up_applied_verified_and_journaled()
    {
        var action = new FakeAction("a.one");
        var harness = new EngineHarness(action);
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.one");

        var result = await harness.Executor.ExecuteAsync(plan);

        Assert.True(result.AllSucceeded);
        Assert.Equal(new[] { "backup", "apply", "verify" }, action.Calls);
        var record = Assert.Single(harness.Journal.GetRecent());
        Assert.Equal(TransactionStatus.Verified, record.Status);
        Assert.Equal("alt", record.BackupJson);
        Assert.Equal(result.BatchId, record.BatchId);
    }

    [Fact]
    public async Task Failed_verification_is_never_reported_as_success()
    {
        var harness = new EngineHarness(new FakeAction("a.one") { CorruptOnApply = true });
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.one");

        var result = await harness.Executor.ExecuteAsync(plan);

        Assert.False(result.AllSucceeded);
        Assert.False(result.Steps[0].Success);
        Assert.Equal(TransactionStatus.VerificationFailed, harness.Journal.GetRecent()[0].Status);
    }

    [Fact]
    public async Task Failed_apply_is_journaled_and_original_state_is_kept()
    {
        var action = new FakeAction("a.one") { FailApply = true };
        var harness = new EngineHarness(action);
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.one");

        var result = await harness.Executor.ExecuteAsync(plan);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal("alt", action.State);
        Assert.Equal(TransactionStatus.Failed, harness.Journal.GetRecent()[0].Status);
    }

    [Fact]
    public async Task Exception_during_apply_does_not_abort_remaining_steps()
    {
        var harness = new EngineHarness(new FakeAction("a.crash") { ThrowOnApply = true }, new FakeAction("a.ok"));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.crash", "a.ok");

        var result = await harness.Executor.ExecuteAsync(plan);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.SucceededCount);
        Assert.False(harness.Gate.IsBusy);
    }

    [Fact]
    public async Task Dependent_step_is_skipped_when_dependency_failed()
    {
        var dependent = new FakeAction("a.dependent", dependencies: new[] { "a.base" });
        var harness = new EngineHarness(dependent, new FakeAction("a.base") { FailApply = true });
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.dependent");

        var result = await harness.Executor.ExecuteAsync(plan);

        Assert.Equal(2, result.FailedCount);
        Assert.Empty(dependent.Calls);
    }

    [Fact]
    public async Task Rollback_all_on_failure_restores_previous_steps_in_reverse_order()
    {
        var first = new FakeAction("a.first");
        var harness = new EngineHarness(first, new FakeAction("a.second") { FailApply = true });
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.first", "a.second");

        var result = await harness.Executor.ExecuteAsync(plan, new ExecutionOptions { RollbackAllOnFailure = true });

        Assert.True(result.RolledBackAfterFailure);
        Assert.Equal("alt", first.State);
        Assert.Equal(TransactionStatus.RolledBack, harness.Journal.GetRecent().Single(r => r.ActionId == "a.first").Status);
    }

    [Fact]
    public async Task Individually_confirmable_step_is_skipped_without_confirmation()
    {
        var action = new FakeAction("a.high", RiskLevel.High);
        var harness = new EngineHarness(action);
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.high");
        Assert.True(plan.Steps[0].RequiresIndividualConfirmation);

        var skipped = await harness.Executor.ExecuteAsync(plan);
        Assert.False(skipped.AllSucceeded);
        Assert.Empty(action.Calls);

        var confirmed = await harness.Executor.ExecuteAsync(plan, new ExecutionOptions { IndividuallyConfirmed = new HashSet<string> { plan.Steps[0].Key } });
        Assert.True(confirmed.AllSucceeded);
    }

    [Fact]
    public async Task Concurrent_run_is_rejected_as_busy()
    {
        var harness = new EngineHarness(new FakeAction("a.one"));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.one");
        Assert.True(harness.Gate.TryEnter());
        try
        {
            var result = await harness.Executor.ExecuteAsync(plan);
            Assert.True(result.Busy);
            Assert.Empty(harness.Journal.GetRecent());
        }
        finally { harness.Gate.Exit(); }
    }

    [Fact]
    public async Task Cancellation_stops_before_next_step_but_never_mid_step()
    {
        var harness = new EngineHarness(new FakeAction("a.one"), new FakeAction("a.two"));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.one", "a.two");
        using var cts = new CancellationTokenSource();
        var options = new ExecutionOptions { Progress = new SyncProgress(p => { if (p.Phase == "Wird verifiziert") cts.Cancel(); }) };

        var result = await harness.Executor.ExecuteAsync(plan, options, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Single(result.Steps);
        Assert.True(result.Steps[0].Success);
    }

    [Fact]
    public async Task Missing_privilege_fails_step_and_skips_remaining_admin_steps()
    {
        var harness = new EngineHarness(
            catalog => new RoutingActionRunner(catalog, new LocalActionRunner(catalog), gateway: null, isElevated: () => false),
            new FakeAction("a.admin1") { Privilege = PrivilegeLevel.Administrator }, new FakeAction("a.user"), new FakeAction("a.admin2") { Privilege = PrivilegeLevel.Administrator });
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.admin1", "a.user", "a.admin2");

        var result = await harness.Executor.ExecuteAsync(plan);

        Assert.Equal(new[] { false, true, false }, result.Steps.Select(s => s.Success));
        Assert.Null(result.Steps[2].Transaction);
    }

    private sealed class SyncProgress : IProgress<ExecutionProgress>
    {
        private readonly Action<ExecutionProgress> _handler;
        public SyncProgress(Action<ExecutionProgress> handler) => _handler = handler;
        public void Report(ExecutionProgress value) => _handler(value);
    }
}

public class RollbackServiceTests
{
    [Fact]
    public async Task Rollback_last_restores_state_and_updates_journal()
    {
        var action = new FakeAction("a.one");
        var harness = new EngineHarness(action);
        await harness.Executor.ExecuteAsync(await harness.PlanAsync(null, ExecutionSource.User, "a.one"));

        var outcome = await harness.Rollback.RollbackLastAsync();

        Assert.True(outcome!.Success);
        Assert.Equal("alt", action.State);
        Assert.Equal(TransactionStatus.RolledBack, harness.Journal.GetRecent()[0].Status);
        Assert.Null(harness.Rollback.FindLastActive());
    }

    [Fact]
    public async Task Rollback_survives_application_restart_using_only_the_journal()
    {
        var action = new FakeAction("a.one");
        var harness = new EngineHarness(action);
        await harness.Executor.ExecuteAsync(await harness.PlanAsync(null, ExecutionSource.User, "a.one"));

        // „Neustart“: neue Engine-Instanzen, gleiches Journal und gleicher Systemzustand.
        var executor = new OptimizationExecutor(new LocalActionRunner(harness.Catalog), harness.Journal, new ExecutionGate());
        var service = new RollbackService(executor, harness.Journal);

        var outcome = await service.RollbackAsync(harness.Journal.GetRecent()[0].Id);

        Assert.True(outcome.Success);
        Assert.Equal("alt", action.State);
    }

    [Fact]
    public async Task External_change_is_detected_as_conflict()
    {
        var action = new FakeAction("a.one");
        var harness = new EngineHarness(action);
        await harness.Executor.ExecuteAsync(await harness.PlanAsync(null, ExecutionSource.User, "a.one"));
        action.State = "fremd";

        var cautious = await harness.Rollback.RollbackLastAsync();
        Assert.True(cautious!.Conflict);
        Assert.Equal("fremd", action.State);

        var forced = await harness.Rollback.RollbackAsync(cautious.Transaction.Id, force: true);
        Assert.True(forced.Success);
        Assert.Equal("alt", action.State);
    }

    [Fact]
    public async Task Irreversible_change_cannot_be_rolled_back()
    {
        var harness = new EngineHarness(new FakeAction("a.irreversible", reversible: false));
        var plan = await harness.PlanAsync(null, ExecutionSource.User, "a.irreversible");
        await harness.Executor.ExecuteAsync(plan, new ExecutionOptions { IndividuallyConfirmed = plan.Steps.Select(s => s.Key).ToHashSet() });

        var outcome = await harness.Rollback.RollbackAsync(harness.Journal.GetRecent()[0].Id);

        Assert.False(outcome.Success);
        Assert.Null(harness.Rollback.FindLastActive());
    }

    [Fact]
    public async Task Failed_rollback_is_recorded()
    {
        var action = new FakeAction("a.one");
        var harness = new EngineHarness(action);
        await harness.Executor.ExecuteAsync(await harness.PlanAsync(null, ExecutionSource.User, "a.one"));
        action.FailRollback = true;

        var outcome = await harness.Rollback.RollbackLastAsync();

        Assert.False(outcome!.Success);
        Assert.Equal(TransactionStatus.RollbackFailed, harness.Journal.GetRecent()[0].Status);
    }

    [Fact]
    public async Task Batch_rollback_runs_in_reverse_order()
    {
        var first = new FakeAction("a.first");
        var second = new FakeAction("a.second");
        var harness = new EngineHarness(first, second);
        var batch = await harness.Executor.ExecuteAsync(await harness.PlanAsync(null, ExecutionSource.User, "a.first", "a.second"));

        var outcomes = await harness.Rollback.RollbackBatchAsync(batch.BatchId);

        Assert.Equal(new[] { "a.second", "a.first" }, outcomes.Select(o => o.Transaction.ActionId));
        Assert.All(outcomes, o => Assert.True(o.Success));
    }

    [Fact]
    public void Interrupted_transactions_are_flagged_on_startup()
    {
        var harness = new EngineHarness(new FakeAction("a.one"));
        TransactionRecord Make(string id, TransactionStatus status) => new()
        {
            Id = id, BatchId = "b", Timestamp = DateTimeOffset.UtcNow, ActionId = "a.one", ActionName = "T", Status = status, IsReversible = true, BackupJson = status == TransactionStatus.Pending ? null : "alt",
        };
        harness.Journal.Insert(Make("1", TransactionStatus.Pending));
        harness.Journal.Insert(Make("2", TransactionStatus.BackedUp));
        harness.Journal.Insert(Make("3", TransactionStatus.Applied));
        harness.Journal.Insert(Make("4", TransactionStatus.Verified));

        var recovered = harness.Rollback.RecoverInterrupted();

        Assert.Equal(3, recovered.Count);
        Assert.Equal(TransactionStatus.Failed, harness.Journal.Get("1")!.Status);
        Assert.Equal(TransactionStatus.Interrupted, harness.Journal.Get("2")!.Status);
        Assert.Equal(TransactionStatus.Interrupted, harness.Journal.Get("3")!.Status);
        Assert.True(harness.Journal.Get("3")!.CanRollback);
        Assert.Equal(TransactionStatus.Verified, harness.Journal.Get("4")!.Status);
    }

    [Fact]
    public async Task Interrupted_transaction_can_be_rolled_back_after_restart()
    {
        var action = new FakeAction("a.one") { State = "neu" }; // Änderung wurde vor dem Absturz noch geschrieben
        var harness = new EngineHarness(action);
        harness.Journal.Insert(new TransactionRecord
        {
            Id = "x", BatchId = "b", Timestamp = DateTimeOffset.UtcNow, ActionId = "a.one", ActionName = "T", Status = TransactionStatus.BackedUp, IsReversible = true, BackupJson = "alt",
        });
        harness.Rollback.RecoverInterrupted();

        var outcome = await harness.Rollback.RollbackAsync("x");

        Assert.True(outcome.Success);
        Assert.Equal("alt", action.State);
    }
}

public class ParameterValidatorTests
{
    [Theory]
    [InlineData(@"C:\Spiele\spiel.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Spiel\bin\game.exe", true)]
    [InlineData(@"spiel.exe", false)]
    [InlineData(@"\\server\freigabe\spiel.exe", false)]
    [InlineData(@"C:\Spiele\..\Windows\System32\cmd.exe", false)]
    [InlineData("C:\\Spiele\\a\"b.exe", false)]
    [InlineData(@"C:\Spiele\spiel.exe:stream", false)]
    [InlineData(@"C:/Spiele/spiel.exe", false)]
    [InlineData(@"C:\Spiele\\spiel.exe", false)]
    [InlineData(@"C:\Spiele\spiel.exe ", false)]
    public void File_paths_are_validated_strictly(string path, bool valid)
        => Assert.Equal(valid, NexusOptimizer.Core.Security.ParameterValidator.ValidateFilePath(path) is null);

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("1.1.1.1,8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("1", false)]
    [InlineData("1.1.1", false)]
    [InlineData("example.com", false)]
    [InlineData("1.1.1.1; shutdown /s", false)]
    [InlineData("1.1.1.1,2.2.2.2,3.3.3.3,4.4.4.4,5.5.5.5", false)]
    public void Ip_lists_are_validated_strictly(string value, bool valid)
    {
        var spec = new ParameterSpec("dns", ParameterType.IpAddressList, "DNS");
        Assert.Equal(valid, NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, value) is null);
    }

    [Fact]
    public void Control_characters_and_overlong_values_are_rejected()
    {
        var spec = new ParameterSpec("name", ParameterType.Text, "Name", MaxLength: 10);
        Assert.NotNull(NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, "ab\ncd"));
        Assert.NotNull(NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, new string('a', 11)));
        Assert.Null(NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, "gültig"));
    }

    [Fact]
    public void Choice_lists_reject_unknown_and_duplicate_entries()
    {
        var spec = new ParameterSpec("kategorien", ParameterType.ChoiceList, "Kategorien", Choices: new[] { "a", "b" });
        Assert.Null(NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, "a,b"));
        Assert.NotNull(NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, "a,c"));
        Assert.NotNull(NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, "a,a"));
        Assert.NotNull(NexusOptimizer.Core.Security.ParameterValidator.ValidateValue(spec, ""));
    }
}
