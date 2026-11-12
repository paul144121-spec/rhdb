using System.Text.Json;
using System.Text.RegularExpressions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Services;

public enum ServiceStartMode
{
    Boot,
    System,
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Unknown,
}

public sealed record ServiceInfo
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; init; } = "";
    public bool IsRunning { get; init; }
    public ServiceStartMode StartMode { get; init; } = ServiceStartMode.Unknown;
    public string? BinaryPath { get; init; }
    public int ProcessId { get; init; }
    public string? Account { get; init; }
    public bool IsDriver { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependents { get; init; } = Array.Empty<string>();
}

public interface IServiceManager
{
    IReadOnlyList<ServiceInfo> GetServices();
    ServiceInfo? GetService(string name);
    void SetStartMode(string name, ServiceStartMode mode);
}

public static class ServiceStartModeExtensions
{
    public static string ToGerman(this ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Boot => "Systemstart (Treiber)",
        ServiceStartMode.System => "System (Treiber)",
        ServiceStartMode.Automatic => "Automatisch",
        ServiceStartMode.AutomaticDelayed => "Automatisch (verzögerter Start)",
        ServiceStartMode.Manual => "Manuell",
        ServiceStartMode.Disabled => "Deaktiviert",
        _ => "Unbekannt",
    };
}

/// <summary>
/// Dienste, deren Startkonfiguration diese Anwendung niemals verändert: Sicherheit, Updates, Authentifizierung,
/// Verschlüsselung, Wiederherstellung sowie Kerninfrastruktur, ohne die Windows nicht startet.
/// </summary>
public static class ServiceProtection
{
    private static readonly Dictionary<string, string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        // Sicherheit
        ["WinDefend"] = "Microsoft Defender Antivirus", ["WdNisSvc"] = "Defender-Netzwerkinspektion", ["Sense"] = "Defender for Endpoint",
        ["SecurityHealthService"] = "Windows-Sicherheit", ["wscsvc"] = "Sicherheitscenter", ["mpssvc"] = "Windows Defender Firewall",
        ["BFE"] = "Basisfiltermodul (Firewall)", ["SgrmBroker"] = "System Guard", ["webthreatdefsvc"] = "Webbedrohungsschutz",
        ["webthreatdefusersvc"] = "Webbedrohungsschutz (Benutzer)", ["AppIDSvc"] = "Anwendungsidentität", ["EventLog"] = "Ereignisprotokoll",
        // Updates und Installation
        ["wuauserv"] = "Windows Update", ["UsoSvc"] = "Update-Orchestrator", ["WaaSMedicSvc"] = "Windows Update Medic", ["BITS"] = "Intelligenter Hintergrundübertragungsdienst",
        ["TrustedInstaller"] = "Windows Modules Installer", ["msiserver"] = "Windows Installer", ["AppXSvc"] = "AppX-Bereitstellung", ["DoSvc"] = "Übermittlungsoptimierung",
        // Authentifizierung und Verschlüsselung
        ["SamSs"] = "Sicherheitskontenverwaltung", ["lsass"] = "Lokale Sicherheitsautorität", ["Netlogon"] = "Anmeldedienst", ["KeyIso"] = "CNG-Schlüsselisolation",
        ["CryptSvc"] = "Kryptografiedienste", ["VaultSvc"] = "Anmeldeinformationsverwaltung", ["BDESVC"] = "BitLocker", ["EFS"] = "Verschlüsselndes Dateisystem",
        ["TPM"] = "TPM", ["tbs"] = "TPM-Basisdienste", ["NgcSvc"] = "Microsoft Passport", ["NgcCtnrSvc"] = "Microsoft Passport-Container", ["wlidsvc"] = "Microsoft-Konto-Anmeldung",
        ["SCardSvr"] = "Smartcard", ["CertPropSvc"] = "Zertifikatverteilung", ["gpsvc"] = "Gruppenrichtlinienclient", ["Appinfo"] = "Anwendungsinformationen (UAC)",
        // Wiederherstellung
        ["VSS"] = "Volumeschattenkopie", ["swprv"] = "Schattenkopie-Anbieter", ["SDRSVC"] = "Windows-Sicherung", ["srservice"] = "Systemwiederherstellung", ["wbengine"] = "Blockebenen-Sicherung",
        // Kerninfrastruktur
        ["RpcSs"] = "Remoteprozeduraufruf", ["RpcEptMapper"] = "RPC-Endpunktzuordnung", ["DcomLaunch"] = "DCOM-Prozessstart", ["LSM"] = "Lokaler Sitzungs-Manager",
        ["Power"] = "Stromversorgung", ["PlugPlay"] = "Plug & Play", ["ProfSvc"] = "Benutzerprofildienst", ["Schedule"] = "Aufgabenplanung", ["SystemEventsBroker"] = "Systemereignisbroker",
        ["BrokerInfrastructure"] = "Infrastruktur für Hintergrundaufgaben", ["CoreMessagingRegistrar"] = "CoreMessaging", ["StateRepository"] = "State Repository", ["UserManager"] = "Benutzer-Manager",
        ["Winmgmt"] = "Windows-Verwaltungsinstrumentation", ["nsi"] = "Netzwerkspeicher-Schnittstelle", ["Dhcp"] = "DHCP-Client", ["Dnscache"] = "DNS-Client", ["NlaSvc"] = "NLA",
        ["netprofm"] = "Netzwerklistendienst", ["LanmanWorkstation"] = "Arbeitsstationsdienst", ["Wcmsvc"] = "Windows-Verbindungs-Manager", ["WlanSvc"] = "Automatische WLAN-Konfiguration",
        ["AudioSrv"] = "Windows-Audio", ["AudioEndpointBuilder"] = "Audio-Endpunkterstellung", ["Themes"] = "Designs", ["DeviceInstall"] = "Geräteinstallation", ["TimeBrokerSvc"] = "Zeitbroker",
        ["TextInputManagementService"] = "Texteingabeverwaltung", ["camsvc"] = "Zugriffs-Manager für Funktionen", ["TokenBroker"] = "Webkonto-Manager", ["W32Time"] = "Windows-Zeitgeber",
    };

    public static bool IsProtected(string serviceName, out string reason)
    {
        // Benutzerspezifische Dienstinstanzen tragen ein Suffix wie "_1a2b3c".
        var baseName = Regex.Replace(serviceName, "_[0-9a-fA-F]{4,8}$", "");
        if (Protected.TryGetValue(serviceName, out var label) || Protected.TryGetValue(baseName, out label))
        {
            reason = $"„{label}“ gehört zu den geschützten Windows-Diensten (Sicherheit, Updates, Anmeldung, Wiederherstellung oder Kerninfrastruktur) und kann hier nicht verändert werden.";
            return true;
        }
        reason = "";
        return false;
    }
}

