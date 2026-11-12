using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NexusOptimizer.Core.Benchmark;

namespace NexusOptimizer.Core.Diagnostics;

public enum FindingSeverity
{
    Ok,
    Info,
    Warning,
    Critical,
    Unavailable,
}

public sealed record DiagnosticFinding(string Area, string Title, string Detail, FindingSeverity Severity, string? PageKey = null);

public sealed record EventLogEntryInfo(DateTime Time, string Log, string Source, int EventId, string Level, string Message);

public sealed record DeviceProblem(string Name, string? DeviceId, int ErrorCode, string Explanation);

public sealed record CrashInfo(DateTime Time, string Application, string? FaultingModule, string? ExceptionCode, bool IsHang);

public sealed record ReliabilityInfo(double? StabilityIndex, DateTime? MeasuredAt, string Note);

/// <summary>Schreibgeschützte Diagnoseangabe für das Expertenlabor (z. B. VBS-Status, Hypervisor, Zeitgeber).</summary>
public sealed record AdvancedDiagnosticItem(string Group, string Name, string Value, string Explanation, bool IsSecurityRelevant = false);

/// <summary>Geführter manueller Ablauf für Einstellungen, die bewusst nicht automatisiert werden.</summary>
public sealed record GuidedWorkflow(string Id, string Title, string Summary, IReadOnlyList<string> Steps, string? SettingsUri, string Warning);

public interface ISystemDiagnostics
{
    Task<IReadOnlyList<EventLogEntryInfo>> GetRecentErrorsAsync(TimeSpan window, int maxEntries = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceProblem>> GetDeviceProblemsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CrashInfo>> GetApplicationCrashesAsync(TimeSpan window, CancellationToken cancellationToken = default);
    Task<ReliabilityInfo> GetReliabilityAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdvancedDiagnosticItem>> GetAdvancedDiagnosticsAsync(CancellationToken cancellationToken = default);
}

public static class GuidedWorkflows
{
    private const string SecurityWarning = "Diese Einstellung betrifft eine Sicherheitsgrenze von Windows. NEXUS Optimizer AI ändert sie nicht automatisch und empfiehlt, sie eingeschaltet zu lassen.";

