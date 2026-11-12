using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Tools;

namespace NexusOptimizer.Core.Tests;

public class PowerActionTests
{
    private static readonly SystemContext Context = new() { WindowsBuild = 22631 };

    [Fact]
    public async Task Plan_activation_can_be_verified_and_rolled_back()
    {
        var power = new FakePowerPolicy();
        var action = new PowerPlanActivateAction(power);
        var parameters = ActionParameters.Of(("plan", WellKnownPowerPlans.HighPerformance.ToString()));

        var backup = await action.CreateBackupAsync(parameters);
        Assert.True((await action.ApplyAsync(parameters)).Success);
        Assert.True((await action.VerifyAsync(parameters)).Verified);
        Assert.Equal(WellKnownPowerPlans.HighPerformance, power.Active);

        Assert.True((await action.RollbackAsync(parameters, backup, force: false)).Success);
        Assert.Equal(WellKnownPowerPlans.Balanced, power.Active);
    }

    [Fact]
    public async Task Plan_that_does_not_exist_on_this_system_is_unsupported()
    {
        var action = new PowerPlanActivateAction(new FakePowerPolicy());
        var support = await action.IsSupportedAsync(Context, ActionParameters.Of(("plan", WellKnownPowerPlans.UltimatePerformance.ToString())));
        Assert.False(support.IsSupported);
    }

    [Fact]
    public async Task Plan_rollback_detects_manual_change_in_between()
    {
        var power = new FakePowerPolicy();
        var action = new PowerPlanActivateAction(power);
        var parameters = ActionParameters.Of(("plan", WellKnownPowerPlans.HighPerformance.ToString()));
        var backup = await action.CreateBackupAsync(parameters);
        await action.ApplyAsync(parameters);
        power.Active = WellKnownPowerPlans.PowerSaver; // Benutzer stellt manuell um

        var result = await action.RollbackAsync(parameters, backup, force: false);

        Assert.True(result.ConflictDetected);
        Assert.Equal(WellKnownPowerPlans.PowerSaver, power.Active);
    }

    [Fact]
    public async Task Access_denied_on_plan_switch_is_a_failure()
    {
        var action = new PowerPlanActivateAction(new FakePowerPolicy { FailWrites = true });
        var result = await action.ApplyAsync(ActionParameters.Of(("plan", WellKnownPowerPlans.HighPerformance.ToString())));
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("processor-max", "49", false)]   // unter dem zulässigen Minimum von 50
    [InlineData("processor-max", "101", false)]
    [InlineData("processor-max", "99", true)]
    [InlineData("boost-mode", "5", false)]
    [InlineData("boost-mode", "2", true)]
    [InlineData("unbekannt", "1", false)]
    public void Power_setting_values_are_limited_to_the_allowlisted_range(string setting, string value, bool valid)
    {
        var action = new PowerSettingAction(new FakePowerPolicy());
        Assert.Equal(valid, action.ValidateParameters(ActionParameters.Of(("setting", setting), ("ac", value))).IsValid);
    }

    [Fact]
    public void Power_setting_requires_at_least_one_value()
        => Assert.False(new PowerSettingAction(new FakePowerPolicy()).ValidateParameters(ActionParameters.Of(("setting", "processor-max"))).IsValid);

    [Fact]
    public async Task Power_setting_roundtrip_restores_only_changed_values()
    {
        var power = new FakePowerPolicy();
        var definition = PowerSettingCatalog.Find(PowerSettingCatalog.ProcessorMax)!;
        power.Values[(power.Active, definition.Setting, true)] = 100;
        power.Values[(power.Active, definition.Setting, false)] = 80;
        var action = new PowerSettingAction(power);
        var parameters = ActionParameters.Of(("setting", PowerSettingCatalog.ProcessorMax), ("ac", "99"));

        var backup = await action.CreateBackupAsync(parameters);
        Assert.True((await action.ApplyAsync(parameters)).Success);
        Assert.True((await action.VerifyAsync(parameters)).Verified);
        Assert.Equal(99u, power.Values[(power.Active, definition.Setting, true)]);
        Assert.Equal(80u, power.Values[(power.Active, definition.Setting, false)]);

        Assert.True((await action.RollbackAsync(parameters, backup, force: false)).Success);
        Assert.Equal(100u, power.Values[(power.Active, definition.Setting, true)]);
        Assert.Equal(80u, power.Values[(power.Active, definition.Setting, false)]);
    }

