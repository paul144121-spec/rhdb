using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Tools;
using NexusOptimizer.Windows.Native;

namespace NexusOptimizer.Windows.SystemAccess;

/// <summary>Löst die Bereinigungskategorien in feste, bekannte Windows-Pfade auf. Es gibt keine frei wählbaren Pfade.</summary>
public sealed class WindowsCleanupLocations : ICleanupLocationProvider
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public IReadOnlyList<CleanupLocation> GetLocations(string categoryId) => categoryId switch
    {
        CleanupCategories.UserTemp => new[] { new CleanupLocation(Path.GetTempPath().TrimEnd('\\'), MinAge: TimeSpan.FromHours(24)) },
        CleanupCategories.ThumbnailCache => new[]
        {
            new CleanupLocation(Path.Combine(Local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", Recursive: false),
            new CleanupLocation(Path.Combine(Local, "Microsoft", "Windows", "Explorer"), "iconcache_*.db", Recursive: false),
        },
        CleanupCategories.UserCrashDumps => new[] { new CleanupLocation(Path.Combine(Local, "CrashDumps")) },
        CleanupCategories.UserErrorReports => new[]
        {
            new CleanupLocation(Path.Combine(Local, "Microsoft", "Windows", "WER", "ReportArchive")),
            new CleanupLocation(Path.Combine(Local, "Microsoft", "Windows", "WER", "ReportQueue")),
        },
        CleanupCategories.ShaderCache => new[]
        {
            new CleanupLocation(Path.Combine(Local, "D3DSCache")),
            new CleanupLocation(Path.Combine(Local, "NVIDIA", "DXCache")),
            new CleanupLocation(Path.Combine(Local, "AMD", "DxCache")),
        },
        CleanupCategories.EdgeCache => ChromiumCaches(Path.Combine(Local, "Microsoft", "Edge", "User Data")),
        CleanupCategories.ChromeCache => ChromiumCaches(Path.Combine(Local, "Google", "Chrome", "User Data")),
        CleanupCategories.FirefoxCache => FirefoxCaches(Path.Combine(Local, "Mozilla", "Firefox", "Profiles")),
        CleanupCategories.WindowsTemp => new[] { new CleanupLocation(Path.Combine(Windows, "Temp"), MinAge: TimeSpan.FromHours(24)) },
        CleanupCategories.SystemCrashDumps => new[]
        {
            new CleanupLocation(Path.Combine(Windows, "Minidump")),
            new CleanupLocation(Path.Combine(Windows, "LiveKernelReports"), "*.dmp"),
        },
        CleanupCategories.SystemErrorReports => new[]
        {
            new CleanupLocation(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive")),
            new CleanupLocation(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")),
        },
        CleanupCategories.OldWindowsLogs => new[] { new CleanupLocation(Path.Combine(Windows, "Logs", "CBS"), "CbsPersist_*.*", Recursive: false, MinAge: TimeSpan.FromDays(30)) },
        _ => Array.Empty<CleanupLocation>(),
    };

    /// <summary>Nur die Cache-Unterordner der Profile – niemals das Profil selbst (Lesezeichen, Verlauf, Cookies, Zugangsdaten).</summary>
    private static IReadOnlyList<CleanupLocation> ChromiumCaches(string userData)
    {
        var locations = new List<CleanupLocation>();
        try
        {
            if (!Directory.Exists(userData)) return locations;
            foreach (var profile in Directory.EnumerateDirectories(userData).Where(d => Path.GetFileName(d) is var n && (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal))))
            {
                locations.Add(new CleanupLocation(Path.Combine(profile, "Cache", "Cache_Data")));
                locations.Add(new CleanupLocation(Path.Combine(profile, "Code Cache")));
                locations.Add(new CleanupLocation(Path.Combine(profile, "GPUCache")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return locations;
    }

    private static IReadOnlyList<CleanupLocation> FirefoxCaches(string profiles)
    {
        try
        {
            return Directory.Exists(profiles)
                ? Directory.EnumerateDirectories(profiles).Select(p => new CleanupLocation(Path.Combine(p, "cache2"))).ToList()
                : Array.Empty<CleanupLocation>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<CleanupLocation>(); }
    }

    public bool IsProcessRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
        catch (InvalidOperationException) { return false; }
    }
}

public sealed class WindowsRecycleBin : IRecycleBin
{
    public (long Bytes, long Items)? Query()
    {
        long bytes = 0, items = 0;
        var any = false;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            if (NativeMethods.SHQueryRecycleBinW(drive.Name, ref info) != 0) continue;
            any = true;
            bytes += info.i64Size;
            items += info.i64NumItems;
        }
        return any ? (bytes, items) : null;
    }

    public bool Empty()
    {
        var result = NativeMethods.SHEmptyRecycleBinW(IntPtr.Zero, null, NativeMethods.SherbNoConfirmation | NativeMethods.SherbNoProgressUi | NativeMethods.SherbNoSound);
        // E_UNEXPECTED wird gemeldet, wenn der Papierkorb bereits leer ist.
        return result == 0 || result == unchecked((int)0x8000FFFF);
    }
}

/// <summary>DNS-Konfiguration je Adapter über Win32_NetworkAdapterConfiguration.</summary>
public sealed class WindowsNetworkConfigurator : INetworkConfigurator
{
    public IReadOnlyList<AdapterDnsInfo> GetAdapters()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()) names[adapter.Id] = adapter.Name;
        }
        catch (NetworkInformationException) { }

        var result = new List<AdapterDnsInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT SettingID, Description, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            using var items = searcher.Get();
            foreach (var item in items)
            {
                using (item)
                {
                    var id = item["SettingID"]?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;
                    var servers = (item["DNSServerSearchOrder"] as string[] ?? Array.Empty<string>()).Where(s => !s.Contains(':')).ToList();
                    result.Add(new AdapterDnsInfo(id, names.GetValueOrDefault(id) ?? item["Description"]?.ToString() ?? id, IsAutomatic(id), servers));
                }
            }
        }
        catch (Exception ex) when (WmiSupport.IsRecoverable(ex))
        {
            // Ohne WMI: Adapter und DNS-Server aus der .NET-Netzwerkschnittstelle lesen (nur Anzeige).
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(a => a.OperationalStatus == OperationalStatus.Up && a.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
                {
                    var servers = adapter.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                    result.Add(new AdapterDnsInfo(adapter.Id, adapter.Name, IsAutomatic(adapter.Id), servers));
                }
            }
            catch (NetworkInformationException) { }
        }
        return result;
    }

    /// <summary>Statisch eingetragene DNS-Server stehen im Wert „NameServer“ der Schnittstelle; ist er leer, kommt DNS per DHCP.</summary>
    private static bool IsAutomatic(string adapterId)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");
            return string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return true; }
    }

    public void SetDnsServers(string adapterId, IReadOnlyList<string>? servers)
    {
        if (!Guid.TryParse(adapterId, out _)) throw new InvalidOperationException("Ungültige Adapterkennung.");
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{adapterId}'");
            using var items = searcher.Get();
            foreach (ManagementObject item in items)
            {
                using (item)
                {
                    using var parameters = item.GetMethodParameters("SetDNSServerSearchOrder");
                    parameters["DNSServerSearchOrder"] = servers is { Count: > 0 } ? servers.ToArray() : null;
                    using var output = item.InvokeMethod("SetDNSServerSearchOrder", parameters, null);
                    var code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
                    if (code == 91) throw new UnauthorizedAccessException("Zugriff verweigert.");
                    if (code > 1) throw new InvalidOperationException($"Windows hat die DNS-Änderung abgelehnt (Rückgabewert {code}).");
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException and not InvalidOperationException && WmiSupport.IsRecoverable(ex))
        {
            throw new InvalidOperationException("Die Windows-Verwaltungsschnittstelle (WMI) ist nicht verfügbar: " + ex.Message, ex);
        }
        throw new InvalidOperationException("Der Netzwerkadapter wurde nicht gefunden.");
    }
}

