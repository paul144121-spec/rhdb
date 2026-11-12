using System.Runtime.InteropServices;
using NexusOptimizer.Windows.Native;

namespace NexusOptimizer.Windows.Monitoring;

/// <summary>
/// Schlanke Hülle um die PDH-API (Performance Data Helper). Verwendet ausschließlich englische Zählernamen
/// (PdhAddEnglishCounter), damit die Abfragen auf jeder Windows-Sprachversion funktionieren.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private IntPtr _buffer;
    private int _bufferSize;
    private readonly object _gate = new();

    public PdhQuery()
    {
        var status = NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != NativeMethods.ErrorSuccess) throw new InvalidOperationException($"PDH-Abfrage konnte nicht geöffnet werden (0x{status:X8}).");
    }

    /// <summary>Fügt einen Zähler hinzu. Liefert IntPtr.Zero, wenn der Zähler auf diesem System nicht existiert.</summary>
    public IntPtr TryAdd(string englishPath)
    {
        lock (_gate)
        {
            if (_query == IntPtr.Zero) return IntPtr.Zero;
            return NativeMethods.PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var counter) == NativeMethods.ErrorSuccess ? counter : IntPtr.Zero;
        }
    }

    public bool Collect()
    {
        lock (_gate) return _query != IntPtr.Zero && NativeMethods.PdhCollectQueryData(_query) == NativeMethods.ErrorSuccess;
    }

    /// <summary>Liest einen Einzelwert; null, wenn (noch) keine gültigen Daten vorliegen – etwa beim ersten Abruf eines Ratenzählers.</summary>
    public double? Read(IntPtr counter, bool allowAbove100 = false)
    {
        if (counter == IntPtr.Zero) return null;
        lock (_gate)
        {
            if (_query == IntPtr.Zero) return null;
            var format = NativeMethods.PdhFmtDouble | (allowAbove100 ? NativeMethods.PdhFmtNoCap100 : 0);
            if (NativeMethods.PdhGetFormattedCounterValue(counter, format, out _, out var value) != NativeMethods.ErrorSuccess) return null;
            if (value.CStatus > 1 || double.IsNaN(value.DoubleValue) || double.IsInfinity(value.DoubleValue)) return null;
            return value.DoubleValue;
        }
    }

    /// <summary>Liest alle Instanzen eines Platzhalter-Zählers (z. B. „\Processor Information(*)\…“).</summary>
    public IReadOnlyList<(string Instance, double Value)> ReadArray(IntPtr counter, bool allowAbove100 = false)
    {
        if (counter == IntPtr.Zero) return Array.Empty<(string, double)>();
        lock (_gate)
        {
            if (_query == IntPtr.Zero) return Array.Empty<(string, double)>();
            var format = NativeMethods.PdhFmtDouble | (allowAbove100 ? NativeMethods.PdhFmtNoCap100 : 0);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var size = (uint)_bufferSize;
                var status = NativeMethods.PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, _buffer);
                if (status == NativeMethods.PdhMoreData || (status == NativeMethods.ErrorSuccess && _buffer == IntPtr.Zero && size > 0))
                {
                    // Puffer zu klein: mit Reserve neu anlegen, da zwischen zwei Aufrufen Instanzen hinzukommen können.
                    if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                    _bufferSize = (int)size + 4096;
                    _buffer = Marshal.AllocHGlobal(_bufferSize);
                    continue;
                }
                if (status != NativeMethods.ErrorSuccess || count == 0) return Array.Empty<(string, double)>();

                var itemSize = IntPtr.Size + 16; // LPWSTR szName + PDH_FMT_COUNTERVALUE
                var result = new List<(string, double)>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var item = _buffer + i * itemSize;
                    var cStatus = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                    if (cStatus > 1) continue;
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    result.Add((name, value));
                }
                return result;
            }
            return Array.Empty<(string, double)>();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero)
            {
                NativeMethods.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = 0;
            }
        }
    }
}

/// <summary>
/// Optionaler Sensoranbieter für NVIDIA-Grafikkarten über die NVIDIA Management Library (nvml.dll), die der Treiber mitinstalliert.
/// Die Bibliothek wird ausschließlich aus dem Systemverzeichnis geladen. Fehlt sie, bleiben die Werte „nicht verfügbar“.
/// </summary>
internal sealed class NvmlSensors : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ShutdownDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetHandleDelegate(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUIntDelegate(IntPtr device, int selector, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPowerDelegate(IntPtr device, out uint milliwatts);

    private IntPtr _library;
    private IntPtr _device;
    private ShutdownDelegate? _shutdown;
    private GetUIntDelegate? _temperature;
    private GetUIntDelegate? _clock;
    private GetPowerDelegate? _power;

    public bool IsAvailable => _device != IntPtr.Zero;
    public string? UnavailableReason { get; private set; }

    public static NvmlSensors TryCreate()
    {
        var sensors = new NvmlSensors();
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
            if (!File.Exists(path))
            {
                sensors.UnavailableReason = "Keine NVIDIA-Treiberschnittstelle (nvml.dll) gefunden.";
                return sensors;
            }
            if (!NativeLibrary.TryLoad(path, out sensors._library))
            {
                sensors.UnavailableReason = "Die NVIDIA-Treiberschnittstelle konnte nicht geladen werden.";
                return sensors;
            }

            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(sensors._library, name));
            var init = Export<InitDelegate>("nvmlInit_v2");
            sensors._shutdown = Export<ShutdownDelegate>("nvmlShutdown");
            var getHandle = Export<GetHandleDelegate>("nvmlDeviceGetHandleByIndex_v2");
            sensors._temperature = Export<GetUIntDelegate>("nvmlDeviceGetTemperature");
            sensors._clock = Export<GetUIntDelegate>("nvmlDeviceGetClockInfo");
            sensors._power = Export<GetPowerDelegate>("nvmlDeviceGetPowerUsage");

            if (init() != 0 || getHandle(0, out sensors._device) != 0)
            {
                sensors._device = IntPtr.Zero;
                sensors.UnavailableReason = "Die NVIDIA-Treiberschnittstelle meldet keine unterstützte Grafikkarte.";
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            sensors._device = IntPtr.Zero;
            sensors.UnavailableReason = "Die NVIDIA-Treiberschnittstelle ist mit dieser Anwendung nicht kompatibel.";
        }
        return sensors;
    }

    public double? TemperatureCelsius => IsAvailable && _temperature!(_device, 0, out var value) == 0 ? value : null;
    public double? GraphicsClockMhz => IsAvailable && _clock!(_device, 0, out var value) == 0 ? value : null;
    public double? PowerWatts => IsAvailable && _power!(_device, out var value) == 0 ? value / 1000.0 : null;

    public void Dispose()
    {
        try
        {
            if (_library != IntPtr.Zero)
            {
                _shutdown?.Invoke();
                NativeLibrary.Free(_library);
            }
        }
        catch (Exception) { /* Beenden der Treiberschnittstelle darf nie den Programmablauf stören */ }
        _library = IntPtr.Zero;
        _device = IntPtr.Zero;
    }
}
