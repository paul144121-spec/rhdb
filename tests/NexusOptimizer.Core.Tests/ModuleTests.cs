using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Automation;
using NexusOptimizer.Core.Benchmark;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Gaming;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Optimization;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.Tuning;

namespace NexusOptimizer.Core.Tests;

public class MonitoringTests
{
    [Fact]
    public void Ring_buffer_keeps_the_newest_items_in_order()
    {
        var buffer = new RingBuffer<int>(3);
        foreach (var i in Enumerable.Range(1, 5)) buffer.Add(i);
        Assert.Equal(new[] { 3, 4, 5 }, buffer.ToArray());
    }

    [Fact]
    public void Unavailable_sensor_stays_unavailable_and_is_never_replaced_by_a_number()
    {
        var metrics = new SystemMetrics { CpuTotalPercent = 12 };
        Assert.Null(MetricCatalog.Get(MetricId.GpuTemperature).Read(metrics));
        Assert.Null(MetricCatalog.Get(MetricId.MemoryUsedPercent).Read(metrics));
        Assert.Equal("Nicht verfügbar", MetricCatalog.Get(MetricId.GpuTemperature).Format(null));
    }

    [Fact]
    public async Task Monitoring_persists_averages_and_reports_repeated_failures()
    {
        var collector = new FakeMetricsCollector();
        var history = new FakeHistoryStore();
        var time = new ManualTimeProvider();
        using var service = new MonitoringService(collector, history, null, time) { Options = new MonitoringOptions { PersistInterval = TimeSpan.FromSeconds(10) } };
        string? failure = null;
        service.CollectionFailed += (_, message) => failure = message;

        collector.Queue.Enqueue(new SystemMetrics { CpuTotalPercent = 20, Timestamp = time.Now });
        collector.Queue.Enqueue(new SystemMetrics { CpuTotalPercent = 40, Timestamp = time.Now });
        service.Start();
        await service.StopAsync(); // setzt den Persistenz-Zeitstempel
        service.SampleOnce();
        time.Advance(TimeSpan.FromSeconds(11));
        service.SampleOnce();

        var cpu = history.Appended.SelectMany(a => a.Values).Where(v => v.Key == MetricId.CpuTotal).Select(v => v.Value).ToList();
        Assert.Contains(cpu, v => v is > 9 and <= 40);
        Assert.DoesNotContain(history.Appended.SelectMany(a => a.Values.Keys), k => k == MetricId.GpuTemperature);

        collector.Throw = true;
        for (var i = 0; i < 5; i++) Assert.Null(service.SampleOnce());
        Assert.NotNull(failure);
    }

    [Fact]
    public void Polling_adapts_to_background_and_gaming_modes()
    {
        using var service = new MonitoringService(new FakeMetricsCollector()) { Options = new MonitoringOptions { Interval = TimeSpan.FromSeconds(1) } };
        Assert.Equal(TimeSpan.FromSeconds(1), service.EffectiveInterval);
        service.Mode = MonitoringMode.Gaming;
        Assert.Equal(TimeSpan.FromSeconds(2), service.EffectiveInterval);
        service.Mode = MonitoringMode.Background;
        Assert.Equal(TimeSpan.FromSeconds(5), service.EffectiveInterval);
        service.Options = new MonitoringOptions { Interval = TimeSpan.FromMilliseconds(1) };
        Assert.True(service.Options.Interval >= TimeSpan.FromMilliseconds(500));
    }

    private static SystemMetrics At(DateTimeOffset start, int second, double cpu) => new() { CpuTotalPercent = cpu, Timestamp = start.AddSeconds(second) };

    [Fact]
    public void Alert_requires_sustained_violation_and_uses_hysteresis_and_cooldown()
    {
        var engine = new AlertEngine { Rules = new[] { new AlertRule { Id = "r", Name = "CPU", Metric = MetricId.CpuTotal, Threshold = 90, SustainSeconds = 10, Hysteresis = 10, CooldownSeconds = 300 } } };
        var t0 = DateTimeOffset.UtcNow;

        Assert.Empty(engine.Evaluate(At(t0, 0, 95)));
        Assert.Empty(engine.Evaluate(At(t0, 5, 96)));                        // noch nicht lange genug
        Assert.Single(engine.Evaluate(At(t0, 10, 97)));                      // gemeldet
        Assert.Empty(engine.Evaluate(At(t0, 15, 99)));                       // keine Wiederholung
        Assert.Empty(engine.Evaluate(At(t0, 20, 85)));                       // innerhalb der Hysterese → weiterhin aktiv
        var recovery = Assert.Single(engine.Evaluate(At(t0, 25, 70)));       // erst jetzt Entwarnung
        Assert.True(recovery.IsRecovery);

        Assert.Empty(engine.Evaluate(At(t0, 30, 99)));
        Assert.Empty(engine.Evaluate(At(t0, 45, 99)));                       // Abklingzeit: keine zweite Meldung
        Assert.Empty(engine.Evaluate(At(t0, 60, 50)));                       // und keine Entwarnung für eine nie gemeldete Warnung
        Assert.Empty(engine.Evaluate(At(t0, 400, 99)));
        Assert.Single(engine.Evaluate(At(t0, 411, 99)));                     // nach Ablauf der Abklingzeit wieder
    }

    [Fact]
    public void Short_spikes_and_missing_sensors_never_raise_alerts()
    {
        var engine = new AlertEngine { Rules = new[]
        {
            new AlertRule { Id = "cpu", Name = "CPU", Metric = MetricId.CpuTotal, Threshold = 90, SustainSeconds = 10 },
            new AlertRule { Id = "gpu", Name = "GPU", Metric = MetricId.GpuTemperature, Threshold = 80, SustainSeconds = 0 },
        } };
        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 20; i++)
            Assert.Empty(engine.Evaluate(At(t0, i * 3, i % 2 == 0 ? 99 : 20)));
    }
}