/// <summary>Hinweis zu einem bekannten, optionalen Windows-Dienst. Dient ausschließlich als Erläuterung – nie als automatische Änderung.</summary>
public sealed record ServiceAdvice(string Name, string Purpose, string WhenSafeToChange, string Consequences, ServiceStartMode SuggestedMode, RiskLevel Risk);

public static class ServiceAdviceCatalog
{
    public static IReadOnlyList<ServiceAdvice> All { get; } = new[]
    {
        new ServiceAdvice("SysMain", "Lädt häufig verwendete Programme vorab in den Arbeitsspeicher (früher „Superfetch“).",
            "Nur sinnvoll bei nachweislich dauerhafter Datenträgerlast durch den Dienst auf Systemen mit Festplatte.",
            "Programme starten nach dem Hochfahren langsamer; auf SSD-Systemen bringt das Deaktivieren in der Regel keinen Vorteil.", ServiceStartMode.Manual, RiskLevel.Moderate),
        new ServiceAdvice("DiagTrack", "Sammelt und überträgt Diagnose- und Nutzungsdaten („Benutzererfahrung und Telemetrie im verbundenen Modus“).",
            "Wenn optionale Diagnosedaten nicht gesendet werden sollen.",
            "Einige Diagnose- und Feedbackfunktionen sowie Insider-Builds funktionieren nicht mehr.", ServiceStartMode.Disabled, RiskLevel.Moderate),
        new ServiceAdvice("Fax", "Ermöglicht das Senden und Empfangen von Faxen.", "Wenn kein Faxmodem verwendet wird.", "Windows-Fax und -Scan kann keine Faxe mehr verarbeiten.", ServiceStartMode.Disabled, RiskLevel.Low),
        new ServiceAdvice("MapsBroker", "Verwaltet heruntergeladene Offlinekarten.", "Wenn die Karten-App nicht verwendet wird.", "Offlinekarten werden nicht mehr aktualisiert.", ServiceStartMode.Disabled, RiskLevel.Low),
        new ServiceAdvice("RetailDemo", "Stellt den Vorführmodus für Verkaufsgeräte bereit.", "Auf privat genutzten Geräten immer.", "Keine.", ServiceStartMode.Disabled, RiskLevel.Low),
        new ServiceAdvice("WSearch", "Indiziert Dateien und E-Mails für die schnelle Windows-Suche.",
            "Nur wenn die Windows-Suche nicht genutzt wird oder der Indexdienst nachweislich dauerhaft Last erzeugt.",
            "Die Suche im Startmenü, im Explorer und in Outlook wird deutlich langsamer oder unvollständig.", ServiceStartMode.Manual, RiskLevel.Moderate),
        new ServiceAdvice("XblAuthManager", "Authentifizierung für Xbox Live.", "Wenn keine Xbox-/Game-Pass-Spiele oder Xbox-Apps genutzt werden.", "Xbox-App, Game Pass und Spiele mit Xbox-Live-Anmeldung funktionieren nicht mehr.", ServiceStartMode.Manual, RiskLevel.Moderate),
        new ServiceAdvice("XblGameSave", "Synchronisiert Spielstände mit Xbox Live.", "Wenn keine Xbox-Live-Spiele genutzt werden.", "Cloud-Spielstände von Xbox-Live-Spielen werden nicht mehr synchronisiert.", ServiceStartMode.Manual, RiskLevel.Moderate),
        new ServiceAdvice("Spooler", "Verwaltet Druckaufträge.", "Wenn weder Drucker noch „Als PDF drucken“ verwendet werden.", "Drucken – auch in PDF – ist nicht mehr möglich.", ServiceStartMode.Manual, RiskLevel.Moderate),
        new ServiceAdvice("RemoteRegistry", "Erlaubt Remotezugriff auf die Registrierung.", "Auf Einzelplatzrechnern ohne Fernverwaltung.", "Fernverwaltungswerkzeuge können die Registrierung nicht mehr lesen.", ServiceStartMode.Disabled, RiskLevel.Low),
        new ServiceAdvice("WerSvc", "Meldet Programmabstürze an Microsoft.", "Wenn keine Fehlerberichte gesendet werden sollen.", "Absturzberichte und daraus abgeleitete Lösungsvorschläge entfallen.", ServiceStartMode.Manual, RiskLevel.Low),
        new ServiceAdvice("PhoneSvc", "Verwaltet den Telefoniestatus des Geräts.", "Wenn „Smartphone-Link“ nicht genutzt wird.", "Telefoniefunktionen von Smartphone-Link stehen nicht zur Verfügung.", ServiceStartMode.Manual, RiskLevel.Low),
    };