    public static IReadOnlyList<GuidedWorkflow> All { get; } = new[]
    {
        new GuidedWorkflow("memory-integrity", "Speicherintegrität (Kernisolierung)",
            "Die Speicherintegrität schützt den Windows-Kernel vor manipulierten Treibern. Sie kann in manchen Spielen einige Prozent Leistung kosten.",
            new[]
            {
                "Öffnen Sie „Windows-Sicherheit“ → „Gerätesicherheit“ → „Details zur Kernisolierung“.",
                "Dort sehen Sie den aktuellen Zustand des Schalters „Speicherintegrität“.",
                "Wägen Sie ab: Das Abschalten senkt den Schutz vor Kernel-Angriffen. Für die meisten Systeme ist der Leistungsunterschied gering.",
                "Eine Änderung wird erst nach einem Neustart wirksam.",
            }, "windowsdefender://coreisolation", SecurityWarning),
        new GuidedWorkflow("vbs", "Virtualisierungsbasierte Sicherheit (VBS)",
            "VBS trennt sicherheitskritische Teile von Windows mithilfe des Hypervisors ab. Der Status ist im Expertenlabor schreibgeschützt einsehbar.",
            new[]
            {
                "VBS wird über Gruppenrichtlinien, die Speicherintegrität und Firmwareeinstellungen gesteuert.",
                "Prüfen Sie den Status unter „Systeminformationen“ (msinfo32) im Eintrag „Virtualisierungsbasierte Sicherheit“.",
                "Änderungen sollten nur mit Kenntnis der Folgen für Credential Guard, BitLocker und Anti-Cheat-Systeme erfolgen.",
            }, "ms-settings:windowsdefender", SecurityWarning),
        new GuidedWorkflow("hags", "Hardwarebeschleunigte GPU-Planung",
            "Die Einstellung lässt sich hier als journalisierter Tweak setzen – oder manuell in den Windows-Einstellungen.",
            new[]
            {
                "Öffnen Sie „Einstellungen“ → „System“ → „Anzeige“ → „Grafik“ → „Standardgrafikeinstellungen ändern“.",
                "Schalten Sie „Hardwarebeschleunigte GPU-Planung“ um.",
                "Starten Sie den Computer neu und vergleichen Sie das Verhalten in Ihren Spielen.",
            }, "ms-settings:display-advancedgraphics", "Die Wirkung ist spiel- und treiberabhängig; bei Problemen einfach wieder ausschalten."),
        new GuidedWorkflow("pagefile", "Auslagerungsdatei",
            "Windows verwaltet die Auslagerungsdatei standardmäßig selbst – das ist für fast alle Systeme die beste Einstellung.",
            new[]
            {
                "Öffnen Sie „Erweiterte Systemeinstellungen“ → Registerkarte „Erweitert“ → Leistung „Einstellungen“ → „Erweitert“ → Virtueller Arbeitsspeicher „Ändern“.",
                "Empfohlen: „Auslagerungsdateigröße für alle Laufwerke automatisch verwalten“ aktiviert lassen.",
                "Das Deaktivieren der Auslagerungsdatei bringt keinen Leistungsgewinn und kann zu Abstürzen bei Speichermangel führen.",
            }, null, "Eine zu kleine oder abgeschaltete Auslagerungsdatei kann Programme zum Absturz bringen und verhindert Speicherabbilder zur Fehleranalyse."),
        new GuidedWorkflow("boot-configuration", "Startkonfiguration (BCD)",
            "Änderungen an der Startkonfiguration können Windows unstartbar machen. Diese Anwendung nimmt daran keine Änderungen vor.",
            new[]
            {
                "Verbreitete „Tweaks“ wie das Abschalten des dynamischen Ticks oder das Erzwingen des HPET haben auf aktuellen Systemen keinen belegten Nutzen und können die Latenz sogar verschlechtern.",
                "Wenn Sie die Konfiguration nur einsehen möchten: Eingabeaufforderung als Administrator → „bcdedit /enum“.",
                "Legen Sie vor jeder Änderung ein Wiederherstellungslaufwerk an und notieren Sie die Ausgangswerte.",
            }, null, "Fehlerhafte Startkonfigurationen erfordern eine Reparatur über die Windows-Wiederherstellungsumgebung."),
        new GuidedWorkflow("storage-sense", "Speicheroptimierung (Storage Sense)",
            "Windows kann temporäre Dateien und den Papierkorb selbstständig nach Ihren Vorgaben bereinigen.",
            new[]
            {
                "Öffnen Sie „Einstellungen“ → „System“ → „Speicher“ → „Speicheroptimierung“.",
                "Legen Sie fest, wann bereinigt wird und nach wie vielen Tagen Papierkorb und Downloads geleert werden.",
                "Achten Sie besonders auf die Einstellung für den Ordner „Downloads“ – dort liegen oft noch benötigte Dateien.",
            }, "ms-settings:storagepolicies", "Automatisches Löschen aus „Downloads“ kann Dateien entfernen, die Sie noch brauchen."),
        new GuidedWorkflow("network-reset", "Netzwerk zurücksetzen",
            "Setzt alle Netzwerkadapter und -einstellungen auf den Auslieferungszustand zurück.",
            new[]
            {
                "Öffnen Sie „Einstellungen“ → „Netzwerk und Internet“ → „Erweiterte Netzwerkeinstellungen“ → „Netzwerk zurücksetzen“.",
                "Notieren Sie vorher WLAN-Passwörter, VPN-Zugänge und feste IP-/DNS-Einstellungen.",
                "Der Computer startet nach dem Zurücksetzen neu.",
            }, "ms-settings:network-status", "Gespeicherte WLAN-Netze, VPN-Verbindungen und virtuelle Netzwerkadapter gehen verloren."),
        new GuidedWorkflow("windows-update-troubleshoot", "Windows Update: Problembehandlung",
            "Windows bringt eine eigene Problembehandlung für Updatefehler mit.",
            new[]
            {
                "Öffnen Sie „Einstellungen“ → „System“ → „Problembehandlung“ → „Andere Problembehandlungen“.",
                "Starten Sie „Windows Update“.",
                "Bleibt der Fehler bestehen, führen Sie unter „Windows-Reparatur“ zuerst die DISM-Prüfung und dann SFC aus.",
            }, "ms-settings:troubleshoot", "Windows Update selbst wird von dieser Anwendung nicht deaktiviert oder verändert."),
        new GuidedWorkflow("memory-diagnostic", "Windows-Speicherdiagnose",
            "Prüft den Arbeitsspeicher beim nächsten Start auf Hardwarefehler.",
            new[]
            {
                "Speichern Sie alle geöffneten Dokumente.",
                "Starten Sie „Windows-Speicherdiagnose“ (mdsched.exe) über das Startmenü.",
                "Wählen Sie „Jetzt neu starten und nach Problemen suchen“. Das Ergebnis erscheint nach der Anmeldung als Benachrichtigung.",
            }, null, "Der Test läuft außerhalb von Windows und kann je nach RAM-Größe längere Zeit dauern."),
    };
}

public sealed record PingSummary(string Target, int Sent, int Received, double? MinimumMs, double? AverageMs, double? MaximumMs, double? JitterMs, IReadOnlyList<long?> Samples)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

public sealed record TracerouteHop(int Number, string? Address, long? RoundtripMs, bool TimedOut);

public sealed record DnsTiming(string Host, double? Milliseconds, IReadOnlyList<string> Addresses, string? Error);

/// <summary>Netzwerkdiagnose mit Bordmitteln (ICMP-Echo, DNS). Ziele werden ausschließlich vom Benutzer vorgegeben.</summary>
public static class NetworkDiagnostics
{
    /// <summary>Jitter als mittlere absolute Differenz aufeinanderfolgender Antwortzeiten.</summary>
    public static PingSummary Summarize(string target, IReadOnlyList<long?> samples)
    {
        var received = samples.Where(s => s is not null).Select(s => (double)s!.Value).ToList();
        double? jitter = null;
        if (received.Count >= 2)
        {
            double sum = 0;
            for (var i = 1; i < received.Count; i++) sum += Math.Abs(received[i] - received[i - 1]);
            jitter = sum / (received.Count - 1);
        }
        return new PingSummary(target, samples.Count, received.Count,
            received.Count == 0 ? null : received.Min(), received.Count == 0 ? null : Statistics.Mean(received), received.Count == 0 ? null : received.Max(), jitter, samples);
    }

