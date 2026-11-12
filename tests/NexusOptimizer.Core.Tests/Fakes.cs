using NexusOptimizer.Core.Automation;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Tools;

namespace NexusOptimizer.Core.Tests;

internal sealed class FakePowerPolicy : IPowerPolicy
{
    public Dictionary<Guid, string> Plans { get; } = new()
    {
        [WellKnownPowerPlans.Balanced] = "Ausbalanciert",
        [WellKnownPowerPlans.HighPerformance] = "Höchstleistung",
        [WellKnownPowerPlans.PowerSaver] = "Energiesparmodus",
    };
    public Guid Active { get; set; } = WellKnownPowerPlans.Balanced;
    public Dictionary<(Guid Plan, Guid Setting, bool Ac), uint> Values { get; } = new();
    public HashSet<Guid> MissingSettings { get; } = new();
    public bool FailWrites { get; set; }
    public int ActivationCount { get; private set; }

    public IReadOnlyList<PowerPlanInfo> GetPlans() => Plans.Select(p => new PowerPlanInfo(p.Key, p.Value, p.Key == Active)).ToList();
    public Guid GetActivePlan() => Active;

    public void SetActivePlan(Guid plan)
    {
        if (FailWrites) throw new UnauthorizedAccessException("verweigert");
        if (!Plans.ContainsKey(plan)) throw new InvalidOperationException("Plan fehlt");
        Active = plan;
        ActivationCount++;
    }

    public uint? ReadAcValue(Guid plan, Guid subgroup, Guid setting) => MissingSettings.Contains(setting) ? null : Values.GetValueOrDefault((plan, setting, true), 50u);
    public uint? ReadDcValue(Guid plan, Guid subgroup, Guid setting) => MissingSettings.Contains(setting) ? null : Values.GetValueOrDefault((plan, setting, false), 50u);

    public void WriteAcValue(Guid plan, Guid subgroup, Guid setting, uint value)
    {
        if (FailWrites) throw new UnauthorizedAccessException("verweigert");
        Values[(plan, setting, true)] = value;
    }

    public void WriteDcValue(Guid plan, Guid subgroup, Guid setting, uint value)
    {
        if (FailWrites) throw new UnauthorizedAccessException("verweigert");
        Values[(plan, setting, false)] = value;
    }

    public Guid DuplicatePlan(Guid source, string name) { var id = Guid.NewGuid(); Plans[id] = name; return id; }
    public void DeletePlan(Guid plan) => Plans.Remove(plan);
    public void RenamePlan(Guid plan, string name) => Plans[plan] = name;
}

internal sealed class FakeServiceManager : IServiceManager
{
    public Dictionary<string, ServiceInfo> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailWrites { get; set; }

    public FakeServiceManager Add(string name, ServiceStartMode mode, bool driver = false, params string[] dependents)
    {
        Services[name] = new ServiceInfo { Name = name, DisplayName = name, StartMode = mode, State = "Running", IsRunning = true, IsDriver = driver, Dependents = dependents };
        return this;
    }

    public IReadOnlyList<ServiceInfo> GetServices() => Services.Values.ToList();
    public ServiceInfo? GetService(string name) => Services.GetValueOrDefault(name);

    public void SetStartMode(string name, ServiceStartMode mode)
    {
        if (FailWrites) throw new UnauthorizedAccessException("verweigert");
        Services[name] = Services[name] with { StartMode = mode };
    }
}

internal sealed class FakeStartupInventory : IStartupInventory
{
    public List<StartupEntry> Entries { get; } = new();
    public IReadOnlyList<StartupEntry> GetEntries() => Entries;
}

internal sealed class FakeNetworkConfigurator : INetworkConfigurator
{
    public const string AdapterId = "{11111111-2222-3333-4444-555555555555}";
    public bool Automatic { get; set; } = true;
    public List<string> Servers { get; set; } = new() { "192.168.1.1" };

    public IReadOnlyList<AdapterDnsInfo> GetAdapters() => new[] { new AdapterDnsInfo(AdapterId, "Ethernet", Automatic, Servers.ToList()) };

    public void SetDnsServers(string adapterId, IReadOnlyList<string>? servers)
    {
        if (servers is null or { Count: 0 }) { Automatic = true; Servers = new List<string> { "192.168.1.1" }; }
        else { Automatic = false; Servers = servers.ToList(); }
    }
}

internal sealed class FakeProcessControl : IProcessControl
{
    public Dictionary<int, ProcessIdentity> Processes { get; } = new();
    public ProcessIdentity? Find(int processId) => Processes.GetValueOrDefault(processId);
    public void SetPriority(int processId, ProcessPriorityLevel priority) => Processes[processId] = Processes[processId] with { Priority = priority };
}

internal sealed class FakeToolRunner : ISystemToolRunner
{
    public int ExitCode { get; set; }
    public bool Throw { get; set; }
    public bool TimedOut { get; set; }
    public List<(SystemTool Tool, IReadOnlyList<string> Arguments)> Calls { get; } = new();

    public Task<ToolResult> RunAsync(SystemTool tool, IReadOnlyList<string> arguments, IProgress<string>? output, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls.Add((tool, arguments));
        if (Throw) throw new FileNotFoundException("Werkzeug fehlt");
        output?.Report("Zeile 1");
        return Task.FromResult(new ToolResult(ExitCode, "Ausgabe", TimedOut, TimeSpan.FromSeconds(1)));
    }
}