/// <summary>
/// Startet ausschließlich die fest hinterlegten Windows-Bordwerkzeuge – mit absolutem Pfad aus dem Systemverzeichnis,
/// ohne Shell und mit einzeln übergebenen Argumenten. Ausgabe und Rückgabecode werden vollständig erfasst.
/// </summary>
public sealed class WindowsSystemToolRunner : ISystemToolRunner
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static readonly Dictionary<SystemTool, string> Executables = new()
    {
        [SystemTool.Sfc] = "sfc.exe",
        [SystemTool.Dism] = "Dism.exe",
        [SystemTool.Chkdsk] = "chkdsk.exe",
        [SystemTool.Defrag] = "defrag.exe",
        [SystemTool.IpConfig] = "ipconfig.exe",
        [SystemTool.Netsh] = "netsh.exe",
        [SystemTool.PowerCfg] = "powercfg.exe",
    };

    static WindowsSystemToolRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<ToolResult> RunAsync(SystemTool tool, IReadOnlyList<string> arguments, IProgress<string>? output, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Executables.TryGetValue(tool, out var file)) throw new InvalidOperationException("Unbekanntes Werkzeug.");
        var path = Path.Combine(Environment.SystemDirectory, file);
        if (!File.Exists(path)) throw new FileNotFoundException($"Das Windows-Werkzeug „{file}“ wurde nicht gefunden.", path);

        // SFC gibt UTF-16 aus, die übrigen Werkzeuge die OEM-Codepage der Konsole.
        Encoding encoding;
        try { encoding = tool == SystemTool.Sfc ? Encoding.Unicode : Encoding.GetEncoding((int)GetOEMCP()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { encoding = Encoding.UTF8; }

        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        var collected = new StringBuilder();
        var gate = new object();
        string? lastReported = null;

        void Handle(string? line)
        {
            if (line is null) return;
            // Fortschrittsanzeigen arbeiten mit Wagenrücklauf; nur der letzte Stand je Zeile ist relevant.
            var text = line.Split('\r', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim('\0', ' ', '\t');
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (gate)
            {
                if (collected.Length < 512 * 1024) collected.AppendLine(text);
                if (text == lastReported) return;
                lastReported = text;
            }
            output?.Report(text);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            if (!timedOut) throw;
        }

        string text;
        lock (gate) text = collected.ToString();
        return new ToolResult(timedOut ? -1 : process.ExitCode, text, timedOut, stopwatch.Elapsed);
    }
}

/// <summary>Systemwiederherstellung über die WMI-Klasse SystemRestore (wie Checkpoint-Computer).</summary>
public sealed class WindowsSystemRestore : ISystemRestore
{
    public bool? IsProtectionEnabled()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var policy = baseKey.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"))
                if (policy?.GetValue("DisableSR") is 1) return false;
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            return key?.GetValue("RPSessionInterval") is int interval ? interval == 1 : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static uint? NewestSequenceNumber()
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
        using var items = searcher.Get();
        uint? newest = null;
        foreach (var item in items)
        {
            using (item)
            {
                var number = Convert.ToUInt32(item["SequenceNumber"]);
                if (newest is null || number > newest) newest = number;
            }
        }
        return newest;
    }

    public RestorePointResult CreateRestorePoint(string description)
    {
        try
        {
            uint? before = null;
            try { before = NewestSequenceNumber(); } catch (Exception ex) when (WmiSupport.IsRecoverable(ex)) { }

            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var parameters = restore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description;
            parameters["RestorePointType"] = 12u; // MODIFY_SETTINGS
            parameters["EventType"] = 100u;       // BEGIN_SYSTEM_CHANGE
            using var output = restore.InvokeMethod("CreateRestorePoint", parameters, null);
            var code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
            if (code == 1058) return new RestorePointResult(false, "Die Systemwiederherstellung ist deaktiviert. Es wurde kein Wiederherstellungspunkt erstellt.");
            if (code != 0) return new RestorePointResult(false, $"Windows konnte keinen Wiederherstellungspunkt erstellen (Fehler {code}).");

            // Windows meldet auch dann Erfolg, wenn wegen der 24-Stunden-Regel kein neuer Punkt angelegt wurde – deshalb nachprüfen.
            uint? after = null;
            try { after = NewestSequenceNumber(); } catch (Exception ex) when (WmiSupport.IsRecoverable(ex)) { }
            if (after is not null && (before is null || after > before))
                return new RestorePointResult(true, $"Wiederherstellungspunkt „{description}“ wurde erstellt (Nr. {after}).");
            return new RestorePointResult(false, "Windows hat die Anforderung angenommen, aber keinen neuen Wiederherstellungspunkt angelegt. In der Regel liegt das daran, dass innerhalb der letzten 24 Stunden bereits ein Punkt erstellt wurde.");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new RestorePointResult(false, "Zugriff verweigert. Für Wiederherstellungspunkte sind Administratorrechte erforderlich.");
        }
        catch (Exception ex) when (WmiSupport.IsRecoverable(ex))
        {
            return new RestorePointResult(false, "Die Systemwiederherstellung ist auf diesem System nicht verfügbar: " + ex.Message);
        }
    }
}

