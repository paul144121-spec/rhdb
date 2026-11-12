using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Windows.Native;

namespace NexusOptimizer.Windows.SystemAccess;

/// <summary>Listet Autostarteinträge aus den unterstützten Orten: Run-Schlüssel (Benutzer, Computer, 32 Bit) und Autostart-Ordner.</summary>
public sealed class WindowsStartupInventory : IStartupInventory
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly IRegistryStore _registry;

    public WindowsStartupInventory(IRegistryStore registry) => _registry = registry;

    public IReadOnlyList<StartupEntry> GetEntries()
    {
        var entries = new List<StartupEntry>();
        ReadRun(entries, StartupSource.RegistryRunUser, RegistryHive.CurrentUser, RegistryView.Registry64);
        ReadRun(entries, StartupSource.RegistryRunMachine, RegistryHive.LocalMachine, RegistryView.Registry64);
        ReadRun(entries, StartupSource.RegistryRunMachine32, RegistryHive.LocalMachine, RegistryView.Registry32);
        ReadFolder(entries, StartupSource.StartupFolderUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        ReadFolder(entries, StartupSource.StartupFolderCommon, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        return entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private bool IsEnabled(StartupSource source, string name)
    {
        try
        {
            var target = StartupApproval.Target(source, name);
            return StartupApproval.IsEnabled(_registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return true; }
    }

    private void ReadRun(List<StartupEntry> entries, StartupSource source, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(RunKey);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var command = key.GetValue(name)?.ToString() ?? "";
                entries.Add(Describe(source, name, command, ExtractExecutable(command)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    private void ReadFolder(List<StartupEntry> entries, StartupSource source, string folder)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(Describe(source, name, file, file));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }

    private StartupEntry Describe(StartupSource source, string name, string command, string? path)
    {
        string? publisher = null, product = null;
        var exists = false;
        long? size = null;
        try
        {
            if (path is not null && File.Exists(path))
            {
                exists = true;
                size = new FileInfo(path).Length;
                if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var version = FileVersionInfo.GetVersionInfo(path);
                    publisher = string.IsNullOrWhiteSpace(version.CompanyName) ? null : version.CompanyName.Trim();
                    product = string.IsNullOrWhiteSpace(version.FileDescription) ? version.ProductName?.Trim() : version.FileDescription.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        return new StartupEntry
        {
            Source = source, Name = name, Command = command, FilePath = path, Enabled = IsEnabled(source, name),
            Publisher = publisher, ProductName = string.IsNullOrWhiteSpace(product) ? null : product, FileExists = exists, FileSizeBytes = size,
        };
    }

    /// <summary>Ermittelt die ausführbare Datei aus einer Befehlszeile – nur zur Anzeige, die Zeile wird nie ausgeführt.</summary>
    internal static string? ExtractExecutable(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.Length == 0) return null;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return command[..(exe + 4)];
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}

/// <summary>Prozessliste mit CPU-, Speicher- und E/A-Raten sowie abgesicherten Eingriffen (Priorität, Beenden).</summary>
public sealed class WindowsProcessMonitor : IProcessMonitor, IProcessControl
{
    private sealed record Sample(TimeSpan Cpu, ulong IoBytes, long Timestamp);

    private readonly IServiceManager? _services;
    private readonly Dictionary<int, Sample> _previous = new();
    private readonly ConcurrentDictionary<string, (string? Publisher, string? Description)> _fileInfo = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, List<string>> _servicesByProcess = new();
    private long _servicesRefreshed;
    private readonly object _gate = new();

    public WindowsProcessMonitor(IServiceManager? services = null) => _services = services;

    public IReadOnlyList<(int ProcessId, string Name)> GetRunningProcessNames()
    {
        var result = new List<(int, string)>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry)) return result;
            do
            {
                result.Add(((int)entry.th32ProcessID, entry.szExeFile));
            } while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
        return result;
    }

    private Dictionary<int, int> ReadParents()
    {
        var parents = new Dictionary<int, int>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return parents;
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(snapshot, ref entry)) return parents;
            do { parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; } while (NativeMethods.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
        return parents;
    }

    public IReadOnlyList<ProcessSnapshot> GetProcesses()
    {
        lock (_gate)
        {
            RefreshServices();
            var parents = ReadParents();
            var now = Stopwatch.GetTimestamp();
            var seen = new HashSet<int>();
            var result = new List<ProcessSnapshot>();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var id = process.Id;
                    seen.Add(id);
                    string name;
                    try { name = process.ProcessName; } catch (InvalidOperationException) { continue; }

                    var denied = false;
                    TimeSpan? cpuTime = null;
                    DateTime? start = null;
                    ProcessPriorityLevel? priority = null;
                    long workingSet = 0, privateBytes = 0;
                    int threads = 0, handles = 0, session = 0;
                    try { workingSet = process.WorkingSet64; privateBytes = process.PrivateMemorySize64; threads = process.Threads.Count; handles = process.HandleCount; session = process.SessionId; }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
                    try { cpuTime = process.TotalProcessorTime; start = process.StartTime; }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { denied = true; }
                    try { priority = Map(process.PriorityClass); }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }

                    var (path, io) = ReadPathAndIo(id);
                    double cpuPercent = 0;
                    double? ioRate = null;
                    if (_previous.TryGetValue(id, out var previous))
                    {
                        var seconds = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                        if (seconds > 0.05)
                        {
                            if (cpuTime is not null) cpuPercent = Math.Clamp((cpuTime.Value - previous.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100.0, 0, 100);
                            if (io is not null && io.Value >= previous.IoBytes) ioRate = (io.Value - previous.IoBytes) / seconds;
                        }
                    }
                    _previous[id] = new Sample(cpuTime ?? TimeSpan.Zero, io ?? 0, now);

                    var (publisher, description) = path is null ? (null, null) : _fileInfo.GetOrAdd(path, ReadFileInfo);
                    result.Add(new ProcessSnapshot
                    {
                        ProcessId = id,
                        ParentProcessId = parents.GetValueOrDefault(id),
                        Name = name,
                        ExecutablePath = path,
                        Publisher = publisher,
                        Description = description,
                        CpuPercent = cpuPercent,
                        WorkingSetBytes = workingSet,
                        PrivateBytes = privateBytes,
                        DiskBytesPerSecond = ioRate,
                        ThreadCount = threads,
                        HandleCount = handles,
                        StartTime = start,
                        Priority = priority,
                        IsProtected = id <= 4 || ProcessProtection.IsProtected(name),
                        IsAccessDenied = denied && path is null,
                        Services = _servicesByProcess.TryGetValue(id, out var services) ? services : Array.Empty<string>(),
                        SessionId = session,
                    });
                }
            }

            foreach (var gone in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(gone);
            return result;
        }
    }

    private void RefreshServices()
    {
        if (_services is null || Stopwatch.GetElapsedTime(_servicesRefreshed).TotalSeconds < 30 && _servicesRefreshed != 0) return;
        _servicesRefreshed = Stopwatch.GetTimestamp();
        try
        {
            _servicesByProcess = _services.GetServices().Where(s => s.ProcessId > 0).GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.Select(s => s.DisplayName).ToList());
        }
        catch (Exception) { /* Dienstzuordnung ist eine Zusatzinformation */ }
    }

    private static (string? Path, ulong? IoBytes) ReadPathAndIo(int processId)
    {
        if (processId <= 4) return (null, null);
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return (null, null);
        try
        {
            string? path = null;
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size) && size > 0) path = new string(buffer, 0, (int)size);
            ulong? io = NativeMethods.GetProcessIoCounters(handle, out var counters) ? counters.ReadTransferCount + counters.WriteTransferCount : null;
            return (path, io);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static (string?, string?) ReadFileInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim(), string.IsNullOrWhiteSpace(info.FileDescription) ? null : info.FileDescription.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (null, null); }
    }

    private static ProcessPriorityLevel? Map(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => ProcessPriorityLevel.Idle,
        ProcessPriorityClass.BelowNormal => ProcessPriorityLevel.BelowNormal,
        ProcessPriorityClass.Normal => ProcessPriorityLevel.Normal,
        ProcessPriorityClass.AboveNormal => ProcessPriorityLevel.AboveNormal,
        ProcessPriorityClass.High => ProcessPriorityLevel.High,
        _ => null, // „Echtzeit“ wird angezeigt als unbekannt und nie gesetzt
    };

    public OperationResult Terminate(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (processId <= 4 || ProcessProtection.IsProtected(process.ProcessName))
                return OperationResult.Fail("Dieser Prozess gehört zu Windows oder zur Sicherheitssoftware und wird nicht beendet.");
            if (processId == Environment.ProcessId) return OperationResult.Fail("Die Anwendung beendet sich nicht selbst über den Prozess-Manager.");
            var name = process.ProcessName;
            process.Kill(entireProcessTree: false);
            return process.WaitForExit(5000) ? OperationResult.Ok($"„{name}“ (PID {processId}) wurde beendet.") : OperationResult.Fail($"„{name}“ hat auf die Beendigung nicht reagiert.");
        }
        catch (ArgumentException) { return OperationResult.Fail("Der Prozess läuft nicht mehr."); }
        catch (Win32Exception ex) { return OperationResult.Fail("Zugriff verweigert. Der Prozess gehört einem anderen Benutzer oder ist geschützt.", ex.Message); }
        catch (InvalidOperationException) { return OperationResult.Fail("Der Prozess wurde bereits beendet."); }
        catch (NotSupportedException ex) { return OperationResult.Fail("Dieser Prozess kann nicht beendet werden.", ex.Message); }
    }

    public ProcessIdentity? Find(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            ProcessPriorityLevel? priority = null;
            long start = 0;
            try { priority = Map(process.PriorityClass); start = process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
            return new ProcessIdentity(processId, process.ProcessName, start, priority, processId <= 4 || ProcessProtection.IsProtected(process.ProcessName));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    public void SetPriority(int processId, ProcessPriorityLevel priority)
    {
        using var process = Process.GetProcessById(processId);
        if (ProcessProtection.IsProtected(process.ProcessName)) throw new InvalidOperationException("Geschützte Prozesse werden nicht verändert.");
        process.PriorityClass = priority switch
        {
            ProcessPriorityLevel.Idle => ProcessPriorityClass.Idle,
            ProcessPriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
            ProcessPriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
            ProcessPriorityLevel.High => ProcessPriorityClass.High,
            _ => ProcessPriorityClass.Normal,
        };
    }
}
