using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Windows.Native;

namespace NexusOptimizer.Windows.Monitoring;

/// <summary>
/// Erfasst Live-Messwerte über dokumentierte Windows-Schnittstellen: PDH-Leistungsindikatoren, GlobalMemoryStatusEx,
/// GetPerformanceInfo, GetSystemPowerStatus und CallNtPowerInformation. Zähler, die ein System nicht bereitstellt,
/// bleiben null und werden mit Begründung als „nicht verfügbar“ ausgewiesen.
/// </summary>
public sealed class WindowsMetricsCollector : IMetricsCollector
{
    private readonly PdhQuery _pdh = new();
    private readonly Dictionary<MetricId, string> _unavailable = new();
    private readonly IntPtr _cpuTotal, _cpuPerCore, _cpuPerformance, _cpuLimit, _dpc, _interrupt, _queue, _thermal;
    private readonly IntPtr _gpuEngine, _gpuDedicated, _gpuShared;
    private readonly IntPtr _standbyNormal, _standbyReserve, _standbyCore, _modified, _cache;
    private readonly IntPtr _diskIdle, _diskRead, _diskWrite, _diskQueue, _diskLatency;
    private readonly IntPtr _netReceived, _netSent;
    private readonly bool _cpuUtilityCounter;
    private NvmlSensors? _nvml;
    private bool _nvmlRequested;
    private double? _lastLatency;
    private DateTime _lastLatencyProbe = DateTime.MinValue;
    private int _latencyProbeRunning;
    private bool _disposed;