internal sealed class FakeCleanupLocations : ICleanupLocationProvider
{
    public Dictionary<string, List<CleanupLocation>> Locations { get; } = new();
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<CleanupLocation> GetLocations(string categoryId) => Locations.GetValueOrDefault(categoryId) ?? new List<CleanupLocation>();
    public bool IsProcessRunning(string processName) => Running.Contains(processName);
}

internal sealed class FakeMetricsCollector : IMetricsCollector
{
    public Queue<SystemMetrics> Queue { get; } = new();
    public SystemMetrics Default { get; set; } = new() { CpuTotalPercent = 10, MemoryTotalBytes = 16UL << 30, MemoryAvailableBytes = 8UL << 30 };
    public bool Throw { get; set; }
    public int Calls { get; private set; }
    public IReadOnlyDictionary<MetricId, string> UnavailableReasons { get; } = new Dictionary<MetricId, string>();

    public SystemMetrics Collect()
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("Sensorfehler");
        return Queue.Count > 0 ? Queue.Dequeue() : Default with { Timestamp = DateTimeOffset.UtcNow };
    }

    public void Dispose() { }
}

internal sealed class FakeHistoryStore : IMetricHistoryStore
{
    public List<(DateTimeOffset Timestamp, IReadOnlyDictionary<MetricId, double> Values)> Appended { get; } = new();
    public int PruneCalls { get; private set; }
    public void Append(DateTimeOffset timestamp, IReadOnlyDictionary<MetricId, double> values) => Appended.Add((timestamp, values));
    public IReadOnlyList<MetricPoint> Query(MetricId metric, DateTimeOffset from, DateTimeOffset to, int maxPoints = 600) => Array.Empty<MetricPoint>();
    public int Prune(DateTimeOffset olderThan) { PruneCalls++; return 0; }
    public void DeleteAll() => Appended.Clear();
}

internal sealed class FakeProcessMonitor : IProcessMonitor
{
    public List<ProcessSnapshot> Processes { get; } = new();
    public IReadOnlyList<ProcessSnapshot> GetProcesses() => Processes.ToList();
    public OperationResult Terminate(int processId) => OperationResult.Ok();
    public IReadOnlyList<(int ProcessId, string Name)> GetRunningProcessNames() => Processes.Select(p => (p.ProcessId, p.Name)).ToList();
}

internal sealed class FakeAutomationContext : IAutomationContext
{
    public bool? OnAcPower { get; set; } = true;
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsProcessRunning(string processName) => Running.Contains(processName);
    public double? CpuPercent { get; set; } = 10;
    public bool IsGameRunning { get; set; }
}

internal sealed class FakeAutomationExecutor : IAutomationActionExecutor
{
    public List<(string Rule, AutomationAction Action)> Executed { get; } = new();
    public List<IReadOnlyList<string>> Restored { get; } = new();
    public bool Fail { get; set; }
    private int _batch;

    public Task<AutomationActionResult> ExecuteAsync(AutomationRule rule, AutomationAction action, CancellationToken cancellationToken)
    {
        Executed.Add((rule.Name, action));
        return Task.FromResult(Fail ? new AutomationActionResult(false, "Fehler") : new AutomationActionResult(true, "OK", action.Type == RuleActionType.Notify ? null : "batch" + ++_batch));
    }

    public Task<AutomationActionResult> RestoreAsync(IReadOnlyList<string> batchIds, CancellationToken cancellationToken)
    {
        Restored.Add(batchIds.ToList());
        return Task.FromResult(new AutomationActionResult(true, "Wiederhergestellt"));
    }
}

/// <summary>Steuerbare Uhr für zeitabhängige Tests.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-tests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public string File(string relative, int bytes = 16, DateTime? lastWriteUtc = null, byte fill = 7)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, Enumerable.Repeat(fill, bytes).ToArray());
        if (lastWriteUtc is not null) System.IO.File.SetLastWriteTimeUtc(full, lastWriteUtc.Value);
        return full;
    }
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FakeHardwareInfoProvider : IHardwareInfoProvider
{
    public SystemInfo Info { get; set; } = new()
    {
        OsName = "Windows 11 Pro", OsDisplayVersion = "23H2", OsBuild = 22631, ComputerName = "GEHEIMER-PC", CpuName = "Test-CPU 9000", CpuCores = 8, CpuLogicalProcessors = 16,
        MemoryTotalBytes = 16L << 30,
        Gpus = new[] { new GpuInfo { Name = "Test-GPU", Vendor = "NVIDIA", IsIntegrated = false, DedicatedMemoryBytes = 8L << 30 } },
        Volumes = new[] { new VolumeInfo { Letter = "C:", TotalBytes = 500L << 30, FreeBytes = 200L << 30, MediaType = DriveMediaType.Nvme, IsSystem = true } },
        NetworkAdapters = new[] { new NetworkAdapterInfo { Id = "{1}", Name = "Ethernet", Type = "Ethernet", IsUp = true, IPv4 = new[] { "192.168.178.23" }, MacAddress = "AA-BB-CC-DD-EE-FF" } },
    };
    public bool IsProcessElevated => false;
    public Task<SystemInfo> GetSystemInfoAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => Task.FromResult(Info);
}

internal sealed class FakeSecretStore : NexusOptimizer.Core.Settings.ISecretStore
{
    private readonly Dictionary<string, string> _values = new();
    public void Set(string name, string? secret) { if (secret is null) _values.Remove(name); else _values[name] = secret; }
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public bool Has(string name) => _values.ContainsKey(name);
}
