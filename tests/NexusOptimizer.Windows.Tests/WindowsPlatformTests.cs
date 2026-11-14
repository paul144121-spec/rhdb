using System.IO.Pipes;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Security;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.Tools;
using NexusOptimizer.Data;
using NexusOptimizer.Windows.Hardware;
using NexusOptimizer.Windows.Monitoring;
using NexusOptimizer.Windows.SystemAccess;

namespace NexusOptimizer.Windows.Tests;

/// <summary>
/// Integrationstests gegen ein echtes Windows. Alle Tests sind lesend – mit Ausnahme ausdrücklich gekennzeichneter
/// Wegwerf-Ressourcen (Testschlüssel unter HKCU, temporäre Ordner). Es werden keine Systemeinstellungen des Hosts verändert.
/// </summary>
public class PowerPolicyTests
{
    private readonly WindowsPowerPolicy _power = new();

    [Fact]
    public void Lists_plans_and_exactly_one_is_active()
    {
        var plans = _power.GetPlans();
        Assert.NotEmpty(plans);
        Assert.Single(plans, p => p.IsActive);
        Assert.Equal(_power.GetActivePlan(), plans.Single(p => p.IsActive).Id);
        Assert.All(plans, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Reads_processor_settings_of_the_active_plan_or_reports_them_as_missing()
    {
        var plan = _power.GetActivePlan();
        var definition = PowerSettingCatalog.Find(PowerSettingCatalog.ProcessorMax)!;
        var value = _power.ReadAcValue(plan, definition.Subgroup, definition.Setting);
        if (value is not null) Assert.InRange(value.Value, 0u, 100u);
    }

    [Fact]
    public void Unknown_setting_reads_as_null_instead_of_throwing()
        => Assert.Null(_power.ReadAcValue(_power.GetActivePlan(), Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public async Task Activating_the_already_active_plan_is_recognized_as_no_change()
    {
        var action = new PowerPlanActivateAction(_power);
        var preview = await action.PreviewAsync(ActionParameters.Of(("plan", _power.GetActivePlan().ToString())));
        Assert.False(preview.WillChange);
    }

    [Fact]
    public void Built_in_plans_cannot_be_deleted()
        => Assert.Throws<InvalidOperationException>(() => _power.DeletePlan(WellKnownPowerPlans.Balanced));
}

public class ServiceAndStartupInspectionTests
{
    [Fact]
    public void Service_inventory_contains_core_windows_services_with_details()
    {
        var services = new WindowsServiceManager();
        var all = services.GetServices();
        Assert.True(all.Count > 20);
        var rpc = services.GetService("RpcSs");
        Assert.NotNull(rpc);
        Assert.True(rpc!.IsRunning);
        Assert.NotEqual(ServiceStartMode.Unknown, rpc.StartMode);
    }

    [Theory]
    [InlineData("Rpc'Ss")]
    [InlineData("x' OR Name LIKE '%")]
    [InlineData("")]
    public void Service_lookup_rejects_names_that_could_alter_the_query(string name)
        => Assert.Null(new WindowsServiceManager().GetService(name));

    [Fact]
    public void Protected_service_is_refused_before_any_system_call()
        => Assert.Throws<InvalidOperationException>(() => new WindowsServiceManager().SetStartMode("WinDefend", ServiceStartMode.Disabled));

    [Fact]
    public void Startup_inventory_can_be_read_and_every_entry_has_a_source_and_name()
    {
        var entries = new WindowsStartupInventory(new RegistryAccess.WindowsRegistryStore()).GetEntries();
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe /start", "C:\\Tools\\tool.exe")]
    [InlineData("rundll32 shell32.dll,Control", "rundll32")]
    public void Startup_command_lines_are_parsed_for_display(string command, string expected)
        => Assert.Equal(expected, WindowsStartupInventory.ExtractExecutable(command));
}

public class MonitoringCollectionTests
{
    [Fact]
    public async Task Collector_returns_real_memory_and_cpu_values()
    {
        using var collector = new WindowsMetricsCollector();
        collector.Collect();
        await Task.Delay(1200); // Ratenzähler benötigen zwei Abrufe
        var metrics = collector.Collect();

        Assert.True(metrics.MemoryTotalBytes > 512UL * 1024 * 1024);
        Assert.InRange(metrics.MemoryAvailableBytes, 1UL, metrics.MemoryTotalBytes);
        Assert.True(metrics.CommitLimitBytes >= metrics.CommitUsedBytes);
        Assert.NotNull(metrics.CpuTotalPercent);
        Assert.InRange(metrics.CpuTotalPercent!.Value, 0, 100);
        Assert.True(metrics.ProcessCount > 5);
        Assert.True(metrics.Uptime > TimeSpan.Zero);
        Assert.All(metrics.CpuPerCorePercent, v => Assert.InRange(v, 0, 100));
    }

    [Fact]
    public void Missing_sensors_are_reported_with_a_reason_and_never_as_numbers()
    {
        using var collector = new WindowsMetricsCollector { NvidiaSensorsEnabled = false };
        var metrics = collector.Collect();
        Assert.Null(metrics.GpuTemperatureCelsius);
        Assert.Null(metrics.CpuPackageCelsius);
        Assert.Null(metrics.LatencyMs);
        Assert.True(collector.UnavailableReasons.ContainsKey(MetricId.GpuTemperature));
        Assert.True(collector.UnavailableReasons.ContainsKey(MetricId.Latency));
    }

    [Fact]
    public async Task Monitoring_service_collects_and_persists_history()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-tests", Guid.NewGuid().ToString("N"), "m.db");
        try
        {
            var store = new SqliteMetricHistoryStore(new NexusDatabase(path));
            using var service = new MonitoringService(new WindowsMetricsCollector(), store) { Options = new MonitoringOptions { Interval = TimeSpan.FromMilliseconds(500), PersistInterval = TimeSpan.FromSeconds(1) } };
            service.Start();
            await Task.Delay(2600);
            await service.StopAsync();

            Assert.NotNull(service.Latest);
            Assert.NotEmpty(store.Query(MetricId.MemoryUsedPercent, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Process_monitor_lists_this_process_and_protects_system_processes()
    {
        var monitor = new WindowsProcessMonitor();
        monitor.GetProcesses();
        var processes = monitor.GetProcesses();
        var self = processes.Single(p => p.ProcessId == Environment.ProcessId);
        Assert.True(self.WorkingSetBytes > 0);
        Assert.NotNull(self.ExecutablePath);
        Assert.Contains(monitor.GetRunningProcessNames(), p => p.ProcessId == Environment.ProcessId);

        Assert.False(monitor.Terminate(4).Success);              // System
        Assert.False(monitor.Terminate(Environment.ProcessId).Success);
        Assert.NotNull(monitor.Find(Environment.ProcessId));
        Assert.Null(monitor.Find(int.MaxValue - 7));
    }
}

public class HardwareAndStorageTests
{
    [Fact]
    public async Task Hardware_information_comes_from_the_real_system()
    {
        var info = await new WindowsHardwareInfoProvider(new WindowsPowerPolicy()).GetSystemInfoAsync();
        Assert.Equal(Environment.OSVersion.Version.Build, info.OsBuild);
        Assert.False(string.IsNullOrWhiteSpace(info.CpuName));
        Assert.True(info.MemoryTotalBytes > 0);
        Assert.Equal(Environment.ProcessorCount, info.CpuLogicalProcessors);
        Assert.Contains(info.Volumes, v => v.IsSystem && v.TotalBytes > 0);
        Assert.Equal(Environment.MachineName, info.ComputerName);
    }

    [Theory]
    [InlineData("NVIDIA", "NVIDIA GeForce RTX 4070", false)]
    [InlineData("Intel", "Intel(R) UHD Graphics 770", true)]
    [InlineData("Intel", "Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("Intel", "Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Intel", "Intel(R) Arc(TM) Graphics", true)]
    [InlineData("AMD", "AMD Radeon RX 7800 XT", false)]
    [InlineData("AMD", "AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD", "AMD Radeon(TM) 780M", true)]
    [InlineData("Microsoft", "Microsoft Basic Display Adapter", true)]
    public void Integrated_graphics_heuristic(string vendor, string name, bool integrated)
        => Assert.Equal(integrated, WindowsHardwareInfoProvider.IsIntegratedGpu(vendor, name));

    [Fact]
    public void Every_cleanup_location_is_an_acceptable_narrow_root()
    {
        var provider = new WindowsCleanupLocations();
        foreach (var category in CleanupCategories.All.Where(c => c.Id != CleanupCategories.RecycleBin))
        {
            foreach (var location in provider.GetLocations(category.Id))
                Assert.True(CleanupEngine.IsAcceptableRoot(location.Root, out var reason), $"{category.Id}: {location.Root} – {reason}");
        }
    }

    [Fact]
    public void Cleanup_scan_is_read_only_and_reports_sizes()
    {
        var engine = new CleanupEngine(new WindowsCleanupLocations(), new WindowsRecycleBin());
        var before = Directory.Exists(Path.GetTempPath()) ? Directory.EnumerateFileSystemEntries(Path.GetTempPath()).Count() : 0;
        foreach (var category in CleanupCategories.For(PrivilegeLevel.User))
        {
            var scan = engine.Scan(category.Id);
            Assert.True(scan.Bytes >= 0);
        }
        var after = Directory.Exists(Path.GetTempPath()) ? Directory.EnumerateFileSystemEntries(Path.GetTempPath()).Count() : 0;
        Assert.True(after >= before - 5); // der Scan selbst löscht nichts (andere Prozesse können parallel Dateien entfernen)
    }

    [Fact]
    public void Secrets_roundtrip_through_dpapi_and_are_not_stored_in_plain_text()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nexus-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiSecretStore(directory);
            store.Set("anthropic-api-key", "sk-ant-geheim-123456");
            Assert.True(store.Has("anthropic-api-key"));
            Assert.Equal("sk-ant-geheim-123456", new DpapiSecretStore(directory).Get("anthropic-api-key"));
            Assert.DoesNotContain("geheim", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(directory).Single())));

            store.Set("anthropic-api-key", null);
            Assert.False(store.Has("anthropic-api-key"));
            Assert.Throws<ArgumentException>(() => store.Set("..\\boese", "x"));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Tool_runner_captures_output_and_exit_code_of_a_harmless_builtin_tool()
    {
        var result = await new WindowsSystemToolRunner().RunAsync(SystemTool.IpConfig, Array.Empty<string>(), null, TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public void Signature_check_recognizes_signed_windows_binaries_and_unsigned_files()
    {
        Assert.Equal(SignatureStatus.Valid, AuthenticodeVerifier.Verify(Path.Combine(Environment.SystemDirectory, "ntdll.dll")));
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, "kein Programm");
            Assert.NotEqual(SignatureStatus.Valid, AuthenticodeVerifier.Verify(temp));
        }
        finally { File.Delete(temp); }
    }
}

public class EngineOnRealRegistryTests : IDisposable
{
    private static readonly string Root = @"Software\NexusOptimizerAI.Tests." + Guid.NewGuid().ToString("N");
    private readonly string _key = Root + @"\" + Guid.NewGuid().ToString("N");
    private readonly string _dbDirectory = Path.Combine(Path.GetTempPath(), "nexus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDirectory, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Complete_workflow_plan_apply_verify_persist_and_rollback_after_restart()
    {
        // Wegwerf-Tweak auf einem Testschlüssel: derselbe Codepfad wie echte Katalogeinträge, aber ohne Host-Einstellungen zu berühren.
        var definition = new RegistryTweakDefinition
        {
            Id = "test.disposable", Name = "Testwert", Description = "Nur für Tests", Category = ActionCategory.Interface, Root = RegistryRoot.CurrentUser,
            KeyPath = _key, ValueName = "Wert", OptimizedValue = RegistryValueData.DWord(1), WindowsDefault = RegistryValueData.DWord(0),
            OptimizedLabel = "Ein", DefaultLabel = "Aus", ExpectedBehavior = "Test",
        };
        var store = new RegistryAccess.WindowsRegistryStore();
        store.WriteValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "Wert", RegistryValueData.DWord(7));
        var catalog = new NexusOptimizer.Core.Actions.ActionCatalog(new[] { new RegistryTweakAction(definition, store) });
        var journal = new SqliteTransactionJournal(new NexusDatabase(Path.Combine(_dbDirectory, "nexus.db")));
        var planner = new OptimizationPlanner(catalog);
        var executor = new OptimizationExecutor(new LocalActionRunner(catalog), journal, new ExecutionGate());

        var plan = await planner.BuildAsync(new[] { new PlanRequest(definition.ActionId) }, new RiskPolicy(), new SystemContext { WindowsBuild = Environment.OSVersion.Version.Build }, ExecutionSource.User);
        Assert.Single(plan.Steps);
        Assert.Contains("7", plan.Steps[0].Preview.CurrentValue);

        var batch = await executor.ExecuteAsync(plan);
        Assert.True(batch.AllSucceeded);
        Assert.Equal(1u, store.ReadValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "Wert")!.AsDWord());

        // „Neustart“ der Anwendung: neues Journalobjekt auf derselben Datenbank.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var journal2 = new SqliteTransactionJournal(new NexusDatabase(Path.Combine(_dbDirectory, "nexus.db")));
        Assert.Equal(TransactionStatus.Verified, journal2.GetRecent().Single().Status);
        var rollback = new RollbackService(new OptimizationExecutor(new LocalActionRunner(catalog), journal2, new ExecutionGate()), journal2);

        var outcome = await rollback.RollbackLastAsync();

        Assert.True(outcome!.Success);
        Assert.Equal(7u, store.ReadValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, _key, "Wert")!.AsDWord());
    }

    [Fact]
    public async Task Pipe_protocol_works_over_a_real_named_pipe_and_enforces_the_allowlist()
    {
        var store = new RegistryAccess.WindowsRegistryStore();
        var catalog = new NexusOptimizer.Core.Actions.ActionCatalog(RegistryTweakCatalog.All.Select(t => new RegistryTweakAction(t, store)));
        var handler = new PipeRequestHandler(catalog, () => new SystemContext { WindowsBuild = Environment.OSVersion.Version.Build, IsElevated = true });
        var name = "NexusOptimizerAI.Tests." + Guid.NewGuid().ToString("N");

        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            while (await PipeProtocol.ReadAsync<PipeRequest>(server) is { } request && request.Operation != PipeOperation.Shutdown)
                await PipeProtocol.WriteAsync(server, await handler.HandleAsync(request, null));
        });

        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);

        async Task<PipeResponse> Send(PipeRequest request)
        {
            await PipeProtocol.WriteAsync(client, request);
            return (await PipeProtocol.ReadAsync<PipeResponse>(client))!;
        }

        Assert.True((await Send(new PipeRequest { Operation = PipeOperation.Ping })).Ok);
        Assert.False((await Send(new PipeRequest { Operation = PipeOperation.Apply, ActionId = "cmd.exe" })).Ok);
        // Benutzeraktionen (HKCU) führt die Administratorkomponente nicht aus – hier wird also nichts verändert.
        Assert.False((await Send(new PipeRequest { Operation = PipeOperation.Apply, ActionId = "reg.explorer.show-file-extensions" })).Ok);

        await PipeProtocol.WriteAsync(client, new PipeRequest { Operation = PipeOperation.Shutdown });
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