    [Fact]
    public async Task Power_setting_missing_on_this_system_is_unsupported()
    {
        var power = new FakePowerPolicy();
        power.MissingSettings.Add(PowerSettingCatalog.Find(PowerSettingCatalog.CoreParkingMin)!.Setting);
        var support = await new PowerSettingAction(power).IsSupportedAsync(Context, ActionParameters.Of(("setting", PowerSettingCatalog.CoreParkingMin), ("ac", "100")));
        Assert.False(support.IsSupported);
    }

    [Fact]
    public void Performance_plan_risk_mentions_battery_and_thermal_constraints()
    {
        var action = new PowerPlanActivateAction(new FakePowerPolicy());
        var risk = action.EvaluateRisk(ActionParameters.Of(("plan", WellKnownPowerPlans.HighPerformance.ToString())), Context with { OnBatteryPower = true, ThermalThrottlingObserved = true });
        Assert.Contains(risk.Reasons, r => r.Contains("Akkubetrieb"));
        Assert.Contains(risk.Reasons, r => r.Contains("Drosselung"));
    }
}

public class ServiceActionTests
{
    private static readonly SystemContext Context = new() { WindowsBuild = 22631 };

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("wuauserv")]
    [InlineData("BITS")]
    [InlineData("VaultSvc")]
    [InlineData("VSS")]
    [InlineData("RpcSs")]
    [InlineData("webthreatdefusersvc_1a2b3c")]
    public void Security_critical_services_can_never_be_changed(string service)
    {
        var action = new ServiceStartupAction(new FakeServiceManager().Add(service, ServiceStartMode.Automatic));
        var validation = action.ValidateParameters(ActionParameters.Of(("service", service), ("mode", "Disabled")));
        Assert.False(validation.IsValid);
    }

    [Theory]
    [InlineData("Fax; shutdown /s")]
    [InlineData("Fax\"")]
    [InlineData("")]
    public void Malformed_service_names_are_rejected(string service)
        => Assert.False(new ServiceStartupAction(new FakeServiceManager()).ValidateParameters(ActionParameters.Of(("service", service), ("mode", "Manual"))).IsValid);

    [Theory]
    [InlineData("Boot")]
    [InlineData("System")]
    [InlineData("Unknown")]
    public void Driver_start_modes_cannot_be_requested(string mode)
        => Assert.False(new ServiceStartupAction(new FakeServiceManager().Add("Fax", ServiceStartMode.Manual)).ValidateParameters(ActionParameters.Of(("service", "Fax"), ("mode", mode))).IsValid);

    [Fact]
    public async Task Service_change_roundtrip_and_never_in_automation()
    {
        var services = new FakeServiceManager().Add("Fax", ServiceStartMode.Automatic);
        var action = new ServiceStartupAction(services);
        var parameters = ActionParameters.Of(("service", "Fax"), ("mode", "Disabled"));
        Assert.False(action.Metadata.AllowInAutomation);
        Assert.Equal(PrivilegeLevel.Administrator, action.GetRequiredPrivilege(parameters));

        var backup = await action.CreateBackupAsync(parameters);
        Assert.True((await action.ApplyAsync(parameters)).Success);
        Assert.True((await action.VerifyAsync(parameters)).Verified);
        Assert.True((await action.RollbackAsync(parameters, backup, force: false)).Success);
        Assert.Equal(ServiceStartMode.Automatic, services.GetService("Fax")!.StartMode);
    }

    [Fact]
    public async Task Driver_services_are_unsupported()
    {
        var action = new ServiceStartupAction(new FakeServiceManager().Add("SomeDriver", ServiceStartMode.System, driver: true));
        Assert.False((await action.IsSupportedAsync(Context, ActionParameters.Of(("service", "SomeDriver"), ("mode", "Disabled")))).IsSupported);
    }

    [Fact]
    public void Disabling_unknown_service_with_dependents_is_high_risk()
    {
        var action = new ServiceStartupAction(new FakeServiceManager().Add("Unbekannt", ServiceStartMode.Automatic, false, "Abhaengig1"));
        var risk = action.EvaluateRisk(ActionParameters.Of(("service", "Unbekannt"), ("mode", "Disabled")), Context);
        Assert.Equal(RiskLevel.High, risk.Level);
    }

    [Fact]
    public async Task Rollback_rejects_backup_of_a_different_service()
    {
        var services = new FakeServiceManager().Add("Fax", ServiceStartMode.Automatic).Add("MapsBroker", ServiceStartMode.Automatic);
        var action = new ServiceStartupAction(services);
        var backup = await action.CreateBackupAsync(ActionParameters.Of(("service", "Fax"), ("mode", "Disabled")));

        var result = await action.RollbackAsync(ActionParameters.Of(("service", "MapsBroker"), ("mode", "Disabled")), backup, force: true);

        Assert.False(result.Success);
    }
}