    public static ServiceAdvice? Find(string name) => All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Ändert den Starttyp eines nicht geschützten Windows-Dienstes. Erfordert Administratorrechte; wird nie automatisch ausgeführt.</summary>
public sealed class ServiceStartupAction : OptimizationActionBase
{
    public const string ActionId = "service.startup.set";
    private static readonly string[] AllowedModes = { nameof(ServiceStartMode.Automatic), nameof(ServiceStartMode.AutomaticDelayed), nameof(ServiceStartMode.Manual), nameof(ServiceStartMode.Disabled) };
    private readonly IServiceManager _services;

    public ServiceStartupAction(IServiceManager services) => _services = services;

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "Starttyp eines Dienstes ändern",
        Description = "Legt fest, ob ein Windows-Dienst automatisch, verzögert, manuell oder gar nicht gestartet wird.",
        Category = ActionCategory.Services,
        Risk = RiskLevel.Moderate,
        RequiredPrivilege = PrivilegeLevel.Administrator,
        Restart = RestartRequirement.Reboot,
        ExpectedEffect = "Der Dienst wird ab dem nächsten Systemstart gemäß dem neuen Starttyp behandelt. Ein laufender Dienst wird nicht beendet.",
        SideEffects = "Funktionen, die den Dienst benötigen, können ausfallen. Abhängige Dienste starten möglicherweise nicht mehr.",
        BackupStrategy = "Der bisherige Starttyp (einschließlich „verzögert“) wird im Transaktionsjournal gespeichert.",
        RollbackStrategy = "Der gesicherte Starttyp wird wiederhergestellt.",
        VerificationStrategy = "Die Dienstkonfiguration wird nach der Änderung erneut über den Dienststeuerungs-Manager gelesen.",
        ExcludeFromAutomation = true,
        Parameters = new[]
        {
            new ParameterSpec("service", ParameterType.Text, "Interner Dienstname", Pattern: @"^[A-Za-z0-9_.\-$ ]{1,256}$", MaxLength: 256, IsTargetKey: true),
            new ParameterSpec("mode", ParameterType.Choice, "Neuer Starttyp", Choices: AllowedModes),
        },
        Tags = new[] { "services" },
    };