public class AdvisorTests
{
    internal static SystemSnapshot Snapshot(Func<SystemInfo, SystemInfo>? info = null, Func<SystemSnapshot, SystemSnapshot>? change = null)
    {
        var baseInfo = new SystemInfo
        {
            OsBuild = 22631, CpuName = "Test-CPU", MemoryTotalBytes = 16L << 30,
            Gpus = new[] { new GpuInfo { Name = "Dedizierte GPU", Vendor = "NVIDIA", IsIntegrated = false } },
            Volumes = new[] { new VolumeInfo { Letter = "C:", TotalBytes = 500L << 30, FreeBytes = 200L << 30, MediaType = DriveMediaType.Nvme, IsSystem = true } },
        };
        baseInfo = info?.Invoke(baseInfo) ?? baseInfo;
        var snapshot = new SystemSnapshot
        {
            Info = baseInfo,
            Context = baseInfo.ToContext(false, false, false),
            PowerPlans = new[]
            {
                new PowerPlanInfo(WellKnownPowerPlans.Balanced, "Ausbalanciert", true),
                new PowerPlanInfo(WellKnownPowerPlans.HighPerformance, "Höchstleistung", false),
                new PowerPlanInfo(WellKnownPowerPlans.PowerSaver, "Energiesparmodus", false),
            },
        };
        return change?.Invoke(snapshot) ?? snapshot;
    }

    private readonly RuleBasedAdvisor _advisor = new();

    [Fact]
    public void Healthy_system_produces_no_warnings()
        => Assert.DoesNotContain(_advisor.Analyze(Snapshot()), r => r.Severity >= RecommendationSeverity.Warning);

    [Fact]
    public void Low_system_storage_is_flagged_with_measured_evidence()
    {
        var snapshot = Snapshot(i => i with { Volumes = new[] { new VolumeInfo { Letter = "C:", TotalBytes = 250L << 30, FreeBytes = 4L << 30, IsSystem = true, MediaType = DriveMediaType.Ssd } } });
        var recommendation = Assert.Single(_advisor.Analyze(snapshot), r => r.Id == "storage-low-c");
        Assert.Equal(RecommendationSeverity.Critical, recommendation.Severity);
        Assert.NotEmpty(recommendation.Evidence);
        Assert.Empty(recommendation.Actions); // Bereinigung ist nicht umkehrbar → nie als automatischer Vorschlag
    }

    [Fact]
    public void Memory_pressure_explains_cache_and_never_offers_a_ram_cleaner()
    {
        var snapshot = Snapshot(change: s => s with { Memory = new MetricSummary(91, 97, 60), Commit = new MetricSummary(70, 80, 60) });
        var recommendation = Assert.Single(_advisor.Analyze(snapshot), r => r.Id == "memory-pressure");
        Assert.Contains("Standby", recommendation.Explanation);
        Assert.Empty(recommendation.Actions);
    }

    [Fact]
    public void Too_few_samples_do_not_trigger_load_based_rules()
    {
        var snapshot = Snapshot(change: s => s with { Memory = new MetricSummary(99, 99, 2), Cpu = new MetricSummary(99, 99, 2) });
        Assert.DoesNotContain(_advisor.Analyze(snapshot), r => r.Id is "memory-pressure" or "cpu-saturated");
    }

    [Fact]
    public void Power_saver_on_ac_proposes_balanced_plan_but_not_when_user_wants_quiet()
    {
        SystemSnapshot OnPowerSaver(SystemSnapshot s) => s with { PowerPlans = s.PowerPlans.Select(p => p with { IsActive = p.Id == WellKnownPowerPlans.PowerSaver }).ToList() };

        var recommendation = Assert.Single(_advisor.Analyze(Snapshot(change: OnPowerSaver)), r => r.Id == "power-saver-on-ac");
        Assert.Equal(WellKnownPowerPlans.Balanced.ToString(), Assert.Single(recommendation.Actions).Parameters.GetString("plan"));

        Assert.DoesNotContain(_advisor.Analyze(Snapshot(change: OnPowerSaver), OptimizationGoal.LowNoise), r => r.Id == "power-saver-on-ac");
    }

    [Fact]
    public void Thermal_limit_warns_and_discourages_performance_plan()
    {
        var snapshot = Snapshot(change: s => s with
        {
            PerformanceLimit = new MetricSummary(80, 100, 60),
            PowerPlans = s.PowerPlans.Select(p => p with { IsActive = p.Id == WellKnownPowerPlans.HighPerformance }).ToList(),
        });
        var recommendations = _advisor.Analyze(snapshot);
        Assert.Contains(recommendations, r => r.Id == "thermal-limit");
        Assert.Contains(recommendations, r => r.Id == "performance-plan-unsuitable");
    }

    [Fact]
    public void Declined_actions_are_not_proposed_again()
    {
        const string gameMode = "reg.gaming.enable-game-mode";
        var states = new Dictionary<string, bool?> { [gameMode] = false };
        Assert.Contains(_advisor.Analyze(Snapshot(change: s => s with { ActionStates = states })), r => r.Id == "tweak-game-mode");

        var declined = Snapshot(change: s => s with { ActionStates = states, DeclinedActions = new HashSet<string> { gameMode } });
        Assert.DoesNotContain(_advisor.Analyze(declined), r => r.Id == "tweak-game-mode");
    }

    [Fact]
    public void Advisor_identifies_itself_as_rule_based()
        => Assert.Contains("regelbasiert", RuleBasedAdvisor.EngineDescription);

    [Fact]
    public void Gaming_mode_skips_performance_plan_on_battery_or_when_throttling()
    {
        var onBattery = Snapshot(change: s => s with { Context = s.Context with { OnBatteryPower = true } });
        var candidates = OptimizationModes.BuildCandidates(OptimizationMode.Gaming, onBattery, Array.Empty<Recommendation>(), out var notes);
        Assert.DoesNotContain(candidates, c => c.ActionId == PowerPlanActivateAction.ActionId);
        Assert.NotEmpty(notes);

        var throttling = Snapshot(change: s => s with { PerformanceLimit = new MetricSummary(70, 90, 30) });
        Assert.DoesNotContain(OptimizationModes.BuildCandidates(OptimizationMode.MaximumPerformance, throttling, Array.Empty<Recommendation>(), out _), c => c.ActionId == PowerPlanActivateAction.ActionId);

        Assert.Contains(OptimizationModes.BuildCandidates(OptimizationMode.Gaming, Snapshot(), Array.Empty<Recommendation>(), out _), c => c.ActionId == PowerPlanActivateAction.ActionId);
    }

