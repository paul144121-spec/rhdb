using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Windows.Native;

namespace NexusOptimizer.Windows.Hardware;

/// <summary>
/// Liest Systeminformationen aus WMI/CIM, der Registrierung und dokumentierten Win32-Funktionen.
/// Jede Teilabfrage ist einzeln abgesichert: Schlägt eine fehl, bleibt nur dieser Wert leer und wird mit Begründung vermerkt.
/// </summary>
public sealed class WindowsHardwareInfoProvider : IHardwareInfoProvider
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(12);
    private readonly IPowerPolicy? _power;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemInfo? _cached;

    public WindowsHardwareInfoProvider(IPowerPolicy? power = null, ILogger<WindowsHardwareInfoProvider>? logger = null)
    {
        _power = power;
        _logger = logger ?? NullLogger<WindowsHardwareInfoProvider>.Instance;
    }

    public bool IsProcessElevated => ProcessElevation.IsElevated;

    public async Task<SystemInfo> GetSystemInfoAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cached is not null) return _cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is not null) return _cached;
            _cached = await Task.Run(Read, cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Führt eine WQL-Abfrage aus; Fehler werden vermerkt statt weitergereicht.</summary>
    private void Query(string scope, string wql, Dictionary<string, string> notes, string noteKey, Action<ManagementBaseObject> handle)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true, Rewindable = false });
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item) handle(item);
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or TimeoutException or InvalidCastException or PlatformNotSupportedException or TypeInitializationException)
        {
            _logger.LogDebug(ex, "WMI-Abfrage fehlgeschlagen: {Query}", wql);
            notes[noteKey] = ex is UnauthorizedAccessException or ManagementException { ErrorCode: ManagementStatus.AccessDenied }
                ? "Für diese Angabe sind Administratorrechte erforderlich."
                : "Windows stellt diese Angabe auf diesem System nicht bereit.";
        }
    }

    private static string? Text(ManagementBaseObject o, string name)
    {
        try { return o[name]?.ToString()?.Trim() is { Length: > 0 } s ? s : null; }
        catch (ManagementException) { return null; }
    }

    private static long? Number(ManagementBaseObject o, string name)
    {
        try { return o[name] is null ? null : Convert.ToInt64(o[name]); }
        catch (Exception ex) when (ex is ManagementException or FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static bool? Flag(ManagementBaseObject o, string name)
    {
        try { return o[name] is bool b ? b : null; }
        catch (ManagementException) { return null; }
    }

    private static DateTime? Date(ManagementBaseObject o, string name)
    {
        try { return Text(o, name) is { } s ? ManagementDateTimeConverter.ToDateTime(s) : null; }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException) { return null; }
    }

    private static T? Registry<T>(RegistryHive hive, string path, string name) where T : class
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as T;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static int? RegistryInt(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) is int i ? i : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private SystemInfo Read()
    {
        var notes = new Dictionary<string, string>();
        const string cimv2 = @"root\CIMV2";
        const string currentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        // ── Betriebssystem ──
        var build = Environment.OSVersion.Version.Build;
        string osName = build >= 22000 ? "Windows 11" : "Windows 10";
        Query(cimv2, "SELECT Caption FROM Win32_OperatingSystem", notes, nameof(SystemInfo.OsName), o => osName = Text(o, "Caption")?.Replace("Microsoft ", "") ?? osName);
        var displayVersion = Registry<string>(RegistryHive.LocalMachine, currentVersion, "DisplayVersion") ?? Registry<string>(RegistryHive.LocalMachine, currentVersion, "ReleaseId");
        var edition = Registry<string>(RegistryHive.LocalMachine, currentVersion, "EditionID");
        var revision = RegistryInt(RegistryHive.LocalMachine, currentVersion, "UBR");

        // ── Computer, Prozessor ──
        string? manufacturer = null, model = null;
        bool? hypervisor = null, autoPageFile = null;
        long memoryTotal = 0;
        var isMobile = false;
        Query(cimv2, "SELECT Manufacturer, Model, HypervisorPresent, TotalPhysicalMemory, PCSystemType, AutomaticManagedPagefile FROM Win32_ComputerSystem", notes, nameof(SystemInfo.SystemModel), o =>
        {
            manufacturer = Text(o, "Manufacturer");
            model = Text(o, "Model");
            hypervisor = Flag(o, "HypervisorPresent");
            memoryTotal = Number(o, "TotalPhysicalMemory") ?? 0;
            isMobile = Number(o, "PCSystemType") == 2;
            autoPageFile = Flag(o, "AutomaticManagedPagefile");
        });

        string cpuName = "";
        int? cores = null, maxMhz = null;
        var logical = Environment.ProcessorCount;
        bool? virtualization = null;
        Query(cimv2, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, VirtualizationFirmwareEnabled FROM Win32_Processor", notes, nameof(SystemInfo.CpuName), o =>
        {
            cpuName = Regex.Replace(Text(o, "Name") ?? cpuName, @"\s+", " ");
            cores = (cores ?? 0) + (int)(Number(o, "NumberOfCores") ?? 0);
            maxMhz = (int?)Number(o, "MaxClockSpeed") ?? maxMhz;
            virtualization ??= Flag(o, "VirtualizationFirmwareEnabled");
        });
        if (cores == 0) cores = null;
        if (string.IsNullOrEmpty(cpuName)) cpuName = Registry<string>(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "Unbekannter Prozessor";
        maxMhz ??= RegistryInt(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz");

        // ── Grafik ──
        var gpus = new List<GpuInfo>();
        Query(cimv2, "SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController", notes, nameof(SystemInfo.Gpus), o =>
        {
            var name = Text(o, "Name") ?? "Unbekannte Grafikeinheit";
            var vendor = NormalizeVendor(Text(o, "AdapterCompatibility"), name);
            var width = Number(o, "CurrentHorizontalResolution");
            var height = Number(o, "CurrentVerticalResolution");
            gpus.Add(new GpuInfo
            {
                Name = name,
                Vendor = vendor,
                DriverVersion = Text(o, "DriverVersion"),
                DriverDate = Date(o, "DriverDate"),
                // AdapterRAM ist ein 32-Bit-Wert und läuft bei mehr als 4 GB über – die Registrierung liefert den korrekten 64-Bit-Wert.
                DedicatedMemoryBytes = ReadVideoMemory(name) ?? (Number(o, "AdapterRAM") is > 0 and < uint.MaxValue - 1024 * 1024 ? Number(o, "AdapterRAM") : null),
                IsIntegrated = IsIntegratedGpu(vendor, name),
                Resolution = width is > 0 && height is > 0 ? $"{width} × {height}" : null,
                RefreshRateHz = (int?)Number(o, "CurrentRefreshRate"),
            });
        });

        // ── Mainboard, BIOS, RAM ──
        string? boardVendor = null, boardModel = null, biosVersion = null;
        DateTime? biosDate = null;
        Query(cimv2, "SELECT Manufacturer, Product FROM Win32_BaseBoard", notes, nameof(SystemInfo.MotherboardModel), o => { boardVendor = Text(o, "Manufacturer"); boardModel = Text(o, "Product"); });
        Query(cimv2, "SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", notes, nameof(SystemInfo.BiosVersion), o => { biosVersion = Text(o, "SMBIOSBIOSVersion"); biosDate = Date(o, "ReleaseDate"); });

        var modules = new List<MemoryModuleInfo>();
        Query(cimv2, "SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory", notes, nameof(SystemInfo.MemoryModules), o =>
        {
            var speed = Number(o, "ConfiguredClockSpeed") is > 0 and var configured ? configured : Number(o, "Speed");
            modules.Add(new MemoryModuleInfo(Text(o, "DeviceLocator") ?? "?", Number(o, "Capacity") ?? 0, speed is > 0 ? (int)speed : null, Text(o, "Manufacturer"), Text(o, "PartNumber")));
        });
        if (memoryTotal == 0) memoryTotal = modules.Sum(m => m.CapacityBytes);
        if (memoryTotal == 0)
        {
            var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref status)) memoryTotal = (long)status.ullTotalPhys;
        }

        // ── Datenträger ──
        const string storage = @"root\Microsoft\Windows\Storage";
        var drives = new List<PhysicalDriveInfo>();
        Query(storage, "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, FirmwareVersion FROM MSFT_PhysicalDisk", notes, nameof(SystemInfo.Drives), o =>
        {
            var bus = (int)(Number(o, "BusType") ?? 0);
            var media = (int)(Number(o, "MediaType") ?? 0);
            drives.Add(new PhysicalDriveInfo
            {
                DeviceNumber = int.TryParse(Text(o, "DeviceId"), out var id) ? id : -1,
                Model = Text(o, "FriendlyName") ?? "Unbekannter Datenträger",
                MediaType = bus == 17 ? DriveMediaType.Nvme : bus is 14 or 15 ? DriveMediaType.Virtual : bus is 7 or 12 or 13 && media != 4 ? DriveMediaType.Removable
                    : media switch { 3 => DriveMediaType.Hdd, 4 => DriveMediaType.Ssd, _ => DriveMediaType.Unknown },
                BusType = BusName(bus),
                SizeBytes = Number(o, "Size") ?? 0,
                Health = (Number(o, "HealthStatus")) switch { 0 => HealthState.Healthy, 1 => HealthState.Warning, 2 => HealthState.Unhealthy, _ => HealthState.Unknown },
                FirmwareVersion = Text(o, "FirmwareVersion"),
            });
        });

        // Zuverlässigkeitszähler (Temperatur, Verschleiß) erfordern in der Regel Administratorrechte.
        Query(storage, "SELECT DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsTotal FROM MSFT_StorageReliabilityCounter", notes, "DriveReliability", o =>
        {
            if (!int.TryParse(Text(o, "DeviceId"), out var id)) return;
            var index = drives.FindIndex(d => d.DeviceNumber == id);
            if (index < 0) return;
            drives[index] = drives[index] with
            {
                TemperatureCelsius = Number(o, "Temperature") is > 0 and < 150 and var t ? (int)t : null,
                WearPercent = Number(o, "Wear") is >= 0 and <= 100 and var w ? (int)w : null,
                PowerOnHours = Number(o, "PowerOnHours"),
                ReadErrors = Number(o, "ReadErrorsTotal"),
            };
        });

        var letterToDisk = new Dictionary<char, int>();
        Query(storage, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition", notes, "Partitions", o =>
        {
            if (o["DriveLetter"] is char letter && letter != '\0' && Number(o, "DiskNumber") is { } disk) letterToDisk[char.ToUpperInvariant(letter)] = (int)disk;
        });

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var letter = drive.Name.TrimEnd('\\');
                var media = drive.DriveType == DriveType.Removable ? DriveMediaType.Removable
                    : letterToDisk.TryGetValue(char.ToUpperInvariant(letter[0]), out var disk) ? drives.FirstOrDefault(d => d.DeviceNumber == disk)?.MediaType ?? DriveMediaType.Unknown : DriveMediaType.Unknown;
                volumes.Add(new VolumeInfo
                {
                    Letter = letter, Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel, FileSystem = drive.DriveFormat,
                    TotalBytes = drive.TotalSize, FreeBytes = drive.AvailableFreeSpace, MediaType = media, IsSystem = letter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // ── Akku ──
        BatteryInfo? battery = null;
        if (NativeMethods.GetSystemPowerStatus(out var power) && (power.BatteryFlag & 128) == 0 && power.BatteryFlag != 255)
        {
            long? design = null, full = null;
            int? cycles = null;
            var scratch = new Dictionary<string, string>();
            Query(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData", scratch, "b", o => design = Number(o, "DesignedCapacity"));
            Query(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", scratch, "b", o => full = Number(o, "FullChargedCapacity"));
            Query(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount", scratch, "b", o => cycles = Number(o, "CycleCount") is > 0 and var c ? (int)c : null);
            battery = new BatteryInfo
            {
                ChargePercent = power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null,
                OnAcPower = power.ACLineStatus == 255 ? null : power.ACLineStatus == 1,
                DesignCapacityMilliwattHours = design is > 0 ? design : null,
                FullChargeCapacityMilliwattHours = full is > 0 ? full : null,
                CycleCount = cycles,
            };
        }

        // ── Firmware und Sicherheit ──
        string? bootMode = NativeMethods.GetFirmwareType(out var firmware) ? firmware switch { 1 => "Legacy-BIOS", 2 => "UEFI", _ => null } : null;
        var secureBootRaw = RegistryInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        bool? secureBoot = secureBootRaw is null ? (bootMode == "Legacy-BIOS" ? false : null) : secureBootRaw == 1;

        bool? tpmPresent = null;
        string? tpmVersion = null;
        try
        {
            var status = NativeMethods.Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<NativeMethods.TPM_DEVICE_INFO>(), out var tpm);
            if (status == 0)
            {
                tpmPresent = true;
                tpmVersion = tpm.tpmVersion switch { 1 => "1.2", 2 => "2.0", _ => null };
            }
            else if (status == 0x8028400F) tpmPresent = false; // TBS_E_TPM_NOT_FOUND
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            notes[nameof(SystemInfo.TpmPresent)] = "Die TPM-Basisdienste sind auf diesem System nicht verfügbar.";
        }

        var security = new List<SecurityFeatureInfo>();
        Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard", notes, "DeviceGuard", o =>
        {
            var vbs = Number(o, "VirtualizationBasedSecurityStatus");
            security.Add(new SecurityFeatureInfo("Virtualisierungsbasierte Sicherheit (VBS)", vbs is null ? null : vbs == 2, vbs switch { 2 => "Aktiv", 1 => "Aktiviert, aber nicht aktiv", 0 => "Aus", _ => "Unbekannt" }));
            var running = o["SecurityServicesRunning"] as uint[] ?? Array.Empty<uint>();
            security.Add(new SecurityFeatureInfo("Speicherintegrität (HVCI)", running.Contains(2u), running.Contains(2u) ? "Aktiv" : "Nicht aktiv"));
        });
        if (security.Count == 0)
        {
            var hvci = RegistryInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
            security.Add(new SecurityFeatureInfo("Speicherintegrität (HVCI)", hvci is null ? null : hvci == 1, hvci switch { 1 => "Laut Konfiguration aktiviert", 0 => "Laut Konfiguration deaktiviert", _ => "Nicht konfiguriert / nicht ermittelbar" }));
        }

        var antivirus = new List<string>();
        Query(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct", notes, "Antivirus", o => { if (Text(o, "displayName") is { } name) antivirus.Add(name); });
        if (antivirus.Count > 0) security.Add(new SecurityFeatureInfo("Virenschutz", true, string.Join(", ", antivirus.Distinct())));
        var firewall = RegistryInt(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile", "EnableFirewall");
        if (firewall is not null) security.Add(new SecurityFeatureInfo("Windows-Firewall (privates Profil)", firewall == 1, firewall == 1 ? "Ein" : "Aus"));
        var uac = RegistryInt(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        if (uac is not null) security.Add(new SecurityFeatureInfo("Benutzerkontensteuerung (UAC)", uac == 1, uac == 1 ? "Ein" : "Aus"));

        // ── Aktivierung (kann mehrere Sekunden dauern) ──
        bool? activated = null;
        Query(cimv2, "SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL", notes, nameof(SystemInfo.IsActivated),
            o => activated = (activated ?? false) || Number(o, "LicenseStatus") == 1);

        // ── Auslagerungsdatei ──
        var pageFiles = new List<string>();
        long? pageAllocated = null, pageUsage = null, pagePeak = null;
        Query(cimv2, "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage", notes, nameof(SystemInfo.PageFile), o =>
        {
            if (Text(o, "Name") is { } name) pageFiles.Add(name);
            pageAllocated = (pageAllocated ?? 0) + (Number(o, "AllocatedBaseSize") ?? 0) * 1024 * 1024;
            pageUsage = (pageUsage ?? 0) + (Number(o, "CurrentUsage") ?? 0) * 1024 * 1024;
            pagePeak = (pagePeak ?? 0) + (Number(o, "PeakUsage") ?? 0) * 1024 * 1024;
        });

        var (pendingReboot, rebootReasons) = PendingRebootDetector.Detect();
        string? activePlan = null;
        try { activePlan = _power?.GetPlans().FirstOrDefault(p => p.IsActive)?.Name; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException) { notes[nameof(SystemInfo.ActivePowerPlan)] = "Der Energieplan konnte nicht ermittelt werden."; }

        var description = $"{manufacturer} {model}";
        var isVirtual = new[] { "Virtual", "VMware", "VirtualBox", "KVM", "QEMU", "Xen", "Parallels", "Bochs" }.Any(v => description.Contains(v, StringComparison.OrdinalIgnoreCase));

        return new SystemInfo
        {
            OsName = osName, OsDisplayVersion = displayVersion, OsBuild = build, OsRevision = revision, OsEdition = edition,
            ComputerName = Environment.MachineName, SystemManufacturer = manufacturer, SystemModel = model,
            CpuName = cpuName, CpuCores = cores, CpuLogicalProcessors = logical, CpuMaxClockMhz = maxMhz,
            Gpus = gpus, MotherboardManufacturer = boardVendor, MotherboardModel = boardModel, BiosVersion = biosVersion, BiosDate = biosDate,
            MemoryTotalBytes = memoryTotal, MemoryModules = modules, Drives = drives, Volumes = volumes, NetworkAdapters = ReadNetworkAdapters(),
            ActivePowerPlan = activePlan, Battery = battery ?? (isMobile ? new BatteryInfo() : null), IsActivated = activated,
            BootMode = bootMode, SecureBootEnabled = secureBoot, TpmPresent = tpmPresent, TpmVersion = tpmVersion,
            VirtualizationEnabledInFirmware = virtualization, HypervisorPresent = hypervisor, IsVirtualMachine = isVirtual,
            SecurityFeatures = security, PendingReboot = pendingReboot, PendingRebootReasons = rebootReasons,
            PageFile = autoPageFile is null && pageFiles.Count == 0 ? null : new PageFileInfo(autoPageFile ?? false, pageFiles, pageAllocated, pageUsage, pagePeak),
            Notes = notes,
        };
    }

    private static string NormalizeVendor(string? compatibility, string name)
    {
        var text = $"{compatibility} {name}";
        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) || text.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase) || text.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (text.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
        return compatibility ?? "Unbekannt";
    }

    /// <summary>Heuristik anhand der Produktbezeichnung; bei unbekannten Herstellern gilt die Einheit als integriert bzw. virtuell.</summary>
    internal static bool IsIntegratedGpu(string vendor, string name) => vendor switch
    {
        "NVIDIA" => false,
        "Intel" => !Regex.IsMatch(name, @"Arc(\(TM\))?\s+(Pro\s+)?[AB]\d{3}", RegexOptions.IgnoreCase),
        "AMD" => Regex.IsMatch(name, @"Radeon(\(TM\))?\s+(Graphics|Vega|R\d\s+Graphics|\d{3}M\b)|Radeon\(TM\)\s+\d{3}M|Vega \d+ Graphics", RegexOptions.IgnoreCase),
        _ => true,
    };

    private static string? BusName(int bus) => bus switch
    {
        3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 or 15 => "Virtuell", 17 => "NVMe", 0 => null, _ => $"Bus-Typ {bus}",
    };

    /// <summary>Liest die Größe des Grafikspeichers aus dem Treiberschlüssel der Anzeigeadapter-Klasse.</summary>
    private static long? ReadVideoMemory(string adapterName)
    {
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(classKey);
            if (root is null) return null;
            foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") as string != adapterName) continue;
                return key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l when l > 0 => l,
                    byte[] { Length: 8 } bytes => BitConverter.ToInt64(bytes),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i when i > 0 => (uint)i,
                        byte[] { Length: 4 } bytes => BitConverter.ToUInt32(bytes),
                        _ => null,
                    },
                };
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return null;
    }

    private static List<NetworkAdapterInfo> ReadNetworkAdapters()
    {
        var result = new List<NetworkAdapterInfo>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var properties = adapter.GetIPProperties();
                bool? dhcp = null;
                try { dhcp = properties.GetIPv4Properties()?.IsDhcpEnabled; } catch (NetworkInformationException) { }
                result.Add(new NetworkAdapterInfo
                {
                    Id = adapter.Id,
                    Name = adapter.Name,
                    Description = adapter.Description,
                    Type = adapter.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Ethernet",
                        NetworkInterfaceType.Wireless80211 => "WLAN",
                        NetworkInterfaceType.Ppp => "Einwahl/VPN",
                        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobilfunk",
                        var other => other.ToString(),
                    },
                    IsUp = adapter.OperationalStatus == OperationalStatus.Up,
                    SpeedBitsPerSecond = adapter.Speed > 0 ? adapter.Speed : null,
                    IPv4 = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList(),
                    IPv6 = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList(),
                    Gateways = properties.GatewayAddresses.Select(g => g.Address.ToString()).Where(g => g != "0.0.0.0").ToList(),
                    DnsServers = properties.DnsAddresses.Select(d => d.ToString()).ToList(),
                    MacAddress = adapter.GetPhysicalAddress().GetAddressBytes() is { Length: 6 } mac ? string.Join("-", mac.Select(b => b.ToString("X2"))) : null,
                    IsWireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    DhcpEnabled = dhcp,
                });
            }
        }
        catch (NetworkInformationException) { }
        return result.OrderByDescending(a => a.IsUp).ThenBy(a => a.Name).ToList();
    }
}

public static class ProcessElevation
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, NativeMethods.TokenQuery, out var token)) return false;
            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out var elevation, sizeof(uint), out _) && elevation != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        catch (Exception) { return false; }
    });

    public static bool IsElevated => Elevated.Value;
}

/// <summary>Erkennt anhand der dokumentierten Registrierungsmerkmale, ob Windows auf einen Neustart wartet. Rein lesend.</summary>
public static class PendingRebootDetector
{
    public static (bool Pending, IReadOnlyList<string> Reasons) Detect()
    {
        var reasons = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (cbs is not null) reasons.Add("Die Komponentenwartung (CBS) wartet auf einen Neustart.");
            using (var update = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (update is not null) reasons.Add("Windows Update wartet auf einen Neustart.");
            using (var session = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) reasons.Add("Es stehen Dateiersetzungen aus, die erst beim Neustart ausgeführt werden.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return (reasons.Count > 0, reasons);
    }
}