    public WindowsMetricsCollector()
    {
        // „% Processor Utility“ entspricht der Anzeige im Task-Manager; ältere Systeme kennen nur „% Processor Time“.
        _cpuTotal = _pdh.TryAdd(@"\Processor Information(_Total)\% Processor Utility");
        _cpuUtilityCounter = _cpuTotal != IntPtr.Zero;
        if (!_cpuUtilityCounter) _cpuTotal = _pdh.TryAdd(@"\Processor(_Total)\% Processor Time");
        _cpuPerCore = _pdh.TryAdd(_cpuUtilityCounter ? @"\Processor Information(*)\% Processor Utility" : @"\Processor Information(*)\% Processor Time");
        _cpuPerformance = _pdh.TryAdd(@"\Processor Information(_Total)\% Processor Performance");
        _cpuLimit = _pdh.TryAdd(@"\Processor Information(_Total)\% Performance Limit");
        _dpc = _pdh.TryAdd(@"\Processor Information(_Total)\% DPC Time");
        _interrupt = _pdh.TryAdd(@"\Processor Information(_Total)\% Interrupt Time");
        _queue = _pdh.TryAdd(@"\System\Processor Queue Length");
        _thermal = _pdh.TryAdd(@"\Thermal Zone Information(*)\Temperature");

        _gpuEngine = _pdh.TryAdd(@"\GPU Engine(*)\Utilization Percentage");
        _gpuDedicated = _pdh.TryAdd(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _gpuShared = _pdh.TryAdd(@"\GPU Adapter Memory(*)\Shared Usage");

        _standbyNormal = _pdh.TryAdd(@"\Memory\Standby Cache Normal Priority Bytes");
        _standbyReserve = _pdh.TryAdd(@"\Memory\Standby Cache Reserve Bytes");
        _standbyCore = _pdh.TryAdd(@"\Memory\Standby Cache Core Bytes");
        _modified = _pdh.TryAdd(@"\Memory\Modified Page List Bytes");
        _cache = _pdh.TryAdd(@"\Memory\Cache Bytes");

        _diskIdle = _pdh.TryAdd(@"\PhysicalDisk(_Total)\% Idle Time");
        _diskRead = _pdh.TryAdd(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _diskWrite = _pdh.TryAdd(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _diskQueue = _pdh.TryAdd(@"\PhysicalDisk(_Total)\Current Disk Queue Length");
        _diskLatency = _pdh.TryAdd(@"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer");

        _netReceived = _pdh.TryAdd(@"\Network Interface(*)\Bytes Received/sec");
        _netSent = _pdh.TryAdd(@"\Network Interface(*)\Bytes Sent/sec");

        void Note(IntPtr counter, MetricId metric, string reason) { if (counter == IntPtr.Zero) _unavailable[metric] = reason; }
        Note(_cpuTotal, MetricId.CpuTotal, "Windows stellt den Prozessor-Leistungsindikator nicht bereit.");
        Note(_cpuPerformance, MetricId.CpuFrequency, "Der Leistungsindikator „% Processor Performance“ ist auf diesem System nicht vorhanden.");
        Note(_cpuLimit, MetricId.CpuPerformanceLimit, "Der Leistungsindikator „% Performance Limit“ ist auf diesem System nicht vorhanden.");
        Note(_thermal, MetricId.ThermalZone, "Dieses System meldet keine ACPI-Thermalzone. Die CPU-Kerntemperatur ist ohne herstellerspezifischen Sensorzugriff nicht auslesbar.");
        Note(_gpuEngine, MetricId.GpuUsage, "GPU-Leistungsindikatoren sind erst ab Windows 10 Version 1709 mit WDDM-2.x-Treiber verfügbar.");
        Note(_gpuDedicated, MetricId.GpuMemoryUsed, "GPU-Speicherindikatoren sind auf diesem System nicht verfügbar.");
        Note(_gpuDedicated, MetricId.GpuMemoryPercent, "GPU-Speicherindikatoren sind auf diesem System nicht verfügbar.");
        Note(_diskIdle, MetricId.DiskActive, "Datenträger-Leistungsindikatoren sind deaktiviert oder nicht vorhanden.");
        Note(_diskRead, MetricId.DiskRead, "Datenträger-Leistungsindikatoren sind deaktiviert oder nicht vorhanden.");
        Note(_diskWrite, MetricId.DiskWrite, "Datenträger-Leistungsindikatoren sind deaktiviert oder nicht vorhanden.");
        Note(_diskLatency, MetricId.DiskLatency, "Datenträger-Leistungsindikatoren sind deaktiviert oder nicht vorhanden.");
        Note(_netReceived, MetricId.NetworkDown, "Es wurde kein Netzwerkadapter mit Leistungsindikatoren gefunden.");
        Note(_netSent, MetricId.NetworkUp, "Es wurde kein Netzwerkadapter mit Leistungsindikatoren gefunden.");
        _unavailable[MetricId.GpuTemperature] = "Die GPU-Temperatur wird nur für NVIDIA-Grafikkarten über die Treiberschnittstelle ausgelesen.";
        _unavailable[MetricId.Latency] = "Die Latenzmessung ist ausgeschaltet. Sie kann in den Einstellungen mit von Ihnen freigegebenen Zielen aktiviert werden.";

        _pdh.Collect(); // erster Abruf als Bezugspunkt für Ratenzähler
    }

    public IReadOnlyDictionary<MetricId, string> UnavailableReasons => _unavailable;

    /// <summary>Nennfrequenz des Prozessors (MHz) aus der Hardwareerkennung; Grundlage für die Taktberechnung.</summary>
    public double? CpuBaseFrequencyMhz { get; set; }
    /// <summary>Gesamtgröße des dedizierten Grafikspeichers aus der Hardwareerkennung.</summary>
    public double? GpuDedicatedTotalBytes { get; set; }
    public bool NvidiaSensorsEnabled { get; set; } = true;
    /// <summary>Vom Benutzer freigegebene Ziele der Latenzmessung; leer = keine Messung.</summary>
    public IReadOnlyList<string> LatencyEndpoints { get; set; } = Array.Empty<string>();

    public SystemMetrics Collect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pdh.Collect();

        var memory = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        var hasMemory = NativeMethods.GlobalMemoryStatusEx(ref memory);
        var hasPerformance = NativeMethods.GetPerformanceInfo(out var performance, (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>());

        var perCore = _pdh.ReadArray(_cpuPerCore, allowAbove100: true)
            .Where(i => !i.Instance.Contains("_Total", StringComparison.OrdinalIgnoreCase))
            .Select(i => (Key: ParseCoreKey(i.Instance), Value: Math.Clamp(i.Value, 0, 100)))
            .OrderBy(i => i.Key.Group).ThenBy(i => i.Key.Index)
            .Select(i => i.Value).ToArray();

        var cpuTotal = _pdh.Read(_cpuTotal, allowAbove100: true);
        var performancePercent = _pdh.Read(_cpuPerformance, allowAbove100: true);
        var thermalKelvin = _pdh.ReadArray(_thermal).Select(i => i.Value).Where(v => v > 200 && v < 400).DefaultIfEmpty(double.NaN).Max();
        var diskIdle = _pdh.Read(_diskIdle, allowAbove100: true);
        var diskLatency = _pdh.Read(_diskLatency);

        var (gpuUsage, gpuDedicated, gpuShared) = ReadGpu();
        var nvml = GetNvml();

        var hasPower = NativeMethods.GetSystemPowerStatus(out var power);
        var hasBattery = hasPower && (power.BatteryFlag & 128) == 0 && power.BatteryFlag != 255;
        double? batteryRate = null;
        if (hasBattery && NativeMethods.CallNtPowerInformation(NativeMethods.SystemBatteryState, IntPtr.Zero, 0, out var battery, (uint)Marshal.SizeOf<NativeMethods.SYSTEM_BATTERY_STATE>()) == 0
            && battery.BatteryPresent != 0 && battery.Rate != 0 && battery.Rate != unchecked((int)0x80000000))
        {
            batteryRate = battery.Rate;
        }

        MaybeProbeLatency();

        return new SystemMetrics
        {
            Timestamp = DateTimeOffset.UtcNow,
            CpuTotalPercent = cpuTotal is null ? null : Math.Clamp(cpuTotal.Value, 0, 100),
            CpuPerCorePercent = perCore,
            CpuFrequencyMhz = performancePercent is not null && CpuBaseFrequencyMhz is > 0 ? CpuBaseFrequencyMhz.Value * performancePercent.Value / 100.0 : null,
            CpuPerformanceLimitPercent = _pdh.Read(_cpuLimit),
            ThermalZoneCelsius = double.IsNaN(thermalKelvin) ? null : thermalKelvin - 273.15,
            ProcessorQueueLength = _pdh.Read(_queue),
            DpcTimePercent = _pdh.Read(_dpc),
            InterruptTimePercent = _pdh.Read(_interrupt),

            GpuUsagePercent = gpuUsage,
            GpuDedicatedUsedBytes = gpuDedicated,
            GpuDedicatedTotalBytes = GpuDedicatedTotalBytes,
            GpuSharedUsedBytes = gpuShared,
            GpuTemperatureCelsius = nvml?.TemperatureCelsius,
            GpuPowerWatts = nvml?.PowerWatts,
            GpuClockMhz = nvml?.GraphicsClockMhz,

            MemoryTotalBytes = hasMemory ? memory.ullTotalPhys : 0,
            MemoryAvailableBytes = hasMemory ? memory.ullAvailPhys : 0,
            CommitUsedBytes = hasPerformance ? (ulong)performance.CommitTotal * performance.PageSize : 0,
            CommitLimitBytes = hasPerformance ? (ulong)performance.CommitLimit * performance.PageSize : 0,
            StandbyCacheBytes = Sum(_pdh.Read(_standbyNormal), _pdh.Read(_standbyReserve), _pdh.Read(_standbyCore)),
            ModifiedPageBytes = _pdh.Read(_modified),
            CachedBytes = _pdh.Read(_cache),

            DiskActivePercent = diskIdle is null ? null : Math.Clamp(100 - diskIdle.Value, 0, 100),
            DiskReadBytesPerSecond = _pdh.Read(_diskRead),
            DiskWriteBytesPerSecond = _pdh.Read(_diskWrite),
            DiskQueueLength = _pdh.Read(_diskQueue),
            DiskLatencyMs = diskLatency is null ? null : diskLatency.Value * 1000.0,

            NetworkDownBytesPerSecond = SumInterfaces(_netReceived),
            NetworkUpBytesPerSecond = SumInterfaces(_netSent),
            LatencyMs = _lastLatency,

            BatteryPercent = hasBattery && power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null,
            OnAcPower = hasPower && power.ACLineStatus != 255 ? power.ACLineStatus == 1 : null,
            BatteryRateMilliwatts = batteryRate,

            ProcessCount = hasPerformance ? (int)performance.ProcessCount : null,
            ThreadCount = hasPerformance ? (int)performance.ThreadCount : null,
            HandleCount = hasPerformance ? (int)performance.HandleCount : null,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    private static double? Sum(params double?[] values) => values.All(v => v is null) ? null : values.Sum(v => v ?? 0);

    /// <summary>Instanznamen haben die Form „Gruppe,Index“ (z. B. „0,11“).</summary>
    private static (int Group, int Index) ParseCoreKey(string instance)
    {
        var parts = instance.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out var group) && int.TryParse(parts[1], out var index)
            ? (group, index)
            : (0, int.TryParse(instance, out var single) ? single : int.MaxValue);
    }

    private double? SumInterfaces(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        var values = _pdh.ReadArray(counter)
            .Where(i => !i.Instance.Contains("Loopback", StringComparison.OrdinalIgnoreCase) && !i.Instance.Contains("isatap", StringComparison.OrdinalIgnoreCase) && !i.Instance.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return values.Count == 0 ? null : values.Sum(i => i.Value);
    }

    /// <summary>
    /// GPU-Auslastung wie im Task-Manager: je Adapter und Engine werden die Anteile aller Prozesse summiert; angezeigt wird die am stärksten belastete Engine.
    /// Instanznamen: „pid_1234_luid_0x…_0x…_phys_0_eng_0_engtype_3D“.
    /// </summary>
    private (double? Usage, double? Dedicated, double? Shared) ReadGpu()
    {
        double? usage = null;
        if (_gpuEngine != IntPtr.Zero)
        {
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instance, value) in _pdh.ReadArray(_gpuEngine, allowAbove100: true))
            {
                var luid = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
                var key = luid >= 0 ? instance[luid..] : instance;
                engines[key] = engines.GetValueOrDefault(key) + value;
            }
            // Auch ohne laufende Last existieren die Zähler; eine leere Liste bedeutet lediglich „noch keine Daten“.
            usage = engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
        }

        double? dedicated = _gpuDedicated == IntPtr.Zero ? null : _pdh.ReadArray(_gpuDedicated).Select(i => i.Value).DefaultIfEmpty(0).Max();
        double? shared = _gpuShared == IntPtr.Zero ? null : _pdh.ReadArray(_gpuShared).Select(i => i.Value).DefaultIfEmpty(0).Max();
        return (usage, dedicated, shared);
    }

    private NvmlSensors? GetNvml()
    {
        if (!NvidiaSensorsEnabled) return null;
        if (!_nvmlRequested)
        {
            _nvmlRequested = true;
            _nvml = NvmlSensors.TryCreate();
            if (_nvml.IsAvailable) _unavailable.Remove(MetricId.GpuTemperature);
        }
        return _nvml is { IsAvailable: true } ? _nvml : null;
    }

    /// <summary>Misst höchstens alle fünf Sekunden und nie auf dem Erfassungsthread, damit eine langsame Antwort die Messschleife nicht aufhält.</summary>
    private void MaybeProbeLatency()
    {
        var endpoints = LatencyEndpoints;
        if (endpoints.Count == 0)
        {
            _lastLatency = null;
            return;
        }
        _unavailable.Remove(MetricId.Latency);
        if ((DateTime.UtcNow - _lastLatencyProbe).TotalSeconds < 5 || Interlocked.Exchange(ref _latencyProbeRunning, 1) == 1) return;
        _lastLatencyProbe = DateTime.UtcNow;
        var target = endpoints[0];
        _ = Task.Run(async () =>
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(target, 2000).ConfigureAwait(false);
                _lastLatency = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
            }
            catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException or ArgumentException)
            {
                _lastLatency = null;
            }
            finally
            {
                Interlocked.Exchange(ref _latencyProbeRunning, 0);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pdh.Dispose();
        _nvml?.Dispose();
    }
}