    [Fact]
    public void Safe_mode_contains_only_very_low_risk_non_experimental_tweaks()
    {
        var candidates = OptimizationModes.BuildCandidates(OptimizationMode.Safe, Snapshot(), Array.Empty<Recommendation>(), out _);
        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            var tweak = RegistryTweakCatalog.Find(candidate.ActionId)!;
            Assert.Equal(RiskLevel.VeryLow, tweak.Risk);
            Assert.False(tweak.Experimental);
            Assert.Equal(RegistryRoot.CurrentUser, tweak.Root);
        }
    }

    [Fact]
    public void No_mode_except_experimental_contains_experimental_tweaks()
    {
        foreach (var mode in Enum.GetValues<OptimizationMode>().Where(m => m != OptimizationMode.Experimental))
        {
            var candidates = OptimizationModes.BuildCandidates(mode, Snapshot(), Array.Empty<Recommendation>(), out _);
            Assert.DoesNotContain(candidates, c => RegistryTweakCatalog.Find(c.ActionId)?.Experimental == true);
        }
    }
}

public class ProfileTests
{
    private static (ProfileManager Manager, EngineHarness Harness, InMemoryRegistryStore Registry, FakePowerPolicy Power, InMemoryProfileStore Store) Create()
    {
        var registry = new InMemoryRegistryStore();
        var power = new FakePowerPolicy();
        var actions = RegistryTweakCatalog.All.Select(t => (IOptimizationAction)new RegistryTweakAction(t, registry)).ToList();
        actions.Add(new PowerPlanActivateAction(power));
        actions.Add(new PowerSettingAction(power));
        var harness = new EngineHarness(actions.ToArray());
        var store = new InMemoryProfileStore();
        return (new ProfileManager(store, harness.Catalog, harness.Planner, harness.Executor, harness.Rollback, power), harness, registry, power, store);
    }

    private static readonly RiskPolicy Policy = new() { MaxInteractiveRisk = RiskLevel.Low };

    [Fact]
    public void Built_in_profiles_cover_the_required_set_and_reference_only_known_actions()
    {
        var (_, harness, _, _, _) = Create();
        Assert.Equal(12, BuiltInProfiles.All.Count);
        foreach (var profile in BuiltInProfiles.All)
        {
            Assert.True(profile.IsBuiltIn);
            foreach (var item in profile.Items)
            {
                var action = harness.Catalog.Find(item.ActionId);
                Assert.True(action is not null, $"{profile.Name}: {item.ActionId}");
                Assert.True(action!.ValidateParameters(new ActionParameters(item.Parameters)).IsValid, $"{profile.Name}: {item.ActionId}");
            }
        }
    }

    [Fact]
    public async Task Activation_applies_changes_and_reset_restores_everything()
    {
        var (manager, harness, registry, power, _) = Create();
        var gaming = BuiltInProfiles.Find(BuiltInProfiles.Gaming)!;

        var result = await manager.ActivateAsync(gaming, Policy, harness.Context);

        Assert.True(result.Success, result.Message);
        Assert.Equal(WellKnownPowerPlans.HighPerformance, power.Active);
        Assert.Equal(gaming.Id, manager.ActiveProfile!.Id);
        Assert.True(registry.WriteCount > 0);

        var outcomes = await manager.ResetActiveAsync();

        Assert.All(outcomes, o => Assert.True(o.Success, o.Message));
        Assert.Equal(WellKnownPowerPlans.Balanced, power.Active);
        Assert.Null(manager.ActiveProfile);
        var recording = RegistryTweakCatalog.Find("gaming.disable-background-recording")!;
        Assert.Null(registry.ReadValue(recording.Root, recording.View, recording.KeyPath, recording.ValueName));
    }

    [Fact]
    public async Task Switching_profiles_first_reverts_the_previous_profile()
    {
        var (manager, harness, _, power, _) = Create();
        await manager.ActivateAsync(BuiltInProfiles.Find(BuiltInProfiles.Gaming)!, Policy, harness.Context);
        var result = await manager.ActivateAsync(BuiltInProfiles.Find(BuiltInProfiles.Standard)!, Policy, harness.Context);

        Assert.True(result.Success);
        Assert.Equal(WellKnownPowerPlans.Balanced, power.Active);
        Assert.Equal(BuiltInProfiles.Standard, manager.ActiveProfile!.Id);
    }

    [Fact]
    public async Task Profiles_cannot_bypass_risk_permissions_or_run_experimental_tweaks()
    {
        var (manager, harness, registry, _, _) = Create();
        var experimental = BuiltInProfiles.Find(BuiltInProfiles.Experimental)!;
        var unlocked = new RiskPolicy { MaxInteractiveRisk = RiskLevel.Critical, ExperimentalUnlocked = true };

        var result = await manager.ActivateAsync(experimental, unlocked, harness.Context);

        Assert.Equal(0, registry.WriteCount);
        Assert.All(result.Plan!.Rejected, r => Assert.Equal(RejectionKind.NotPermitted, r.Kind));
    }

    [Fact]
    public void Missing_power_plan_falls_back_with_a_note()
    {
        var (manager, _, _, power, _) = Create();
        var plan = manager.ResolvePowerPlan("ultimate", out var note);
        Assert.Equal(WellKnownPowerPlans.HighPerformance, plan!.Id);
        Assert.NotNull(note);

        power.Plans.Remove(WellKnownPowerPlans.HighPerformance);
        Assert.Null(manager.ResolvePowerPlan("high-performance", out note));
        Assert.NotNull(note);
    }