/// <summary>
/// Ablage von API-Schlüsseln, verschlüsselt mit der Windows-Datenschutz-API (DPAPI) im Benutzerkontext.
/// Die Dateien sind nur für das angemeldete Benutzerkonto auf diesem Computer entschlüsselbar.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NexusOptimizerAI.Secrets.v1");
    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            throw new ArgumentException("Ungültiger Name für ein Geheimnis.", nameof(name));
        return Path.Combine(_directory, name + ".bin");
    }

    public void Set(string name, string? secret)
    {
        var path = PathFor(name);
        if (string.IsNullOrEmpty(secret))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            File.WriteAllBytes(path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string? Get(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null; // z. B. nach Profilwechsel nicht mehr entschlüsselbar → wie „nicht hinterlegt“ behandeln
        }
    }

    public bool Has(string name) => File.Exists(PathFor(name));
}

public enum SignatureStatus
{
    Unknown,
    Valid,
    Unsigned,
    Invalid,
}

/// <summary>Prüft die Authenticode-Signatur einer Datei über WinVerifyTrust (ohne Netzwerkzugriff für Sperrlisten).</summary>
public static class AuthenticodeVerifier
{
    public static SignatureStatus Verify(string filePath)
    {
        if (!File.Exists(filePath)) return SignatureStatus.Unknown;
        var fileInfo = new NativeMethods.WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<NativeMethods.WINTRUST_FILE_INFO>(), pcwszFilePath = filePath };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new NativeMethods.WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<NativeMethods.WINTRUST_DATA>(),
                dwUIChoice = NativeMethods.WtdUiNone,
                fdwRevocationChecks = NativeMethods.WtdRevokeNone,
                dwUnionChoice = NativeMethods.WtdChoiceFile,
                pFile = filePointer,
                dwStateAction = NativeMethods.WtdStateActionVerify,
                dwProvFlags = NativeMethods.WtdCacheOnlyUrlRetrieval,
            };
            var action = NativeMethods.WintrustActionGenericVerifyV2;
            var result = NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = NativeMethods.WtdStateActionClose;
            NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result switch
            {
                0 => SignatureStatus.Valid,
                unchecked((int)0x800B0100) => SignatureStatus.Unsigned, // TRUST_E_NOSIGNATURE
                _ => SignatureStatus.Invalid,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return SignatureStatus.Unknown;
        }
        finally
        {
            Marshal.DestroyStructure<NativeMethods.WINTRUST_FILE_INFO>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    public static string ToGerman(SignatureStatus status) => status switch
    {
        SignatureStatus.Valid => "Gültig signiert",
        SignatureStatus.Unsigned => "Nicht signiert",
        SignatureStatus.Invalid => "Signatur ungültig oder nicht vertrauenswürdig",
        _ => "Nicht prüfbar",
    };
}

/// <summary>Autostart der Anwendung selbst – ausschließlich auf ausdrücklichen Wunsch über die Einstellungen, nur für das eigene Benutzerkonto.</summary>
public static class ApplicationAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NexusOptimizerAI";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    public static void Set(bool enabled, string executablePath, bool minimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\"{(minimized ? " --minimized" : "")}", RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
