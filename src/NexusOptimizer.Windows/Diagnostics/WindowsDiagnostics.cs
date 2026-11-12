using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NexusOptimizer.Core.Diagnostics;
using Native = NexusOptimizer.Windows.Native;

namespace NexusOptimizer.Windows.Diagnostics;

/// <summary>Rein lesende Windows-Diagnose: Ereignisprotokolle, Geräteprobleme, Abstürze, Zuverlässigkeit und Sicherheits-/Startkonfiguration.</summary>
public sealed class WindowsDiagnostics : ISystemDiagnostics
{
    public Task<IReadOnlyList<EventLogEntryInfo>> GetRecentErrorsAsync(TimeSpan window, int maxEntries = 100, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<EventLogEntryInfo>>(() =>
        {
            var entries = new List<EventLogEntryInfo>();
            var milliseconds = (long)Math.Clamp(window.TotalMilliseconds, 60_000, TimeSpan.FromDays(30).TotalMilliseconds);
            foreach (var log in new[] { "System", "Application" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Stufe 1 = Kritisch, 2 = Fehler.
                var query = new EventLogQuery(log, PathType.LogName, $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]") { ReverseDirection = true };
                try
                {
                    using var reader = new EventLogReader(query);
                    for (var i = 0; i < maxEntries; i++)
                    {
                        using var record = reader.ReadEvent();
                        if (record is null) break;
                        string message;
                        try { message = record.FormatDescription() ?? ""; }
                        catch (EventLogException) { message = ""; }
                        if (string.IsNullOrWhiteSpace(message)) message = "(Für dieses Ereignis ist keine Beschreibung verfügbar.)";
                        entries.Add(new EventLogEntryInfo(record.TimeCreated ?? DateTime.MinValue, log == "System" ? "System" : "Anwendung", record.ProviderName ?? "", record.Id,
                            record.Level == 1 ? "Kritisch" : "Fehler", message.Length > 600 ? message[..600] + "…" : message));
                    }
                }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or InvalidOperationException) { }
            }
            return entries.OrderByDescending(e => e.Time).Take(maxEntries).ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<DeviceProblem>> GetDeviceProblemsAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DeviceProblem>>(() =>
        {
            var problems = new List<DeviceProblem>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
                using var items = searcher.Get();
                foreach (var item in items)
                {
                    using (item)
                    {
                        var code = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? 0);
                        problems.Add(new DeviceProblem(item["Name"]?.ToString() ?? "Unbekanntes Gerät", item["PNPDeviceID"]?.ToString(), code, ExplainDeviceError(code)));
                    }
                }
            }
            catch (Exception ex) when (Native.WmiSupport.IsRecoverable(ex)) { }
            return problems;
        }, cancellationToken);

    private static string ExplainDeviceError(int code) => code switch
    {
        1 => "Das Gerät ist nicht richtig konfiguriert.",
        3 => "Der Treiber ist möglicherweise beschädigt oder es fehlt Arbeitsspeicher.",
        10 => "Das Gerät kann nicht gestartet werden.",
        12 => "Das Gerät findet nicht genügend freie Ressourcen.",
        14 => "Ein Neustart ist erforderlich, damit das Gerät funktioniert.",
        18 => "Die Treiber müssen neu installiert werden.",
        19 => "Die Konfigurationsinformationen in der Registrierung sind unvollständig oder beschädigt.",
        21 => "Windows entfernt das Gerät gerade.",
        22 => "Das Gerät ist deaktiviert.",
        24 => "Das Gerät ist nicht vorhanden oder funktioniert nicht ordnungsgemäß.",
        28 => "Für das Gerät sind keine Treiber installiert.",
        29 => "Das Gerät ist in der Firmware deaktiviert.",
        31 => "Windows kann die erforderlichen Treiber nicht laden.",
        32 => "Der Treiberdienst des Geräts ist deaktiviert.",
        37 => "Der Gerätetreiber konnte nicht initialisiert werden.",
        39 => "Der Treiber ist beschädigt oder fehlt.",
        43 => "Das Gerät hat Probleme gemeldet und wurde angehalten.",
        45 => "Das Gerät ist derzeit nicht angeschlossen.",
        52 => "Die digitale Signatur des Treibers kann nicht überprüft werden.",
        _ => $"Geräte-Manager-Fehlercode {code}.",
    };

    public Task<IReadOnlyList<CrashInfo>> GetApplicationCrashesAsync(TimeSpan window, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CrashInfo>>(() =>
        {
            var crashes = new List<CrashInfo>();
            var milliseconds = (long)Math.Clamp(window.TotalMilliseconds, 60_000, TimeSpan.FromDays(90).TotalMilliseconds);
            // 1000 = Anwendungsfehler (Absturz), 1002 = Anwendung reagiert nicht.
            var query = new EventLogQuery("Application", PathType.LogName, $"*[System[(EventID=1000 or EventID=1002) and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]") { ReverseDirection = true };
            try
            {
                using var reader = new EventLogReader(query);
                for (var i = 0; i < 200; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var record = reader.ReadEvent();
                    if (record is null) break;
                    var properties = record.Properties;
                    string? Property(int index) => properties.Count > index ? properties[index].Value?.ToString() : null;
                    var hang = record.Id == 1002;
                    crashes.Add(new CrashInfo(record.TimeCreated ?? DateTime.MinValue, Property(0) ?? "Unbekannte Anwendung", hang ? null : Property(3), hang ? null : Property(6), hang));
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or InvalidOperationException) { }
            return crashes;
        }, cancellationToken);

    public Task<ReliabilityInfo> GetReliabilityAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics");
                using var items = searcher.Get();
                double? latestIndex = null;
                DateTime? latestTime = null;
                foreach (var item in items)
                {
                    using (item)
                    {
                        var time = ManagementDateTimeConverter.ToDateTime(item["TimeGenerated"].ToString()!);
                        if (latestTime is null || time > latestTime)
                        {
                            latestTime = time;
                            latestIndex = Convert.ToDouble(item["SystemStabilityIndex"]);
                        }
                    }
                }
                return latestIndex is null
                    ? new ReliabilityInfo(null, null, "Windows hat noch keinen Stabilitätsindex berechnet (die Zuverlässigkeitsüberwachung benötigt dafür einige Tage Laufzeit).")
                    : new ReliabilityInfo(latestIndex, latestTime, "Stabilitätsindex der Windows-Zuverlässigkeitsüberwachung (1 = instabil, 10 = sehr stabil).");
            }
            catch (Exception ex) when (Native.WmiSupport.IsRecoverable(ex) || ex is ArgumentOutOfRangeException)
            {
                return new ReliabilityInfo(null, null, "Die Zuverlässigkeitsüberwachung ist auf diesem System nicht verfügbar oder erfordert Administratorrechte.");
            }
        }, cancellationToken);