public class StartupAndGpuActionTests
{
    private static readonly SystemContext Context = new() { WindowsBuild = 22631 };

    [Fact]
    public async Task Startup_entry_is_disabled_without_deleting_it_and_can_be_restored()
    {
        var registry = new InMemoryRegistryStore();
        var inventory = new FakeStartupInventory();
        inventory.Entries.Add(new StartupEntry { Source = StartupSource.RegistryRunUser, Name = "Updater" });
        var action = new StartupEntryAction(registry, inventory);
        var parameters = ActionParameters.Of(("source", "RegistryRunUser"), ("name", "Updater"), ("enabled", "false"));
        Assert.Equal(PrivilegeLevel.User, action.GetRequiredPrivilege(parameters));
        Assert.True((await action.IsSupportedAsync(Context, parameters)).IsSupported);

        var backup = await action.CreateBackupAsync(parameters);
        Assert.True((await action.ApplyAsync(parameters)).Success);
        Assert.True((await action.VerifyAsync(parameters)).Verified);

        var target = StartupApproval.Target(StartupSource.RegistryRunUser, "Updater");
        Assert.False(StartupApproval.IsEnabled(registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName)));

        Assert.True((await action.RollbackAsync(parameters, backup, force: false)).Success);
        Assert.Null(registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName)); // ursprünglich kein Wert → wieder entfernt
    }

    [Theory]
    [InlineData("RegistryRunMachine")]
    [InlineData("RegistryRunMachine32")]
    [InlineData("StartupFolderCommon")]
    public void Machine_wide_startup_entries_require_administrator(string source)
    {
        var action = new StartupEntryAction(new InMemoryRegistryStore(), new FakeStartupInventory());
        Assert.Equal(PrivilegeLevel.Administrator, action.GetRequiredPrivilege(ActionParameters.Of(("source", source), ("name", "X"), ("enabled", "false"))));
    }

    [Fact]
    public async Task Nonexistent_startup_entry_is_unsupported()
    {
        var action = new StartupEntryAction(new InMemoryRegistryStore(), new FakeStartupInventory());
        Assert.False((await action.IsSupportedAsync(Context, ActionParameters.Of(("source", "RegistryRunUser"), ("name", "Fehlt"), ("enabled", "false")))).IsSupported);
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 7, 0, 0, 0 }, false)]
    public void Startup_approved_state_is_decoded_like_task_manager(byte[] data, bool enabled)
        => Assert.Equal(enabled, StartupApproval.IsEnabled(RegistryValueData.Binary(data)));

    [Fact]
    public void Gpu_preference_preserves_other_tokens()
    {
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", GpuPreferenceAction.Compose("SwapEffectUpgradeEnable=1;GpuPreference=1;", 2));
        Assert.Equal("GpuPreference=1;", GpuPreferenceAction.Compose(null, 1));
        Assert.Equal(2, GpuPreferenceAction.Parse("SwapEffectUpgradeEnable=1;GpuPreference=2;"));
        Assert.Null(GpuPreferenceAction.Parse("SwapEffectUpgradeEnable=1;"));
    }

    [Fact]
    public async Task Gpu_preference_roundtrip()
    {
        var registry = new InMemoryRegistryStore();
        var action = new GpuPreferenceAction(registry);
        var parameters = ActionParameters.Of(("path", @"C:\Spiele\spiel.exe"), ("preference", "high-performance"));

        var backup = await action.CreateBackupAsync(parameters);
        Assert.True((await action.ApplyAsync(parameters)).Success);
        Assert.True((await action.VerifyAsync(parameters)).Verified);
        Assert.True((await action.DetectCurrentStateAsync(parameters)).IsApplied);
        Assert.True((await action.RollbackAsync(parameters, backup, false)).Success);
        Assert.False((await action.DetectCurrentStateAsync(parameters)).IsApplied);
    }

    [Theory]
    [InlineData(@"C:\Spiele\spiel.dll")]
    [InlineData(@"spiel.exe")]
    [InlineData(@"C:\Spiele\..\spiel.exe")]
    public void Gpu_preference_only_accepts_full_exe_paths(string path)
        => Assert.False(new GpuPreferenceAction(new InMemoryRegistryStore()).ValidateParameters(ActionParameters.Of(("path", path), ("preference", "default"))).IsValid);
}

