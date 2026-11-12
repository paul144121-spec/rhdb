using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Hardware;

public enum DriveMediaType
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
    Removable,
    Virtual,
}

public enum HealthState
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy,
}

public sealed record GpuInfo
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string? DriverVersion { get; init; }
    public DateTime? DriverDate { get; init; }
    public long? DedicatedMemoryBytes { get; init; }
    public bool IsIntegrated { get; init; }
    public string? Resolution { get; init; }
    public int? RefreshRateHz { get; init; }
    /// <summary>Offizielle Treiberseite des Herstellers.</summary>
    public string? DriverUrl => Vendor switch
    {
        "NVIDIA" => "https://www.nvidia.com/de-de/drivers/",
        "AMD" => "https://www.amd.com/de/support/download/drivers.html",
        "Intel" => "https://www.intel.de/content/www/de/de/download-center/home.html",
        _ => null,
    };
}

public sealed record MemoryModuleInfo(string Slot, long CapacityBytes, int? SpeedMhz, string? Manufacturer, string? PartNumber);

public sealed record PhysicalDriveInfo
{
    public string Model { get; init; } = "";
    public DriveMediaType MediaType { get; init; }
    public string? BusType { get; init; }
    public long SizeBytes { get; init; }
    public HealthState Health { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? SerialHint { get; init; }
    public int? TemperatureCelsius { get; init; }
    public int? WearPercent { get; init; }
    public long? PowerOnHours { get; init; }
    public long? ReadErrors { get; init; }
    public int DeviceNumber { get; init; } = -1;
}

public sealed record VolumeInfo
{
    public string Letter { get; init; } = "";
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public DriveMediaType MediaType { get; init; }
    public bool IsSystem { get; init; }
    public bool? TrimSupported { get; init; }
    public double FreePercent => TotalBytes == 0 ? 0 : 100.0 * FreeBytes / TotalBytes;
}

public sealed record NetworkAdapterInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public bool IsUp { get; init; }
    public long? SpeedBitsPerSecond { get; init; }
    public IReadOnlyList<string> IPv4 { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IPv6 { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Gateways { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();
    public string? MacAddress { get; init; }
    public bool IsWireless { get; init; }
    public bool? DhcpEnabled { get; init; }
}

public sealed record BatteryInfo
{
    public int? ChargePercent { get; init; }
    public bool? OnAcPower { get; init; }
    public long? DesignCapacityMilliwattHours { get; init; }
    public long? FullChargeCapacityMilliwattHours { get; init; }
    public int? CycleCount { get; init; }
    public string? Chemistry { get; init; }
    public double? HealthPercent => DesignCapacityMilliwattHours is > 0 && FullChargeCapacityMilliwattHours is not null
        ? Math.Min(100, 100.0 * FullChargeCapacityMilliwattHours.Value / DesignCapacityMilliwattHours.Value) : null;
}

public sealed record SecurityFeatureInfo(string Name, bool? Enabled, string Detail);

/// <summary>Statische Systeminformationen. Jede nicht ermittelbare Angabe bleibt null und wird in der Oberfläche als „nicht verfügbar“ ausgewiesen.</summary>
public sealed record SystemInfo
{
    public string OsName { get; init; } = "";
    public string? OsDisplayVersion { get; init; }
    public int OsBuild { get; init; }
    public int? OsRevision { get; init; }
    public string? OsEdition { get; init; }
    public string? ComputerName { get; init; }
    public string? SystemManufacturer { get; init; }
    public string? SystemModel { get; init; }
    public string CpuName { get; init; } = "";
    public int? CpuCores { get; init; }
    public int CpuLogicalProcessors { get; init; } = Environment.ProcessorCount;
    public int? CpuMaxClockMhz { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public string? MotherboardManufacturer { get; init; }
    public string? MotherboardModel { get; init; }
    public string? BiosVersion { get; init; }
    public DateTime? BiosDate { get; init; }
    public long MemoryTotalBytes { get; init; }
    public IReadOnlyList<MemoryModuleInfo> MemoryModules { get; init; } = Array.Empty<MemoryModuleInfo>();
    public IReadOnlyList<PhysicalDriveInfo> Drives { get; init; } = Array.Empty<PhysicalDriveInfo>();
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = Array.Empty<VolumeInfo>();
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = Array.Empty<NetworkAdapterInfo>();
    public string? ActivePowerPlan { get; init; }
    public BatteryInfo? Battery { get; init; }
    public bool? IsActivated { get; init; }
    public string? BootMode { get; init; }
    public bool? SecureBootEnabled { get; init; }
    public bool? TpmPresent { get; init; }
    public string? TpmVersion { get; init; }
    public bool? VirtualizationEnabledInFirmware { get; init; }
    public bool? HypervisorPresent { get; init; }
    public bool IsVirtualMachine { get; init; }
    public IReadOnlyList<SecurityFeatureInfo> SecurityFeatures { get; init; } = Array.Empty<SecurityFeatureInfo>();
    public bool PendingReboot { get; init; }
    public IReadOnlyList<string> PendingRebootReasons { get; init; } = Array.Empty<string>();
    public PageFileInfo? PageFile { get; init; }
    /// <summary>Erläuterungen, warum einzelne Angaben fehlen (Schlüssel = Eigenschaftsname).</summary>
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>();

    public int? MemorySpeedMhz => MemoryModules.Select(m => m.SpeedMhz).Where(s => s is > 0).DefaultIfEmpty(null).Min();
    public VolumeInfo? SystemVolume => Volumes.FirstOrDefault(v => v.IsSystem) ?? Volumes.FirstOrDefault();
    public bool HasDedicatedGpu => Gpus.Any(g => !g.IsIntegrated);

    public SystemContext ToContext(bool isElevated, bool onBattery, bool thermalThrottling) => new()
    {
        WindowsBuild = OsBuild,
        WindowsEdition = OsEdition ?? "",
        IsElevated = isElevated,
        HasBattery = Battery is not null,
        OnBatteryPower = onBattery,
        IsVirtualMachine = IsVirtualMachine,
        SystemDriveIsSsd = SystemVolume?.MediaType is DriveMediaType.Ssd or DriveMediaType.Nvme or DriveMediaType.Unknown or null,
        HasDedicatedGpu = HasDedicatedGpu,
        GpuCount = Math.Max(1, Gpus.Count),
        TotalMemoryBytes = MemoryTotalBytes,
        LogicalProcessors = CpuLogicalProcessors,
        ThermalThrottlingObserved = thermalThrottling,
    };
}

public sealed record PageFileInfo(bool AutomaticallyManaged, IReadOnlyList<string> Files, long? AllocatedBytes, long? CurrentUsageBytes, long? PeakUsageBytes);

public interface IHardwareInfoProvider
{
    /// <summary>Liest die Systeminformationen. Langsame Abfragen (WMI) – nie auf dem UI-Thread aufrufen.</summary>
    Task<SystemInfo> GetSystemInfoAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    bool IsProcessElevated { get; }
}

public sealed record ProcessSnapshot
{
    public int ProcessId { get; init; }
    public int ParentProcessId { get; init; }
    public string Name { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public double? DiskBytesPerSecond { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public DateTime? StartTime { get; init; }
    public SystemAccess.ProcessPriorityLevel? Priority { get; init; }
    public bool IsProtected { get; init; }
    public bool IsAccessDenied { get; init; }
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();
    public int SessionId { get; init; }
}

public interface IProcessMonitor
{
    /// <summary>Liefert alle Prozesse; CPU- und E/A-Raten beziehen sich auf den Zeitraum seit dem letzten Aufruf.</summary>
    IReadOnlyList<ProcessSnapshot> GetProcesses();
    /// <summary>Beendet einen Prozess. Geschützte Prozesse werden abgelehnt.</summary>
    OperationResult Terminate(int processId);
    /// <summary>Nur die Namen laufender Prozesse – günstig für Spiel- und Regelüberwachung.</summary>
    IReadOnlyList<(int ProcessId, string Name)> GetRunningProcessNames();
}