    [Fact]
    public void Export_import_roundtrip_yields_a_new_custom_profile()
    {
        var (manager, harness, _, _, _) = Create();
        var json = ProfileSerializer.Export(BuiltInProfiles.Find(BuiltInProfiles.Quiet)!);

        var result = ProfileSerializer.Import(json, harness.Catalog);

        Assert.True(result.Success, string.Join(" ", result.Errors));
        Assert.False(result.Profile!.IsBuiltIn);
        Assert.NotEqual(BuiltInProfiles.Quiet, result.Profile.Id);
        Assert.Equal("99", result.Profile.Items.Single().Parameters["ac"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein json")]
    [InlineData("{\"SchemaVersion\":99,\"Name\":\"X\",\"Items\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Name\":\"\",\"Items\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Name\":\"X\",\"Items\":[{\"ActionId\":\"shell.execute\",\"Parameters\":{\"cmd\":\"calc.exe\"}}]}")]
    [InlineData("{\"SchemaVersion\":1,\"Name\":\"X\",\"Items\":[{\"ActionId\":\"power.setting.set\",\"Parameters\":{\"setting\":\"processor-max\",\"ac\":\"5\"}}]}")]
    [InlineData("{\"SchemaVersion\":1,\"Name\":\"X\",\"PowerPlan\":\"turbo\",\"Items\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Name\":\"X\",\"Items\":[],\"Unbekannt\":true}")]
    [InlineData("{\"SchemaVersion\":1,\"Name\":\"X\",\"Items\":[],\"AssignedApplications\":[\"..\\\\..\\\\boese.exe\"]}")]
    public void Malicious_or_malformed_profile_imports_are_rejected(string json)
    {
        var (_, harness, _, _, _) = Create();
        var result = ProfileSerializer.Import(json, harness.Catalog);
        Assert.False(result.Success);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void Oversized_import_is_rejected_before_parsing()
    {
        var (_, harness, _, _, _) = Create();
        Assert.False(ProfileSerializer.Import(new string(' ', ProfileSerializer.MaxImportBytes + 1) + "{}", harness.Catalog).Success);
    }

    [Fact]
    public void Clone_rename_compare_and_built_in_protection()
    {
        var (manager, _, _, _, _) = Create();
        var gaming = BuiltInProfiles.Find(BuiltInProfiles.Gaming)!;
        var clone = manager.Clone(gaming);
        Assert.False(clone.IsBuiltIn);
        Assert.Equal("Gaming (Kopie)", clone.Name);

        manager.Update(clone with { Name = "Mein Profil", PowerPlan = "balanced" });
        Assert.Contains(manager.GetAll(), p => p.Name == "Mein Profil");
        Assert.Throws<InvalidOperationException>(() => manager.Update(gaming with { Name = "Geändert" }));
        Assert.Throws<InvalidOperationException>(() => manager.Delete(gaming));

        var differences = manager.Compare(gaming, manager.Find(clone.Id)!);
        Assert.Contains(differences, d => d.Title == "Energieplan");
        Assert.NotEmpty(manager.Compare(gaming, BuiltInProfiles.Find(BuiltInProfiles.Quiet)!));
    }
}

public class AutomationTests
{
    private static AutomationRule Rule(string name, TriggerType trigger = TriggerType.GameStarted, int priority = 50, string plan = "high", int cooldown = 60, params AutomationCondition[] conditions) => new()
    {
        Id = name, Name = name, Priority = priority, CooldownSeconds = cooldown, Trigger = new AutomationTrigger { Type = trigger }, Conditions = conditions.ToList(),
        Actions = new List<AutomationAction> { new() { Type = RuleActionType.ApplyAction, ActionId = PowerPlanActivateAction.ActionId, Parameters = new() { ["plan"] = plan } } },
    };

    private static (AutomationEngine Engine, InMemoryAutomationStore Store, FakeAutomationExecutor Executor, FakeAutomationContext Context, ManualTimeProvider Time) Create(params AutomationRule[] rules)
    {
        var store = new InMemoryAutomationStore();
        foreach (var rule in rules) store.SaveRule(rule);
        var executor = new FakeAutomationExecutor();
        var context = new FakeAutomationContext();
        var time = new ManualTimeProvider();
        return (new AutomationEngine(store, executor, context, time), store, executor, context, time);
    }

    [Fact]
    public async Task Higher_priority_rule_wins_when_two_rules_target_the_same_setting()
    {
        var (engine, _, executor, _, time) = Create(Rule("niedrig", priority: 10, plan: "saver"), Rule("hoch", priority: 90, plan: "high"));

        var records = await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now));

        Assert.Equal("hoch", Assert.Single(executor.Executed).Rule);
        Assert.Contains(records, r => r.RuleName == "niedrig" && r.Skipped);
    }

    [Fact]
    public async Task Cooldown_is_enforced_with_a_minimum_of_sixty_seconds()
    {
        var (engine, _, executor, _, time) = Create(Rule("r", cooldown: 0));
        await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now));
        await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now.AddSeconds(30)));
        Assert.Single(executor.Executed);
        await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now.AddSeconds(61)));
        Assert.Equal(2, executor.Executed.Count);
    }

    [Fact]
    public async Task Rapid_oscillation_between_rules_is_detected_and_locked()
    {
        var (engine, _, executor, _, time) = Create(Rule("an", TriggerType.AcConnected, plan: "high"), Rule("aus", TriggerType.BatteryMode, plan: "saver"));
        var records = new List<AutomationRunRecord>();
        for (var i = 0; i < 8; i++)
        {
            var type = i % 2 == 0 ? TriggerType.AcConnected : TriggerType.BatteryMode;
            records.AddRange(await engine.HandleEventAsync(new AutomationEvent(type, time.Now.AddSeconds(i * 61))));
        }

        Assert.Equal(AutomationEngine.OscillationMaxChanges, executor.Executed.Count);
        Assert.Contains(records, r => r.Skipped && r.Message.Contains("Pendeln"));
        Assert.Contains(records, r => r.Skipped && r.Message.Contains("Pendelsperre"));
    }

    [Fact]
    public async Task Conditions_are_evaluated_against_the_live_context()
    {
        var (engine, _, executor, context, time) = Create(Rule("r", conditions: new AutomationCondition { Type = ConditionType.OnAcPower }));
        context.OnAcPower = false;
        await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now));
        Assert.Empty(executor.Executed);

        context.OnAcPower = true;
        await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now));
        Assert.Single(executor.Executed);
    }

    [Fact]
    public async Task Exit_rule_restores_what_the_start_rule_changed()
    {
        var restore = new AutomationRule
        {
            Id = "ende", Name = "ende", Trigger = new AutomationTrigger { Type = TriggerType.GameExited },
            Actions = new List<AutomationAction> { new() { Type = RuleActionType.RestoreRuleChanges, RuleId = "start" } },
        };
        var (engine, _, executor, _, time) = Create(Rule("start"), restore);

        await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now));
        await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameExited, time.Now.AddMinutes(30)));

        Assert.Equal(new[] { "batch1" }, Assert.Single(executor.Restored));
    }

    [Fact]
    public async Task Failed_action_is_recorded_and_disabled_engine_does_nothing()
    {
        var (engine, store, executor, _, time) = Create(Rule("r"));
        executor.Fail = true;
        var record = Assert.Single(await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now)));
        Assert.False(record.Success);
        Assert.Single(store.GetHistory());

        engine.Enabled = false;
        Assert.Empty(await engine.HandleEventAsync(new AutomationEvent(TriggerType.GameStarted, time.Now.AddHours(1))));
    }

    [Fact]
    public void Static_analysis_finds_conflicting_rules_but_not_mutually_exclusive_ones()
    {
        var conflicts = AutomationAnalysis.DetectConflicts(new[] { Rule("a", plan: "high"), Rule("b", plan: "saver") });
        Assert.Contains(conflicts, c => c.Resource == "power.plan");

        var exclusive = AutomationAnalysis.DetectConflicts(new[]
        {
            Rule("a", plan: "high", conditions: new AutomationCondition { Type = ConditionType.OnAcPower }),
            Rule("b", plan: "saver", conditions: new AutomationCondition { Type = ConditionType.OnBattery }),
        });
        Assert.DoesNotContain(exclusive, c => c.Resource == "power.plan");

        Assert.DoesNotContain(AutomationAnalysis.DetectConflicts(new[] { Rule("a", plan: "high"), Rule("b", plan: "high") }), c => c.Resource == "power.plan");
    }

    [Fact]
    public void Rule_validation_rejects_risky_or_incomplete_rules()
    {
        Assert.Empty(AutomationAnalysis.Validate(Rule("gültig")));
        Assert.NotEmpty(AutomationAnalysis.Validate(Rule("riskant") with { MaxRisk = RiskLevel.Moderate }));
        Assert.NotEmpty(AutomationAnalysis.Validate(Rule("leer") with { Actions = new() }));
        Assert.NotEmpty(AutomationAnalysis.Validate(Rule("zeit", TriggerType.Schedule)));
        Assert.NotEmpty(AutomationAnalysis.Validate(Rule("cpu", TriggerType.CpuHigh)));
    }

    [Fact]
    public void Threshold_events_need_duration_and_rearm_only_after_real_recovery()
    {
        var source = new ThresholdEventSource();
        var rules = new[] { Rule("cpu", TriggerType.CpuHigh) with { Trigger = new AutomationTrigger { Type = TriggerType.CpuHigh, Threshold = 90, DurationSeconds = 10 } } };
        var t0 = DateTimeOffset.UtcNow;
        SystemMetrics M(int s, double cpu) => new() { CpuTotalPercent = cpu, Timestamp = t0.AddSeconds(s) };

        Assert.Empty(source.Evaluate(M(0, 95), rules));
        Assert.Single(source.Evaluate(M(10, 95), rules));
        Assert.Empty(source.Evaluate(M(20, 95), rules));   // kein Dauerfeuer
        Assert.Empty(source.Evaluate(M(30, 88), rules));   // knapp darunter: noch nicht scharf
        Assert.Empty(source.Evaluate(M(40, 95), rules));
        Assert.Empty(source.Evaluate(M(50, 95), rules));
        Assert.Empty(source.Evaluate(M(60, 50), rules));   // echte Erholung
        Assert.Empty(source.Evaluate(M(70, 95), rules));
        Assert.Single(source.Evaluate(M(80, 95), rules));
    }

    [Theory]
    [InlineData("22:00", "06:00", "23:30", true)]
    [InlineData("22:00", "06:00", "05:59", true)]
    [InlineData("22:00", "06:00", "12:00", false)]
    [InlineData("08:00", "17:00", "12:00", true)]
    [InlineData("08:00", "17:00", "17:00", false)]
    public void Time_window_handles_ranges_across_midnight(string from, string to, string now, bool expected)
        => Assert.Equal(expected, AutomationEngine.IsBetween(TimeOnly.Parse(now), from, to));
}