public class SystemToolAndMiscActionTests
{
    private static readonly SystemContext Context = new() { WindowsBuild = 22631 };

    [Fact]
    public async Task Tool_success_is_derived_from_the_real_exit_code()
    {
        var runner = new FakeToolRunner { ExitCode = 0 };
        var action = new SystemToolAction(SystemToolCatalog.Find(SystemToolCatalog.SfcScan)!, runner);

        Assert.True((await action.ApplyAsync(ActionParameters.Empty)).Success);
        Assert.True((await action.VerifyAsync(ActionParameters.Empty)).Verified);

        runner.ExitCode = 1;
        Assert.False((await action.ApplyAsync(ActionParameters.Empty)).Success);
        Assert.False((await action.VerifyAsync(ActionParameters.Empty)).Verified);
    }

    [Fact]
    public async Task Tool_that_cannot_start_or_times_out_is_a_failure()
    {
        var action = new SystemToolAction(SystemToolCatalog.Find(SystemToolCatalog.DismCheckHealth)!, new FakeToolRunner { Throw = true });
        Assert.False((await action.ApplyAsync(ActionParameters.Empty)).Success);
        Assert.False((await action.VerifyAsync(ActionParameters.Empty)).Verified);

        var timeout = new SystemToolAction(SystemToolCatalog.Find(SystemToolCatalog.DismCheckHealth)!, new FakeToolRunner { TimedOut = true });
        Assert.False((await timeout.ApplyAsync(ActionParameters.Empty)).Success);
    }

    [Fact]
    public async Task Drive_argument_is_validated_and_substituted_without_shell()
    {
        var runner = new FakeToolRunner();
        var action = new SystemToolAction(SystemToolCatalog.Find(SystemToolCatalog.ChkdskScan)!, runner);

        Assert.False(action.ValidateParameters(ActionParameters.Of(("drive", "C: & format D:"))).IsValid);
        Assert.False(action.ValidateParameters(ActionParameters.Empty).IsValid);

        await action.ApplyAsync(ActionParameters.Of(("drive", "c:")));
        Assert.Equal(new[] { "C:", "/scan" }, runner.Calls.Single().Arguments);
    }

    [Fact]
    public async Task Retrim_is_refused_for_hard_disks()
    {
        var action = new SystemToolAction(SystemToolCatalog.Find(SystemToolCatalog.DriveRetrim)!, new FakeToolRunner(), isSolidState: drive => drive == "C:");
        Assert.True((await action.IsSupportedAsync(Context, ActionParameters.Of(("drive", "C:")))).IsSupported);
        Assert.False((await action.IsSupportedAsync(Context, ActionParameters.Of(("drive", "D:")))).IsSupported);
    }

    [Fact]
    public void Tools_are_never_reversible_and_never_automated()
    {
        foreach (var definition in SystemToolCatalog.All)
        {
            var action = new SystemToolAction(definition, new FakeToolRunner());
            Assert.False(action.Metadata.IsReversible);
            Assert.False(action.Metadata.AllowInAutomation);
            Assert.Equal(definition.ReadOnly, action.Metadata.IsDiagnosticOnly);
        }
    }

    [Fact]
    public async Task Dns_change_roundtrip_restores_automatic_configuration()
    {
        var network = new FakeNetworkConfigurator();
        var action = new DnsSetAction(network);
        var parameters = ActionParameters.Of(("adapter", FakeNetworkConfigurator.AdapterId), ("servers", "1.1.1.1,1.0.0.1"));

        var backup = await action.CreateBackupAsync(parameters);
        Assert.True((await action.ApplyAsync(parameters)).Success);
        Assert.True((await action.VerifyAsync(parameters)).Verified);
        Assert.False(network.Automatic);

        Assert.True((await action.RollbackAsync(parameters, backup, false)).Success);
        Assert.True(network.Automatic);
    }

    [Theory]
    [InlineData("nicht-guid", "1.1.1.1")]
    [InlineData(FakeNetworkConfigurator.AdapterId, "dns.example.com")]
    [InlineData(FakeNetworkConfigurator.AdapterId, "2606:4700:4700::1111")]
    public void Dns_parameters_are_validated(string adapter, string servers)
        => Assert.False(new DnsSetAction(new FakeNetworkConfigurator()).ValidateParameters(ActionParameters.Of(("adapter", adapter), ("servers", servers))).IsValid);