    private static ServiceStartMode Mode(ActionParameters parameters) => Enum.Parse<ServiceStartMode>(parameters.RequireString("mode"), ignoreCase: true);

    public override ValidationResult ValidateParameters(ActionParameters parameters)
    {
        var result = base.ValidateParameters(parameters);
        if (!result.IsValid) return result;
        return ServiceProtection.IsProtected(parameters.RequireString("service"), out var reason) ? ValidationResult.Fail(reason) : ValidationResult.Ok;
    }

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        var service = _services.GetService(parameters.RequireString("service"));
        if (service is null) return SupportResult.NotSupported("Der Dienst ist auf diesem System nicht installiert.");
        if (service.IsDriver || service.StartMode is ServiceStartMode.Boot or ServiceStartMode.System)
            return SupportResult.NotSupported("Treiberdienste werden von dieser Anwendung nicht verändert.");
        return SupportResult.Supported;
    }

    public override RiskAssessment EvaluateRisk(ActionParameters parameters, SystemContext context)
    {
        var name = parameters.RequireString("service");
        var mode = Mode(parameters);
        var service = _services.GetService(name);
        var advice = ServiceAdviceCatalog.Find(name);
        var reasons = new List<string>();
        var level = advice?.Risk ?? RiskLevel.Moderate;

        if (advice is not null) reasons.Add(advice.Consequences);
        else reasons.Add("Für diesen Dienst liegt keine geprüfte Empfehlung vor. Die Auswirkungen sind nicht bekannt.");

        if (mode == ServiceStartMode.Disabled)
        {
            if (advice is null) level = RiskLevel.High;
            var running = service?.Dependents.Count ?? 0;
            if (running > 0)
            {
                level = (RiskLevel)Math.Max((int)level, (int)RiskLevel.High);
                reasons.Add($"{running} andere(r) Dienst(e) hängen von diesem Dienst ab und starten dann nicht mehr: {string.Join(", ", service!.Dependents.Take(5))}.");
            }
        }
        else if (mode is ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed)
        {
            level = advice is null ? RiskLevel.Moderate : RiskLevel.Low;
        }
        return new RiskAssessment(level, reasons);
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var service = _services.GetService(parameters.RequireString("service"));
        if (service is null) return Task.FromResult(ActionState.Unavailable("Dienst nicht gefunden."));
        return Task.FromResult(new ActionState
        {
            DisplayValue = $"{service.StartMode.ToGerman()} ({service.State})",
            RawValue = service.StartMode.ToString(),
            IsApplied = service.StartMode == Mode(parameters),
        });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var name = parameters.RequireString("service");
        var service = _services.GetService(name);
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        var advice = ServiceAdviceCatalog.Find(name);
        return new ChangePreview
        {
            ActionId = ActionId,
            Title = $"Dienst „{service?.DisplayName ?? name}“",
            CurrentValue = state.DisplayValue,
            NewValue = Mode(parameters).ToGerman(),
            WillChange = state.IsApplied != true,
            Note = advice is null ? "Für diesen Dienst liegt keine geprüfte Empfehlung vor." : $"Zweck: {advice.Purpose} Folgen: {advice.Consequences}",
            TechnicalDetails = new[]
            {
                $"Dienstname: {name}",
                $"Abhängig von: {(service?.DependsOn.Count > 0 ? string.Join(", ", service.DependsOn) : "–")}",
                $"Abhängige Dienste: {(service?.Dependents.Count > 0 ? string.Join(", ", service.Dependents) : "–")}",
                $"Ausführbare Datei: {service?.BinaryPath ?? "unbekannt"}",
            },
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var service = _services.GetService(parameters.RequireString("service")) ?? throw new InvalidOperationException("Dienst nicht gefunden.");
        var payload = new ServiceBackup(service.Name, service.StartMode.ToString(), Mode(parameters).ToString());
        return Task.FromResult(new BackupData(ActionId, JsonSerializer.Serialize(payload), service.StartMode.ToGerman(), Mode(parameters).ToGerman()));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var name = parameters.RequireString("service");
        try
        {
            _services.SetStartMode(name, Mode(parameters));
            return Task.FromResult(OperationResult.Ok($"Starttyp von „{name}“ auf „{Mode(parameters).ToGerman()}“ gesetzt."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(OperationResult.Fail("Der Starttyp konnte nicht geändert werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var service = _services.GetService(parameters.RequireString("service"));
        var expected = Mode(parameters);
        if (service is null) return Task.FromResult(VerificationResult.Failure(expected.ToGerman(), "Dienst nicht gefunden"));
        return Task.FromResult(service.StartMode == expected
            ? VerificationResult.Success(expected.ToGerman(), service.StartMode.ToGerman())
            : VerificationResult.Failure(expected.ToGerman(), service.StartMode.ToGerman()));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        var validation = ValidateParameters(parameters);
        if (!validation.IsValid) return Task.FromResult(RollbackResult.Fail(validation.ErrorText));
        var name = parameters.RequireString("service");

        ServiceBackup? payload;
        try { payload = JsonSerializer.Deserialize<ServiceBackup>(backup.PayloadJson); }
        catch (JsonException) { payload = null; }
        // Der Dienstname stammt aus den validierten Parametern; die Sicherung muss dazu passen.
        if (payload is null || !string.Equals(payload.Service, name, StringComparison.OrdinalIgnoreCase)
            || !Enum.TryParse<ServiceStartMode>(payload.Previous, out var previous) || !AllowedModes.Contains(previous.ToString())
            || !Enum.TryParse<ServiceStartMode>(payload.Applied, out var applied))
            return Task.FromResult(RollbackResult.Fail("Die Sicherungsdaten sind beschädigt oder gehören zu einem anderen Dienst."));

        var service = _services.GetService(name);
        if (service is null) return Task.FromResult(RollbackResult.Fail("Der Dienst existiert nicht mehr."));
        if (service.StartMode == previous) return Task.FromResult(RollbackResult.Ok("Der ursprüngliche Starttyp liegt bereits vor."));
        if (!force && service.StartMode != applied)
            return Task.FromResult(RollbackResult.Conflict("Der Starttyp wurde seit der Optimierung erneut geändert. Wiederherstellung nur nach ausdrücklicher Bestätigung."));

        try
        {
            _services.SetStartMode(name, previous);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(RollbackResult.Fail("Der ursprüngliche Starttyp konnte nicht wiederhergestellt werden.", ex.Message));
        }

        return Task.FromResult(_services.GetService(name)?.StartMode == previous
            ? RollbackResult.Ok($"Starttyp „{previous.ToGerman()}“ wiederhergestellt.")
            : RollbackResult.Fail("Die Wiederherstellung des Starttyps konnte nicht bestätigt werden."));
    }

    private sealed record ServiceBackup(string Service, string Previous, string Applied);
}