public class GamingTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
            // Kommentar
            "0"
            {
                "path"		"C:\\Program Files (x86)\\Steam"
                "apps" { "730" "123456" }
            }
            "1"
            {
                "path"		"D:\\SteamLibrary"
            }
            "contentstatsid" "42"
        }
        """;

    [Fact]
    public void Steam_library_folders_are_parsed_in_new_and_old_format()
    {
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, GameManifestParsers.ParseSteamLibraryFolders(LibraryFolders));
        Assert.Equal(new[] { @"E:\Spiele" }, GameManifestParsers.ParseSteamLibraryFolders("\"LibraryFolders\" { \"1\" \"E:\\\\Spiele\" }"));
    }

    [Fact]
    public void Steam_manifest_is_parsed_and_tools_or_path_tricks_are_ignored()
    {
        var game = GameManifestParsers.ParseSteamAppManifest("\"AppState\" { \"appid\" \"730\" \"name\" \"Counter-Strike 2\" \"installdir\" \"Counter-Strike Global Offensive\" }", @"D:\SteamLibrary\steamapps");
        Assert.Equal("steam:730", game!.Id);
        Assert.Equal("Counter-Strike 2", game.Name);
        Assert.EndsWith("Counter-Strike Global Offensive", game.InstallDirectory);

        Assert.Null(GameManifestParsers.ParseSteamAppManifest("\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"Steamworks Shared\" }", "x"));
        Assert.Null(GameManifestParsers.ParseSteamAppManifest("\"AppState\" { \"appid\" \"1\" \"name\" \"Böse\" \"installdir\" \"..\\\\..\\\\Windows\" }", "x"));
        Assert.Null(GameManifestParsers.ParseSteamAppManifest("\"AppState\" { \"appid\" \"1\" }", "x"));
    }

    [Theory]
    [InlineData("\"a\" { \"b\" ")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("}")]
    public void Malformed_vdf_throws_format_exception_instead_of_hanging(string text)
        => Assert.Throws<FormatException>(() => VdfNode.Parse(text));

    [Fact]
    public void Epic_manifest_is_parsed_and_non_games_are_skipped()
    {
        var game = GameManifestParsers.ParseEpicManifest("{\"DisplayName\":\"Fortnite\",\"InstallLocation\":\"C:\\\\Epic\\\\Fortnite\",\"AppName\":\"Fortnite\",\"LaunchExecutable\":\"FortniteGame/Binaries/Win64/FortniteClient.exe\",\"bIsApplication\":true}");
        Assert.Equal("Fortnite", game!.Name);
        Assert.EndsWith("FortniteClient.exe", Assert.Single(game.Executables));

        Assert.Null(GameManifestParsers.ParseEpicManifest("{\"DisplayName\":\"DLC\",\"InstallLocation\":\"C:\\\\x\",\"AppName\":\"dlc\",\"bIsApplication\":false}"));
        Assert.Null(GameManifestParsers.ParseEpicManifest("{\"DisplayName\":\"Unreal\",\"InstallLocation\":\"C:\\\\x\",\"AppName\":\"UE_5.4\"}"));
        Assert.Null(GameManifestParsers.ParseEpicManifest("kaputt"));
        Assert.Empty(GameManifestParsers.ParseEpicManifest("{\"DisplayName\":\"X\",\"InstallLocation\":\"C:\\\\x\",\"AppName\":\"x\",\"LaunchExecutable\":\"..\\\\..\\\\evil.exe\"}")!.Executables);
    }

    [Fact]
    public void Frame_statistics_compute_average_and_lows_and_need_enough_frames()
    {
        Assert.Null(FrameStatistics.FromFrameTimes(Enumerable.Repeat(16.0, 50).ToList()));

        // 990 Frames à 10 ms, 10 Frames à 50 ms → Mittel 10,4 ms; 1 % Low = 1000/50 = 20 FPS.
        var frames = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToList();
        var stats = FrameStatistics.FromFrameTimes(frames)!;
        Assert.Equal(1000, stats.FrameCount);
        Assert.Equal(1000.0 / 10.4, stats.AverageFps, 3);
        Assert.Equal(20.0, stats.OnePercentLowFps, 3);
        Assert.Equal(10.0, stats.MedianFrameTimeMs, 3);
        Assert.True(stats.P99FrameTimeMs >= 10);
    }

    private sealed class Applier : IGameProfileApplier
    {
        public int Applied, Restored;
        public Task<GameProfileApplyResult> ApplyAsync(GameProfile profile, int processId, CancellationToken cancellationToken) { Applied++; return Task.FromResult(new GameProfileApplyResult(true, "batch", "Profil aktiv.")); }
        public Task<string> RestoreAsync(string batchId, CancellationToken cancellationToken) { Restored++; return Task.FromResult("Wiederhergestellt."); }
    }

    private sealed class Store : IGameStore
    {
        public List<GameProfile> Profiles { get; } = new();
        public List<GameSessionRecord> Sessions { get; } = new();
        public IReadOnlyList<GameInfo> GetGames() => Array.Empty<GameInfo>();
        public void SaveGames(IReadOnlyList<GameInfo> games) { }
        public IReadOnlyList<GameProfile> GetProfiles() => Profiles;
        public void SaveProfile(GameProfile profile) => Profiles.Add(profile);
        public void DeleteProfile(string gameId) => Profiles.RemoveAll(p => p.GameId == gameId);
        public void AppendSession(GameSessionRecord session) => Sessions.Add(session);
        public IReadOnlyList<GameSessionRecord> GetSessions(string? gameId = null, int limit = 100) => Sessions;
    }

    [Fact]
    public async Task Session_applies_profile_on_start_and_restores_on_exit_with_real_statistics()
    {
        var store = new Store();
        store.Profiles.Add(new GameProfile { GameId = "g", GameName = "Spiel", ProcessNames = { "spiel.exe" }, AutoActivate = true });
        var processes = new FakeProcessMonitor();
        var applier = new Applier();
        var collector = new FakeMetricsCollector { Default = new SystemMetrics { CpuTotalPercent = 60, GpuUsagePercent = 90, OnAcPower = true, MemoryTotalBytes = 100, MemoryAvailableBytes = 50 } };
        using var monitoring = new MonitoringService(collector);
        var manager = new GamingSessionManager(store, processes, applier, monitoring) { AutoModeEnabled = true };

        await manager.TickAsync();
        Assert.Null(manager.Current);

        processes.Processes.Add(new ProcessSnapshot { ProcessId = 4242, Name = "Spiel" });
        await manager.TickAsync();
        Assert.NotNull(manager.Current);
        Assert.Equal(1, applier.Applied);
        Assert.Equal(MonitoringMode.Gaming, monitoring.Mode);

        monitoring.SampleOnce();
        monitoring.SampleOnce();
        await manager.TickAsync(); // läuft noch
        Assert.Equal(0, applier.Restored);

        processes.Processes.Clear();
        await manager.TickAsync();

        Assert.Null(manager.Current);
        Assert.Equal(1, applier.Restored);
        var session = Assert.Single(store.Sessions);
        Assert.Equal(2, session.Samples);
        Assert.Equal(60, session.AverageCpuPercent);
        Assert.Null(session.Frames);            // ohne Frametime-Anbieter keine erfundenen FPS
        Assert.NotNull(session.FrameNote);
        Assert.Null(session.PeakGpuTemperatureCelsius);
    }

    [Fact]
    public async Task Profile_is_not_applied_on_battery_or_without_explicit_auto_approval()
    {
        var store = new Store();
        store.Profiles.Add(new GameProfile { GameId = "g", GameName = "Spiel", ProcessNames = { "spiel.exe" }, AutoActivate = true, OnlyOnAcPower = true });
        store.Profiles.Add(new GameProfile { GameId = "h", GameName = "Anderes", ProcessNames = { "anderes.exe" }, AutoActivate = false });
        var processes = new FakeProcessMonitor();
        var applier = new Applier();
        using var monitoring = new MonitoringService(new FakeMetricsCollector { Default = new SystemMetrics { OnAcPower = false } });
        monitoring.SampleOnce();
        var manager = new GamingSessionManager(store, processes, applier, monitoring) { AutoModeEnabled = true };

        processes.Processes.Add(new ProcessSnapshot { ProcessId = 1, Name = "anderes" });
        await manager.TickAsync();
        Assert.Null(manager.Current);

        processes.Processes.Add(new ProcessSnapshot { ProcessId = 2, Name = "spiel" });
        await manager.TickAsync();
        Assert.NotNull(manager.Current);
        Assert.Equal(0, applier.Applied);
        Assert.Contains("Akkubetrieb", manager.Current!.Note);
    }
}

public class BenchmarkAndTuningTests
{
    [Fact]
    public void Statistics_basics()
    {
        var values = new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 };
        Assert.Equal(5, Statistics.Mean(values));
        Assert.Equal(4.5, Statistics.Median(values));
        Assert.Equal(2.138, Statistics.StandardDeviation(values), 3);
        Assert.Equal(9, Statistics.Percentile(values, 100));
        Assert.Equal(2, Statistics.Percentile(values, 0));
        Assert.True(double.IsNaN(Statistics.Median(Array.Empty<double>())));
    }

    [Fact]
    public void Noise_is_not_reported_as_improvement()
    {
        var baseline = new[] { 100.0, 104, 97, 102, 99 };
        var candidate = new[] { 101.0, 98, 105, 100, 103 };
        Assert.Equal(ComparisonVerdict.NoSignificantChange, MeasurementComparer.Compare(baseline, candidate, higherIsBetter: true).Verdict);
    }

    [Fact]
    public void Clear_changes_are_classified_by_direction()
    {
        var baseline = new[] { 100.0, 101, 99, 100, 100 };
        var better = new[] { 120.0, 121, 119, 120, 122 };
        Assert.Equal(ComparisonVerdict.Improved, MeasurementComparer.Compare(baseline, better, higherIsBetter: true).Verdict);
        Assert.Equal(ComparisonVerdict.Regressed, MeasurementComparer.Compare(baseline, better, higherIsBetter: false).Verdict);
        Assert.Equal(ComparisonVerdict.Regressed, MeasurementComparer.Compare(better, baseline, higherIsBetter: true).Verdict);
    }

    [Fact]
    public void Tiny_but_consistent_differences_below_minimum_effect_are_not_a_result()
    {
        var baseline = new[] { 100.0, 100.01, 99.99, 100, 100.02 };
        var candidate = new[] { 100.5, 100.51, 100.49, 100.5, 100.52 };
        Assert.Equal(ComparisonVerdict.NoSignificantChange, MeasurementComparer.Compare(baseline, candidate, true).Verdict);
    }

    [Fact]
    public void Too_few_samples_are_inconclusive()
        => Assert.Equal(ComparisonVerdict.InsufficientData, MeasurementComparer.Compare(new[] { 1.0, 2 }, new[] { 5.0, 6 }, true).Verdict);

    [Fact]
    public async Task Cpu_and_latency_benchmarks_return_real_positive_measurements()
    {
        var runner = new BenchmarkRunner();
        var options = new BenchmarkOptions { Runs = 3, RunDuration = TimeSpan.FromMilliseconds(120), WarmUp = TimeSpan.FromMilliseconds(50) };

        var cpu = await runner.RunAsync(BenchmarkKind.CpuSingleThread, options);
        Assert.Equal(3, cpu.Samples.Count);
        Assert.All(cpu.Samples, s => Assert.True(s > 0));
        Assert.Equal("MB/s", cpu.Unit);

        var latency = await runner.RunAsync(BenchmarkKind.SchedulerLatency, options);
        Assert.False(latency.HigherIsBetter);
        Assert.All(latency.Samples, s => Assert.True(s >= 0));
    }

    [Fact]
    public async Task Disk_benchmark_measures_and_removes_its_temporary_file()
    {
        using var temp = new TempDirectory();
        var runner = new BenchmarkRunner();
        var options = new BenchmarkOptions { Runs = 2, RunDuration = TimeSpan.FromMilliseconds(100), DiskFileBytes = 16L * 1024 * 1024, DiskDirectory = temp.Path };

        foreach (var kind in new[] { BenchmarkKind.DiskSequentialWrite, BenchmarkKind.DiskSequentialRead, BenchmarkKind.DiskRandomRead })
        {
            var result = await runner.RunAsync(kind, options);
            Assert.Equal(2, result.Samples.Count);
            Assert.All(result.Samples, s => Assert.True(s > 0));
        }
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void Every_benchmark_documents_methodology_and_limitations()
    {
        foreach (var kind in Enum.GetValues<BenchmarkKind>())
        {
            var description = BenchmarkRunner.Describe(kind);
            Assert.False(string.IsNullOrWhiteSpace(description.Methodology));
            Assert.False(string.IsNullOrWhiteSpace(description.Limitations));
        }
    }

    [Fact]
    public void Same_measurement_is_scored_differently_depending_on_the_goal()
    {
        var changes = new[]
        {
            new DimensionChange(TuningDimension.Throughput, +8, HigherIsBetter: true, Significant: true),
            new DimensionChange(TuningDimension.Temperature, +12, HigherIsBetter: false, Significant: true),
        };
        Assert.True(MultiObjectiveScorer.Score(changes, new[] { OptimizationGoal.MaximumFps }).Score > 0);
        Assert.True(MultiObjectiveScorer.Score(changes, new[] { OptimizationGoal.LowNoise }).Score < 0);

        var noise = new[] { new DimensionChange(TuningDimension.Throughput, +30, true, Significant: false) };
        var score = MultiObjectiveScorer.Score(noise, new[] { OptimizationGoal.MaximumFps });
        Assert.Equal(0, score.Score);
        Assert.False(score.HasEvidence);
    }

    private static async Task<(TuningExperimentResult Result, FakeAction Action, InMemoryFeedbackStore Feedback)> RunExperiment(Func<FakeAction, double> measure)
    {
        var action = new FakeAction("a.tune");
        var harness = new EngineHarness(action);
        var feedback = new InMemoryFeedbackStore();
        var runner = new TuningExperimentRunner(harness.Planner, harness.Executor, harness.Rollback, feedback);
        var counter = 0;
        var workload = new TuningWorkload("Test", "Punkte", true, TuningDimension.Throughput, _ => Task.FromResult(measure(action) + (counter++ % 3) * 0.1));
        var result = await runner.RunAsync(new PlanRequest("a.tune"), workload, EngineHarness.Permissive, harness.Context, OptimizationGoal.BalancedPerformance, runs: 5);
        return (result, action, feedback);
    }

    [Fact]
    public async Task Experiment_keeps_a_measurably_beneficial_change()
    {
        var (result, action, feedback) = await RunExperiment(a => a.State == "neu" ? 130 : 100);
        Assert.Equal(TuningRecommendation.Keep, result.Recommendation);
        Assert.False(result.Reverted);
        Assert.Equal("neu", action.State);
        Assert.Empty(feedback.GetDeclined());
    }

    [Fact]
    public async Task Experiment_reverts_a_change_without_measurable_effect()
    {
        var (result, action, _) = await RunExperiment(_ => 100);
        Assert.Equal(TuningRecommendation.NoMeasurableEffect, result.Recommendation);
        Assert.True(result.Reverted);
        Assert.Equal("alt", action.State);
    }

    [Fact]
    public async Task Experiment_reverts_a_regression_and_remembers_it()
    {
        var (result, action, feedback) = await RunExperiment(a => a.State == "neu" ? 70 : 100);
        Assert.Equal(TuningRecommendation.Revert, result.Recommendation);
        Assert.True(result.Reverted);
        Assert.Equal("alt", action.State);
        Assert.Contains("a.tune", feedback.GetDeclined());
    }

    [Fact]
    public async Task Experiment_refuses_irreversible_changes()
    {
        var harness = new EngineHarness(new FakeAction("a.irreversible", reversible: false));
        var runner = new TuningExperimentRunner(harness.Planner, harness.Executor, harness.Rollback, new InMemoryFeedbackStore());
        var workload = new TuningWorkload("Test", "x", true, TuningDimension.Throughput, _ => Task.FromResult(1.0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new PlanRequest("a.irreversible"), workload, EngineHarness.Permissive, harness.Context, OptimizationGoal.BalancedPerformance));
    }
}

public class SettingsTests
{
    [Fact]
    public void Unattended_risk_can_never_exceed_low_regardless_of_stored_settings()
    {
        var policy = (new AppSettings { MaxUnattendedRisk = RiskLevel.Critical, MaxInteractiveRisk = RiskLevel.Critical }).ToRiskPolicy();
        Assert.Equal(RiskLevel.Low, policy.MaxUnattendedRisk);
        Assert.Equal(RiskLevel.High, policy.MaxInteractiveRisk); // ohne freigeschaltetes Expertenlabor höchstens „Hoch“
        Assert.False(policy.ExperimentalUnlocked);
    }

    [Fact]
    public void Out_of_range_values_are_sanitized()
    {
        var settings = (new AppSettings
        {
            Theme = "Neon", AccentColor = "rot", FontScale = 9, MonitoringIntervalSeconds = 0.0001, HistoryRetentionDays = 9999, LogLevel = "Alles",
            LatencyEndpoints = new List<string> { "1.1.1.1", "bad host name!", "example.com", "1.1.1.1" },
        }).Sanitize();

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("#7C5CFF", settings.AccentColor);
        Assert.Equal(1.5, settings.FontScale);
        Assert.Equal(0.5, settings.MonitoringIntervalSeconds);
        Assert.Equal(90, settings.HistoryRetentionDays);
        Assert.Equal("Information", settings.LogLevel);
        Assert.Equal(new[] { "1.1.1.1", "example.com" }, settings.LatencyEndpoints);
    }

    [Fact]
    public void Defaults_are_privacy_preserving_and_conservative()
    {
        var settings = new AppSettings();
        Assert.Equal(AiProviderKind.RuleBased, settings.AiProvider);
        Assert.False(settings.CloudAiConsent);
        Assert.False(settings.AutomationEnabled);
        Assert.False(settings.AiAutoEnabled);
        Assert.False(settings.ExperimentalUnlocked);
        Assert.False(settings.LatencyProbeEnabled);
        Assert.Null(settings.UpdateManifestUrl);
        Assert.Equal(RiskLevel.Low, settings.MaxInteractiveRisk);
    }

    [Fact]
    public void Settings_service_persists_and_notifies()
    {
        var store = new InMemorySettingsStore();
        var service = new SettingsService(store);
        AppSettings? notified = null;
        service.Changed += (_, s) => notified = s;

        service.Update(s => s with { Theme = "Light", FontScale = 99 });

        Assert.Equal("Light", store.Load().Theme);
        Assert.Equal(1.5, notified!.FontScale);
    }
}