    public Task<IReadOnlyList<AdvancedDiagnosticItem>> GetAdvancedDiagnosticsAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<AdvancedDiagnosticItem>>(() =>
        {
            var items = new List<AdvancedDiagnosticItem>();
            int? Dword(string path, string name)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(path);
                    return key?.GetValue(name) is int value ? value : null;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
            }
            static string OnOff(int? value, string unknown = "Nicht konfiguriert") => value switch { null => unknown, 0 => "Aus", _ => "Ein" };

            const string security = "Sicherheit (nur Anzeige)";
            items.Add(new AdvancedDiagnosticItem(security, "Speicherintegrität (HVCI) laut Konfiguration",
                OnOff(Dword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled")),
                "Schützt den Kernel vor nicht vertrauenswürdigen Treibern. Wird von dieser Anwendung nie verändert.", true));
            items.Add(new AdvancedDiagnosticItem(security, "Virtualisierungsbasierte Sicherheit laut Konfiguration",
                OnOff(Dword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity")),
                "Grundlage für Speicherintegrität und Credential Guard. Wird von dieser Anwendung nie verändert.", true));
            items.Add(new AdvancedDiagnosticItem(security, "Sicherer Start (Secure Boot)",
                OnOff(Dword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled"), "Nicht ermittelbar (Legacy-BIOS?)"),
                "Verhindert das Laden unsignierter Startkomponenten. Lässt sich nur in der Firmware ändern.", true));
            items.Add(new AdvancedDiagnosticItem(security, "Benutzerkontensteuerung (UAC)",
                OnOff(Dword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA")),
                "Trennt Standard- und Administratorrechte. Wird von dieser Anwendung nie verändert.", true));

            const string scheduling = "Zeitplanung und Reaktionsverhalten";
            var separation = Dword(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation");
            items.Add(new AdvancedDiagnosticItem(scheduling, "Win32PrioritySeparation",
                separation is null ? "Nicht gesetzt (Standard)" : $"{separation} (0x{separation:X})",
                "Steuert Zeitscheiben und Vordergrundbevorzugung. Der Client-Standardwert 2 entspricht bereits „kurze, variable Zeitscheiben mit maximaler Vordergrundbevorzugung“ – häufig empfohlene Alternativwerte bringen daher keinen belegten Vorteil. Bewusst nicht als Tweak angeboten."));
            items.Add(new AdvancedDiagnosticItem(scheduling, "MMCSS SystemResponsiveness",
                Dword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness")?.ToString() is { } r ? r + " %" : "Nicht gesetzt (Standard 20 %)",
                "Anteil der CPU-Zeit, der Hintergrundaufgaben unter Multimedialast garantiert wird."));
            var throttling = Dword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex");
            items.Add(new AdvancedDiagnosticItem(scheduling, "MMCSS NetworkThrottlingIndex",
                throttling is null ? "Nicht gesetzt (Standard 10)" : unchecked((uint)throttling.Value) == 0xFFFFFFFF ? "Drosselung aus" : throttling.Value.ToString(),
                "Begrenzung der Paketverarbeitung während Multimediawiedergabe."));
            items.Add(new AdvancedDiagnosticItem(scheduling, "Zeitgeberauflösung und Interrupts",
                "Siehe DPC-/Interrupt-Zeit im System-Monitor",
                "Windows bietet keine dokumentierte Einstellung, die Zeitgeber systemweit „verbessert“. Hohe DPC- oder Interrupt-Zeiten deuten auf Treiberprobleme hin – dafür eignet sich die Messung im System-Monitor bzw. das Windows Performance Toolkit."));

            const string memory = "Speicherverwaltung";
            items.Add(new AdvancedDiagnosticItem(memory, "Auslagerungsdatei beim Herunterfahren leeren",
                OnOff(Dword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown"), "Aus (Standard)"),
                "Sicherheitsoption; verlängert das Herunterfahren."));
            var prefetch = Dword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher");
            items.Add(new AdvancedDiagnosticItem(memory, "Prefetcher",
                prefetch switch { null => "Nicht gesetzt (Standard)", 0 => "Aus", 1 => "Nur Anwendungen", 2 => "Nur Systemstart", 3 => "Anwendungen und Systemstart (Standard)", _ => prefetch.Value.ToString() },
                "Beschleunigt Programm- und Systemstarts. Das Abschalten bringt auch auf SSDs keinen Vorteil und wird nicht angeboten."));

            const string graphics = "Grafik";
            var hags = Dword(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            items.Add(new AdvancedDiagnosticItem(graphics, "Hardwarebeschleunigte GPU-Planung",
                hags switch { null => "Vom Treiber nicht bereitgestellt", 2 => "Ein", 1 => "Aus", _ => hags.Value.ToString() },
                "Verfügbar ab WDDM 2.7 und passender Grafikkarte."));

            const string power = "Energie";
            items.Add(new AdvancedDiagnosticItem(power, "Schnellstart",
                OnOff(Dword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"), "Nicht gesetzt"),
                "Beim Schnellstart wird der Kernel beim Herunterfahren nicht neu initialisiert."));
            var csEnabled = Dword(@"SYSTEM\CurrentControlSet\Control\Power", "PlatformAoAcOverride");
            items.Add(new AdvancedDiagnosticItem(power, "Modern-Standby-Übersteuerung",
                csEnabled is null ? "Nicht gesetzt (Standard)" : csEnabled.Value.ToString(),
                "Geräte mit Modern Standby bieten in der Regel nur den Energiesparplan „Ausbalanciert“ an."));

            return items;
        }, cancellationToken);
}
