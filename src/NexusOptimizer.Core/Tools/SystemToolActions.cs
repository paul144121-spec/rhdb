using System.Collections.Concurrent;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Tools;

/// <summary>Fest definierte Windows-Bordwerkzeuge. Es gibt keinen Weg, ein anderes Programm oder eine Shell zu starten.</summary>
public enum SystemTool
{
    Sfc,
    Dism,
    Chkdsk,
    Defrag,
    IpConfig,
    Netsh,
    PowerCfg,
}

public sealed record ToolResult(int ExitCode, string Output, bool TimedOut, TimeSpan Duration)
{
    public bool Started { get; init; } = true;
}

/// <summary>
/// Startet ein Bordwerkzeug mit einer Argumentliste (ohne Shell, ohne Zeichenkettenverkettung).
/// Die Implementierung löst das Werkzeug auf einen absoluten Pfad unter %SystemRoot%\System32 auf.
/// </summary>
public interface ISystemToolRunner
{
    Task<ToolResult> RunAsync(SystemTool tool, IReadOnlyList<string> arguments, IProgress<string>? output, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed record SystemToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ActionCategory Category { get; init; }
    public required SystemTool Tool { get; init; }
    /// <summary>Feste Argumente; „{drive}“ wird durch den validierten Laufwerksparameter ersetzt.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }
    public bool TakesDrive { get; init; }
    public PrivilegeLevel Privilege { get; init; } = PrivilegeLevel.Administrator;
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public RestartRequirement Restart { get; init; } = RestartRequirement.None;
    public string ExpectedEffect { get; init; } = "";
    public string SideEffects { get; init; } = "";
    public string DurationHint { get; init; } = "Die Dauer hängt vom System ab und lässt sich nicht verlässlich vorhersagen.";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(90);
    public IReadOnlyList<int> SuccessExitCodes { get; init; } = new[] { 0 };
    /// <summary>Werkzeuge ohne bleibende Systemänderung (reine Prüfungen) dürfen ohne Einzelbestätigung laufen.</summary>
    public bool ReadOnly { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

public static class SystemToolCatalog
{
    public const string SfcScan = "repair.sfc.scan";
    public const string DismCheckHealth = "repair.dism.checkhealth";
    public const string DismScanHealth = "repair.dism.scanhealth";
    public const string DismRestoreHealth = "repair.dism.restorehealth";
    public const string DismComponentCleanup = "cleanup.component-store";
    public const string ChkdskScan = "repair.chkdsk.scan";
    public const string DriveOptimize = "storage.optimize";
    public const string DriveRetrim = "storage.retrim";
    public const string DnsFlush = "network.dns.flush";
    public const string WinsockReset = "network.winsock.reset";

    public static IReadOnlyList<SystemToolDefinition> All { get; } = new[]
    {
        new SystemToolDefinition
        {
            Id = SfcScan, Name = "Systemdateien prüfen und reparieren (SFC)", Category = ActionCategory.Repair, Tool = SystemTool.Sfc, Arguments = new[] { "/scannow" },
            Description = "Die Systemdateiprüfung vergleicht geschützte Windows-Dateien mit dem Komponentenspeicher und ersetzt beschädigte Dateien.",
            ExpectedEffect = "Beschädigte Systemdateien werden – soweit möglich – aus dem Komponentenspeicher wiederhergestellt.",
            SideEffects = "Hohe Datenträger- und CPU-Last während der Prüfung. Manuell veränderte Systemdateien werden zurückgesetzt.",
            DurationHint = "Dauert je nach Datenträger typischerweise mehrere Minuten bis deutlich länger.", Tags = new[] { "repair" },
        },
        new SystemToolDefinition
        {
            Id = DismCheckHealth, Name = "Komponentenspeicher: Schnellprüfung (DISM CheckHealth)", Category = ActionCategory.Repair, Tool = SystemTool.Dism,
            Arguments = new[] { "/Online", "/Cleanup-Image", "/CheckHealth" }, ReadOnly = true, Risk = RiskLevel.VeryLow,
            Description = "Prüft, ob Windows den Komponentenspeicher bereits als beschädigt markiert hat. Es wird nichts verändert.",
            ExpectedEffect = "Meldet, ob eine Beschädigung vermerkt ist.", SideEffects = "Keine.", DurationHint = "In der Regel wenige Sekunden.", Timeout = TimeSpan.FromMinutes(10), Tags = new[] { "repair" },
        },
        new SystemToolDefinition
        {
            Id = DismScanHealth, Name = "Komponentenspeicher: vollständige Prüfung (DISM ScanHealth)", Category = ActionCategory.Repair, Tool = SystemTool.Dism,
            Arguments = new[] { "/Online", "/Cleanup-Image", "/ScanHealth" }, ReadOnly = true, Risk = RiskLevel.VeryLow,
            Description = "Durchsucht den Komponentenspeicher vollständig nach Beschädigungen. Es wird nichts repariert.",
            ExpectedEffect = "Meldet gefundene Beschädigungen des Komponentenspeichers.", SideEffects = "Erhöhte Datenträgerlast während der Prüfung.",
            DurationHint = "Kann mehrere Minuten oder länger dauern.", Tags = new[] { "repair" },
        },
        new SystemToolDefinition
        {
            Id = DismRestoreHealth, Name = "Komponentenspeicher reparieren (DISM RestoreHealth)", Category = ActionCategory.Repair, Tool = SystemTool.Dism,
            Arguments = new[] { "/Online", "/Cleanup-Image", "/RestoreHealth" },
            Description = "Repariert den Komponentenspeicher. Fehlende Dateien werden bei Bedarf über Windows Update nachgeladen.",
            ExpectedEffect = "Ein beschädigter Komponentenspeicher wird repariert, sodass SFC und Updates wieder funktionieren.",
            SideEffects = "Benötigt in der Regel eine Internetverbindung und freien Speicherplatz; hohe Systemlast während der Reparatur.",
            DurationHint = "Kann je nach Schaden und Verbindung lange dauern.", Tags = new[] { "repair" },
        },
        new SystemToolDefinition
        {
            Id = DismComponentCleanup, Name = "Komponentenspeicher bereinigen (ersetzte Update-Dateien)", Category = ActionCategory.Cleanup, Tool = SystemTool.Dism,
            Arguments = new[] { "/Online", "/Cleanup-Image", "/StartComponentCleanup" },
            Description = "Entfernt durch neuere Updates ersetzte Komponentenversionen aus dem Komponentenspeicher (WinSxS) über das dafür vorgesehene Windows-Werkzeug.",
            ExpectedEffect = "Gibt Speicherplatz frei, der von überholten Update-Komponenten belegt wird.",
            SideEffects = "Hohe Systemlast während der Bereinigung. Bereits ersetzte Updates lassen sich danach teilweise nicht mehr deinstallieren.",
            DurationHint = "Kann mehrere Minuten oder länger dauern.", Tags = new[] { "cleanup" },
        },
        new SystemToolDefinition
        {
            Id = ChkdskScan, Name = "Dateisystem online prüfen (CHKDSK /scan)", Category = ActionCategory.Repair, Tool = SystemTool.Chkdsk, Arguments = new[] { "{drive}", "/scan" },
            TakesDrive = true, ReadOnly = true, Risk = RiskLevel.VeryLow,
            Description = "Prüft ein NTFS-Laufwerk im laufenden Betrieb auf Dateisystemfehler. Gefundene Probleme werden zur späteren Reparatur vorgemerkt, das Laufwerk bleibt verfügbar.",
            ExpectedEffect = "Meldet den Zustand des Dateisystems.", SideEffects = "Erhöhte Datenträgerlast während der Prüfung.",
            DurationHint = "Abhängig von Größe und Belegung des Laufwerks.", Tags = new[] { "repair", "storage" },
        },
        new SystemToolDefinition
        {
            Id = DriveOptimize, Name = "Laufwerk optimieren (medienabhängig)", Category = ActionCategory.Storage, Tool = SystemTool.Defrag, Arguments = new[] { "{drive}", "/O" },
            TakesDrive = true,
            Description = "Führt die von Windows für den jeweiligen Datenträgertyp vorgesehene Optimierung aus: TRIM bei SSDs, Defragmentierung nur bei Festplatten.",
            ExpectedEffect = "SSDs erhalten ein erneutes TRIM, Festplatten werden defragmentiert.",
            SideEffects = "Bei Festplatten lange Laufzeit und hohe Datenträgerlast. SSDs werden nicht defragmentiert.",
            DurationHint = "Bei SSDs meist kurz, bei Festplatten abhängig von Größe und Fragmentierung.", Tags = new[] { "storage" },
        },
        new SystemToolDefinition
        {
            Id = DriveRetrim, Name = "TRIM für SSD erneut senden", Category = ActionCategory.Storage, Tool = SystemTool.Defrag, Arguments = new[] { "{drive}", "/L" },
            TakesDrive = true, Risk = RiskLevel.VeryLow,
            Description = "Teilt der SSD mit, welche Blöcke nicht mehr belegt sind (Retrim). Windows erledigt das normalerweise automatisch im Wartungsplan.",
            ExpectedEffect = "Die SSD kann freie Blöcke intern bereinigen.", SideEffects = "Kurzzeitig erhöhte Datenträgeraktivität.",
            DurationHint = "In der Regel unter einer Minute.", Timeout = TimeSpan.FromMinutes(30), Tags = new[] { "storage" },
        },
        new SystemToolDefinition
        {
            Id = DnsFlush, Name = "DNS-Cache leeren", Category = ActionCategory.Network, Tool = SystemTool.IpConfig, Arguments = new[] { "/flushdns" },
            Privilege = PrivilegeLevel.User, Risk = RiskLevel.VeryLow, ReadOnly = true,
            Description = "Verwirft zwischengespeicherte DNS-Antworten. Hilft bei veralteten oder fehlerhaften Namensauflösungen.",
            ExpectedEffect = "Namen werden beim nächsten Zugriff neu aufgelöst.", SideEffects = "Die ersten Verbindungen danach benötigen eine erneute DNS-Abfrage.",
            DurationHint = "Sofort.", Timeout = TimeSpan.FromMinutes(1), Tags = new[] { "network" },
        },
        new SystemToolDefinition
        {
            Id = WinsockReset, Name = "Winsock-Katalog zurücksetzen", Category = ActionCategory.Network, Tool = SystemTool.Netsh, Arguments = new[] { "winsock", "reset" },
            Risk = RiskLevel.Moderate, Restart = RestartRequirement.Reboot,
            Description = "Setzt den Winsock-Katalog auf den Auslieferungszustand zurück. Behebt Netzwerkprobleme, die durch fehlerhafte Netzwerkfilter (LSPs) verursacht werden.",
            ExpectedEffect = "Der Netzwerkstapel verwendet wieder die Standardanbieter.",
            SideEffects = "Software, die eigene Netzwerkfilter installiert hat (manche VPN-, Sicherheits- oder Proxyprogramme), muss möglicherweise neu installiert werden. Nicht umkehrbar.",
            DurationHint = "Sofort; wirksam nach Neustart.", Timeout = TimeSpan.FromMinutes(2), Tags = new[] { "network", "repair" },
        },
    };

    public static SystemToolDefinition? Find(string id) => All.FirstOrDefault(d => d.Id == id);
}

/// <summary>Ergebnisprotokoll eines Werkzeuglaufs zur Anzeige in der Oberfläche.</summary>
public sealed record ToolRunReport(string ActionId, DateTimeOffset FinishedAt, int ExitCode, bool Success, bool TimedOut, TimeSpan Duration, string Output);

/// <summary>
/// Aktion für ein fest definiertes Bordwerkzeug. Nicht umkehrbar; meldet Erfolg ausschließlich anhand des tatsächlichen Rückgabecodes.
/// </summary>
public sealed class SystemToolAction : OptimizationActionBase
{
    private readonly ISystemToolRunner _runner;
    private readonly Func<string, bool>? _isSolidState;
    private readonly ConcurrentDictionary<string, ToolRunReport> _lastRuns = new(StringComparer.OrdinalIgnoreCase);

    public SystemToolDefinition Definition { get; }
    public override ActionMetadata Metadata { get; }

    /// <param name="isSolidState">Liefert für ein Laufwerk („C:“), ob es eine SSD ist; für die TRIM-Prüfung.</param>
    public SystemToolAction(SystemToolDefinition definition, ISystemToolRunner runner, Func<string, bool>? isSolidState = null)
    {
        Definition = definition;
        _runner = runner;
        _isSolidState = isSolidState;
        Metadata = new ActionMetadata
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            Category = definition.Category,
            Risk = definition.Risk,
            RequiredPrivilege = definition.Privilege,
            Restart = definition.Restart,
            IsReversible = false,
            IsDiagnosticOnly = definition.ReadOnly,
            ExcludeFromAutomation = true,
            ExpectedEffect = definition.ExpectedEffect,
            SideEffects = definition.SideEffects,
            BackupStrategy = definition.ReadOnly ? "Nicht erforderlich – es wird nichts dauerhaft verändert." : "Keine Sicherung möglich. Diese Aktion kann nicht automatisch rückgängig gemacht werden.",
            RollbackStrategy = definition.ReadOnly ? "Nicht erforderlich." : "Nicht umkehrbar.",
            VerificationStrategy = "Der Rückgabecode des Windows-Werkzeugs wird ausgewertet und die vollständige Ausgabe protokolliert.",
            Parameters = definition.TakesDrive ? new[] { new ParameterSpec("drive", ParameterType.DriveLetter, "Laufwerk, z. B. C:", IsTargetKey: true) } : Array.Empty<ParameterSpec>(),
            Tags = definition.Tags,
            TechnicalDetails = new[]
            {
                $"Werkzeug: {definition.Tool} {string.Join(' ', definition.Arguments)}",
                definition.DurationHint,
            },
        };
    }

    public ToolRunReport? GetLastRun(ActionParameters parameters) => _lastRuns.TryGetValue(parameters.ToJson(), out var report) ? report : null;

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        if (Definition.Id == SystemToolCatalog.DriveRetrim && _isSolidState is not null && parameters.GetString("drive") is { } drive && !_isSolidState(drive))
            return SupportResult.NotSupported("TRIM ist nur für SSDs sinnvoll. Für Festplatten verwenden Sie „Laufwerk optimieren“.");
        return SupportResult.Supported;
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var last = GetLastRun(parameters);
        return Task.FromResult(new ActionState
        {
            DisplayValue = last is null ? "Noch nicht ausgeführt (in dieser Sitzung)" : $"Zuletzt {last.FinishedAt.ToLocalTime():g}: {(last.Success ? "erfolgreich" : $"Fehler (Code {last.ExitCode})")}",
            IsApplied = false,
        });
    }

    public override Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChangePreview
        {
            ActionId = Metadata.Id,
            Title = Metadata.Name + (parameters.GetString("drive") is { } drive ? $" ({drive.ToUpperInvariant()})" : ""),
            CurrentValue = "–",
            NewValue = Definition.ExpectedEffect,
            WillChange = true,
            Note = Definition.DurationHint,
            TechnicalDetails = Metadata.TechnicalDetails,
        });

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        return Task.FromResult(new BackupData(Metadata.Id, "{}", "–", Definition.ExpectedEffect));
    }

    private IReadOnlyList<string> BuildArguments(ActionParameters parameters)
    {
        var drive = parameters.GetString("drive")?.ToUpperInvariant();
        return Definition.Arguments.Select(a => a == "{drive}" ? drive ?? throw new ArgumentException("Laufwerk fehlt.") : a).ToArray();
    }

    public override async Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        ToolResult result;
        try
        {
            result = await _runner.RunAsync(Definition.Tool, BuildArguments(parameters), progress, Definition.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastRuns[parameters.ToJson()] = new ToolRunReport(Metadata.Id, DateTimeOffset.UtcNow, -1, false, false, TimeSpan.Zero, ex.Message);
            return OperationResult.Fail("Das Windows-Werkzeug konnte nicht gestartet werden.", ex.Message);
        }

        var success = result.Started && !result.TimedOut && Definition.SuccessExitCodes.Contains(result.ExitCode);
        _lastRuns[parameters.ToJson()] = new ToolRunReport(Metadata.Id, DateTimeOffset.UtcNow, result.ExitCode, success, result.TimedOut, result.Duration, result.Output);

        if (result.TimedOut) return OperationResult.Fail("Das Werkzeug wurde wegen Zeitüberschreitung abgebrochen.", Tail(result.Output));
        return success
            ? OperationResult.Ok($"Abgeschlossen mit Rückgabecode {result.ExitCode} nach {result.Duration.TotalSeconds:F0} s.")
            : OperationResult.Fail($"Das Werkzeug meldete den Rückgabecode {result.ExitCode}.", Tail(result.Output));
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var last = GetLastRun(parameters);
        if (last is null) return Task.FromResult(VerificationResult.Failure("Rückgabecode 0", "kein Lauf protokolliert"));
        return Task.FromResult(last.Success
            ? new VerificationResult(true, "Erfolgreicher Rückgabecode", $"Rückgabecode {last.ExitCode}", "Das Werkzeug hat den Vorgang als erfolgreich gemeldet.")
            : VerificationResult.Failure("Erfolgreicher Rückgabecode", $"Rückgabecode {last.ExitCode}"));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
        => Task.FromResult(Definition.ReadOnly ? RollbackResult.Ok("Es wurde nichts verändert – keine Wiederherstellung nötig.") : NotReversible());

    private static string Tail(string output) => output.Length <= 2000 ? output : "…" + output[^2000..];
}