    [Fact]
    public async Task Process_priority_is_restored_only_for_the_same_process_instance()
    {
        var processes = new FakeProcessControl();
        processes.Processes[100] = new ProcessIdentity(100, "spiel", StartTimeTicks: 1, ProcessPriorityLevel.Normal, IsProtected: false);
        var action = new ProcessPriorityAction(processes);
        var parameters = ActionParameters.Of(("pid", "100"), ("priority", "High"));

        var backup = await action.CreateBackupAsync(parameters);
        Assert.True((await action.ApplyAsync(parameters)).Success);
        Assert.Equal(ProcessPriorityLevel.High, processes.Processes[100].Priority);

        // PID wurde nach Prozessende neu vergeben → fremder Prozess darf nicht angefasst werden.
        processes.Processes[100] = new ProcessIdentity(100, "anderes", StartTimeTicks: 2, ProcessPriorityLevel.High, IsProtected: false);
        var result = await action.RollbackAsync(parameters, backup, false);

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityLevel.High, processes.Processes[100].Priority);
    }

    [Theory]
    [InlineData("MsMpEng")]
    [InlineData("lsass")]
    [InlineData("csrss")]
    public async Task Protected_processes_cannot_be_reprioritized(string name)
    {
        var processes = new FakeProcessControl();
        processes.Processes[100] = new ProcessIdentity(100, name, 1, ProcessPriorityLevel.Normal, false);
        var action = new ProcessPriorityAction(processes);
        var parameters = ActionParameters.Of(("pid", "100"), ("priority", "Idle"));

        Assert.False((await action.IsSupportedAsync(Context, parameters)).IsSupported);
        Assert.False((await action.ApplyAsync(parameters)).Success);
    }

    [Fact]
    public void Realtime_priority_is_not_offered()
        => Assert.False(new ProcessPriorityAction(new FakeProcessControl()).ValidateParameters(ActionParameters.Of(("pid", "100"), ("priority", "RealTime"))).IsValid);
}

public class CleanupTests
{
    private static (CleanupEngine Engine, FakeCleanupLocations Locations) Create(ManualTimeProvider? time = null)
    {
        var locations = new FakeCleanupLocations();
        return (new CleanupEngine(locations, null, time), locations);
    }

    [Fact]
    public void Deletes_only_files_older_than_the_minimum_age()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        var (engine, locations) = Create(time);
        var old = temp.File("cache/alt.tmp", 100, DateTime.UtcNow.AddDays(-3));
        var fresh = temp.File("cache/neu.tmp", 100, DateTime.UtcNow);
        locations.Locations[CleanupCategories.UserTemp] = new() { new CleanupLocation(Path.Combine(temp.Path, "cache"), MinAge: TimeSpan.FromHours(24)) };

        var scan = engine.Scan(CleanupCategories.UserTemp);
        Assert.Equal(1, scan.Files);
        Assert.Equal(100, scan.Bytes);