    public static async Task<PingSummary> PingAsync(string target, int count = 10, int timeoutMs = 2000, IProgress<long?>? progress = null, CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 100);
        var samples = new List<long?>(count);
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long? result = null;
            try
            {
                var reply = await ping.SendPingAsync(target, timeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) result = reply.RoundtripTime;
            }
            catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException or ArgumentException) { }
            samples.Add(result);
            progress?.Report(result);
            if (i < count - 1) await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return Summarize(target, samples);
    }

    public static async Task<IReadOnlyList<TracerouteHop>> TracerouteAsync(string target, int maxHops = 30, int timeoutMs = 2000, IProgress<TracerouteHop>? progress = null, CancellationToken cancellationToken = default)
    {
        var hops = new List<TracerouteHop>();
        IPAddress address;
        try
        {
            address = IPAddress.TryParse(target, out var parsed) ? parsed : (await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false)).First();
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
        {
            return hops;
        }

        using var ping = new Ping();
        var buffer = new byte[32];
        for (var ttl = 1; ttl <= Math.Clamp(maxHops, 1, 64); ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TracerouteHop hop;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(address, timeoutMs, buffer, new PingOptions(ttl, true)).ConfigureAwait(false);
                stopwatch.Stop();
                hop = reply.Status is IPStatus.Success or IPStatus.TtlExpired
                    ? new TracerouteHop(ttl, reply.Address?.ToString(), reply.Status == IPStatus.Success ? reply.RoundtripTime : stopwatch.ElapsedMilliseconds, false)
                    : new TracerouteHop(ttl, null, null, true);
                hops.Add(hop);
                progress?.Report(hop);
                if (reply.Status == IPStatus.Success) break;
            }
            catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
            {
                hop = new TracerouteHop(ttl, null, null, true);
                hops.Add(hop);
                progress?.Report(hop);
            }
        }
        return hops;
    }

    public static async Task<DnsTiming> MeasureDnsAsync(string host, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new DnsTiming(host, stopwatch.Elapsed.TotalMilliseconds, addresses.Select(a => a.ToString()).ToList(), null);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new DnsTiming(host, null, Array.Empty<string>(), "Der Name konnte nicht aufgelöst werden.");
        }
    }

    /// <summary>Lässt nur Hostnamen und IP-Adressen als Ziel zu – keine URLs, Pfade oder Sonderzeichen.</summary>
    public static bool IsValidTarget(string? target)
        => !string.IsNullOrWhiteSpace(target) && target.Length <= 253 && Uri.CheckHostName(target) != UriHostNameType.Unknown;
}