        var result = engine.Run(CleanupCategories.UserTemp);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(100, result.FreedBytes);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Never_follows_symbolic_links_out_of_the_cleanup_root()
    {
        using var temp = new TempDirectory();
        var (engine, locations) = Create();
        var precious = temp.File("dokumente/wichtig.docx", 50);
        temp.File("cache/muell.tmp", 10);
        var link = Path.Combine(temp.Path, "cache", "verweis");
        try { Directory.CreateSymbolicLink(link, Path.Combine(temp.Path, "dokumente")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // ohne Symlink-Recht nicht testbar
        locations.Locations[CleanupCategories.UserTemp] = new() { new CleanupLocation(Path.Combine(temp.Path, "cache")) };

        var result = engine.Run(CleanupCategories.UserTemp);

        Assert.Equal(1, result.DeletedFiles);
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void Root_itself_and_pattern_mismatches_are_preserved_and_empty_subfolders_removed()
    {
        using var temp = new TempDirectory();
        var (engine, locations) = Create();
        temp.File("explorer/thumbcache_96.db", 10);
        var other = temp.File("explorer/andere.dat", 10);
        temp.File("cache/sub/a.tmp", 5);
        locations.Locations[CleanupCategories.ThumbnailCache] = new() { new CleanupLocation(Path.Combine(temp.Path, "explorer"), "thumbcache_*.db", Recursive: false) };
        locations.Locations[CleanupCategories.UserTemp] = new() { new CleanupLocation(Path.Combine(temp.Path, "cache")) };

        Assert.Equal(1, engine.Run(CleanupCategories.ThumbnailCache).DeletedFiles);
        engine.Run(CleanupCategories.UserTemp);

        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "cache")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "cache", "sub")));
    }

    [Theory]
    [InlineData("relativ/pfad")]
    [InlineData("/")]
    [InlineData("/tmp")]
    public void Overly_broad_or_relative_roots_are_rejected(string root)
        => Assert.False(CleanupEngine.IsAcceptableRoot(root, out _));

    [Theory]
    [InlineData("Documents")]
    [InlineData("Desktop")]
    [InlineData("AppData")]
    [InlineData("User Data")]
    public void Personal_and_profile_folders_are_never_acceptable_roots(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "x", name);
        Assert.False(CleanupEngine.IsAcceptableRoot(root, out _));
    }

    [Fact]
    public void Forbidden_root_from_a_faulty_provider_is_reported_and_nothing_is_deleted()
    {
        using var temp = new TempDirectory();
        var (engine, locations) = Create();
        var file = temp.File("Documents/brief.docx", 10);
        locations.Locations[CleanupCategories.UserTemp] = new() { new CleanupLocation(Path.Combine(temp.Path, "Documents")) };

        var result = engine.Run(CleanupCategories.UserTemp);

        Assert.Equal(0, result.DeletedFiles);
        Assert.NotEmpty(result.Errors);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Browser_cache_is_skipped_while_the_browser_is_running()
    {
        using var temp = new TempDirectory();
        var (engine, locations) = Create();
        var file = temp.File("edge/Cache_Data/f_0001", 10);
        locations.Locations[CleanupCategories.EdgeCache] = new() { new CleanupLocation(Path.Combine(temp.Path, "edge", "Cache_Data")) };
        locations.Running.Add("msedge");

        var result = engine.Run(CleanupCategories.EdgeCache);

        Assert.NotNull(result.BlockedReason);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Cleanup_action_is_irreversible_and_reports_real_numbers()
    {
        using var temp = new TempDirectory();
        var (engine, locations) = Create();
        temp.File("dumps/a.dmp", 1000);
        temp.File("dumps/b.dmp", 500);
        locations.Locations[CleanupCategories.UserCrashDumps] = new() { new CleanupLocation(Path.Combine(temp.Path, "dumps")) };
        var action = new CleanupAction(engine, PrivilegeLevel.User);
        var parameters = ActionParameters.Of(("categories", CleanupCategories.UserCrashDumps));

        Assert.False(action.Metadata.IsReversible);
        Assert.False(action.Metadata.AllowInAutomation);
        Assert.False(action.ValidateParameters(ActionParameters.Of(("categories", CleanupCategories.WindowsTemp))).IsValid); // Admin-Kategorie nicht in der Benutzeraktion

        var preview = await action.PreviewAsync(parameters);
        Assert.True(preview.WillChange);
        var result = await action.ApplyAsync(parameters);
        Assert.True(result.Success);
        Assert.Equal(1500, action.LastResults.Sum(r => r.FreedBytes));
        Assert.True((await action.VerifyAsync(parameters)).Verified);
        Assert.False((await action.RollbackAsync(parameters, new BackupData(action.Metadata.Id, "{}", "", ""), true)).Success);
    }

    [Fact]
    public void Duplicate_finder_requires_identical_content_not_just_identical_size()
    {
        using var temp = new TempDirectory();
        temp.File("a/eins.bin", 4096, fill: 1);
        temp.File("b/kopie.bin", 4096, fill: 1);
        temp.File("c/gleich-gross.bin", 4096, fill: 2);
        temp.File("d/klein.bin", 10, fill: 1);

        var groups = FileAnalysis.FindDuplicates(temp.Path, minimumBytes: 1024);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Paths.Count);
        Assert.Equal(4096, group.WastedBytes);
        Assert.All(group.Paths, p => Assert.DoesNotContain("gleich-gross", p));
    }

    [Fact]
    public void Largest_files_are_returned_in_descending_order()
    {
        using var temp = new TempDirectory();
        temp.File("x/klein.bin", 100);
        temp.File("x/gross.bin", 5000);
        temp.File("y/mittel.bin", 2000);

        var files = FileAnalysis.FindLargestFiles(temp.Path, count: 2, minimumBytes: 1);

        Assert.Equal(new long[] { 5000, 2000 }, files.Select(f => f.Bytes));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(1073741824, "1,0 GB")]
    public void Byte_format_uses_german_notation(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Format(bytes));
}
