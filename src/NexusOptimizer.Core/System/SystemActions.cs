using System.Text.Json;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.SystemAccess;

// ───────────────────────── Netzwerk: DNS ─────────────────────────

public sealed record AdapterDnsInfo(string AdapterId, string Name, bool IsAutomatic, IReadOnlyList<string> Servers);

public interface INetworkConfigurator
{
    IReadOnlyList<AdapterDnsInfo> GetAdapters();
    /// <summary>Setzt statische IPv4-DNS-Server; null oder leer stellt auf automatischen Bezug (DHCP) zurück.</summary>
    void SetDnsServers(string adapterId, IReadOnlyList<string>? servers);
}

/// <summary>Setzt die IPv4-DNS-Server eines Netzwerkadapters. Die bisherige Konfiguration wird gesichert.</summary>
public sealed class DnsSetAction : OptimizationActionBase
{
    public const string ActionId = "network.dns.set";
    private readonly INetworkConfigurator _network;

    public DnsSetAction(INetworkConfigurator network) => _network = network;

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "DNS-Server eines Adapters ändern",
        Description = "Trägt für einen Netzwerkadapter feste IPv4-DNS-Server ein oder stellt auf automatischen Bezug zurück.",
        Category = ActionCategory.Network,
        Risk = RiskLevel.Moderate,
        RequiredPrivilege = PrivilegeLevel.Administrator,
        ExpectedEffect = "Namensauflösungen dieses Adapters laufen über die angegebenen Server. Ein anderer DNS-Server verbessert die Latenz in Spielen in der Regel nicht – er beeinflusst nur die Namensauflösung.",
        SideEffects = "Bei falschen oder nicht erreichbaren Servern funktionieren Internetzugriffe nicht mehr. Firmen-, Kinderschutz- oder Router-Filter, die auf dem bisherigen DNS-Server beruhen, werden umgangen.",
        BackupStrategy = "Die bisherigen DNS-Server und die Information, ob sie automatisch bezogen wurden, werden im Transaktionsjournal gespeichert.",
        RollbackStrategy = "Die gesicherte DNS-Konfiguration des Adapters wird wiederhergestellt.",
        VerificationStrategy = "Die DNS-Konfiguration des Adapters wird nach der Änderung erneut gelesen.",
        ExcludeFromAutomation = true,
        Parameters = new[]
        {
            new ParameterSpec("adapter", ParameterType.Text, "Adapter-GUID", Pattern: @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", MaxLength: 38, IsTargetKey: true),
            new ParameterSpec("servers", ParameterType.IpAddressList, "IPv4-DNS-Server, kommagetrennt (leer lassen für automatischen Bezug)", Required: false),
        },
        Tags = new[] { "network" },
    };

    private static string[]? Servers(ActionParameters p) => p.GetString("servers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override ValidationResult ValidateParameters(ActionParameters parameters)
    {
        var result = base.ValidateParameters(parameters);
        if (!result.IsValid) return result;
        var servers = Servers(parameters);
        if (servers is not null && servers.Any(s => s.Contains(':'))) return ValidationResult.Fail("Es werden derzeit nur IPv4-DNS-Server unterstützt.");
        return ValidationResult.Ok;
    }

    private AdapterDnsInfo? Adapter(ActionParameters p)
        => _network.GetAdapters().FirstOrDefault(a => string.Equals(a.AdapterId, p.RequireString("adapter"), StringComparison.OrdinalIgnoreCase));

    private static string Describe(bool automatic, IReadOnlyList<string> servers)
        => automatic ? "Automatisch (DHCP)" + (servers.Count > 0 ? $" – aktuell {string.Join(", ", servers)}" : "") : string.Join(", ", servers);

    private static bool Matches(AdapterDnsInfo adapter, string[]? wanted)
        => wanted is null or { Length: 0 } ? adapter.IsAutomatic : !adapter.IsAutomatic && adapter.Servers.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase);

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        return Adapter(parameters) is null ? SupportResult.NotSupported("Der Netzwerkadapter wurde nicht gefunden.") : SupportResult.Supported;
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var adapter = Adapter(parameters);
        if (adapter is null) return Task.FromResult(ActionState.Unavailable("Adapter nicht gefunden."));
        return Task.FromResult(new ActionState { DisplayValue = Describe(adapter.IsAutomatic, adapter.Servers), IsApplied = Matches(adapter, Servers(parameters)) });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var adapter = Adapter(parameters);
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        var servers = Servers(parameters);
        return new ChangePreview
        {
            ActionId = ActionId,
            Title = $"DNS-Server für „{adapter?.Name ?? "Adapter"}“",
            CurrentValue = state.DisplayValue,
            NewValue = servers is null or { Length: 0 } ? "Automatisch (DHCP)" : string.Join(", ", servers),
            WillChange = state.IsApplied != true,
            Note = "Hinweis: Ein DNS-Wechsel senkt die Latenz in Spielen normalerweise nicht.",
            TechnicalDetails = new[] { $"Adapter-GUID: {parameters.RequireString("adapter")}", "Betrifft nur IPv4; IPv6-DNS bleibt unverändert." },
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var adapter = Adapter(parameters) ?? throw new InvalidOperationException("Adapter nicht gefunden.");
        var servers = Servers(parameters) ?? Array.Empty<string>();
        var payload = new DnsBackup(adapter.AdapterId, adapter.IsAutomatic, adapter.Servers.ToArray(), servers);
        return Task.FromResult(new BackupData(ActionId, JsonSerializer.Serialize(payload), Describe(adapter.IsAutomatic, adapter.Servers), servers.Length == 0 ? "Automatisch (DHCP)" : string.Join(", ", servers)));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        try
        {
            _network.SetDnsServers(parameters.RequireString("adapter"), Servers(parameters));
            return Task.FromResult(OperationResult.Ok("DNS-Konfiguration geschrieben."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return Task.FromResult(OperationResult.Fail("Die DNS-Konfiguration konnte nicht geändert werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var adapter = Adapter(parameters);
        var servers = Servers(parameters);
        var expected = servers is null or { Length: 0 } ? "Automatisch (DHCP)" : string.Join(", ", servers);
        if (adapter is null) return Task.FromResult(VerificationResult.Failure(expected, "Adapter nicht gefunden"));
        var actual = Describe(adapter.IsAutomatic, adapter.Servers);
        return Task.FromResult(Matches(adapter, servers) ? VerificationResult.Success(expected, actual) : VerificationResult.Failure(expected, actual));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        var validation = ValidateParameters(parameters);
        if (!validation.IsValid) return Task.FromResult(RollbackResult.Fail(validation.ErrorText));

        DnsBackup? payload;
        try { payload = JsonSerializer.Deserialize<DnsBackup>(backup.PayloadJson); }
        catch (JsonException) { payload = null; }
        if (payload is null || !string.Equals(payload.AdapterId, parameters.RequireString("adapter"), StringComparison.OrdinalIgnoreCase)
            || payload.Previous is null || payload.Previous.Length > 4 || payload.Previous.Any(s => !Security.ParameterValidator.IsPlainIpAddress(s)))
            return Task.FromResult(RollbackResult.Fail("Die Sicherungsdaten sind beschädigt oder gehören zu einem anderen Adapter."));

        var adapter = Adapter(parameters);
        if (adapter is null) return Task.FromResult(RollbackResult.Fail("Der Netzwerkadapter existiert nicht mehr."));

        var original = payload.WasAutomatic ? null : payload.Previous.Where(s => !s.Contains(':')).ToArray();
        if (Matches(adapter, original)) return Task.FromResult(RollbackResult.Ok("Die ursprüngliche DNS-Konfiguration liegt bereits vor."));
        if (!force && !Matches(adapter, payload.Applied))
            return Task.FromResult(RollbackResult.Conflict("Die DNS-Konfiguration wurde seit der Optimierung erneut geändert. Wiederherstellung nur nach ausdrücklicher Bestätigung."));

        try
        {
            _network.SetDnsServers(payload.AdapterId, original);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return Task.FromResult(RollbackResult.Fail("Die ursprüngliche DNS-Konfiguration konnte nicht wiederhergestellt werden.", ex.Message));
        }
        return Task.FromResult(RollbackResult.Ok("Ursprüngliche DNS-Konfiguration wiederhergestellt."));
    }

    private sealed record DnsBackup(string AdapterId, bool WasAutomatic, string[] Previous, string[] Applied);
}

// ───────────────────────── Systemwiederherstellung ─────────────────────────

public sealed record RestorePointResult(bool Success, string Message);

public interface ISystemRestore
{
    /// <summary>null, wenn der Status nicht ermittelt werden kann.</summary>
    bool? IsProtectionEnabled();
    RestorePointResult CreateRestorePoint(string description);
}

/// <summary>Legt einen Windows-Wiederherstellungspunkt an. Der tatsächliche Erfolg wird gemeldet – nie angenommen.</summary>
public sealed class RestorePointAction : OptimizationActionBase
{
    public const string ActionId = "backup.restorepoint.create";
    private readonly ISystemRestore _restore;
    private RestorePointResult? _last;

    public RestorePointAction(ISystemRestore restore) => _restore = restore;

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "Wiederherstellungspunkt erstellen",
        Description = "Erstellt über die Windows-Systemwiederherstellung einen Wiederherstellungspunkt als zusätzliche Absicherung.",
        Category = ActionCategory.Backup,
        Risk = RiskLevel.VeryLow,
        RequiredPrivilege = PrivilegeLevel.Administrator,
        IsReversible = false,
        IsDiagnosticOnly = true,
        ExcludeFromAutomation = true,
        ExpectedEffect = "Ein Wiederherstellungspunkt steht in der Windows-Systemwiederherstellung zur Verfügung.",
        SideEffects = "Belegt Speicherplatz im Schattenkopiespeicher. Windows legt standardmäßig höchstens einen Wiederherstellungspunkt pro 24 Stunden an; häufigere Anforderungen werden von Windows stillschweigend übersprungen.",
        BackupStrategy = "Nicht zutreffend.",
        RollbackStrategy = "Nicht zutreffend – Wiederherstellungspunkte verwaltet Windows.",
        VerificationStrategy = "Der Rückgabewert der Systemwiederherstellung wird ausgewertet.",
        Parameters = new[] { new ParameterSpec("description", ParameterType.Text, "Beschreibung", Required: false, Pattern: @"^[\w \-.,:()äöüÄÖÜß]{1,120}$", MaxLength: 120) },
        Tags = new[] { "backup" },
    };

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        return _restore.IsProtectionEnabled() == false
            ? SupportResult.NotSupported("Der Computerschutz (Systemwiederherstellung) ist für das Systemlaufwerk deaktiviert. Aktivieren Sie ihn unter „Wiederherstellungspunkt erstellen“ in der Systemsteuerung.")
            : SupportResult.Supported;
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var enabled = _restore.IsProtectionEnabled();
        return Task.FromResult(new ActionState
        {
            DisplayValue = enabled switch { true => "Computerschutz aktiv", false => "Computerschutz deaktiviert", _ => "Status unbekannt" },
            IsApplied = false,
        });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new ChangePreview { ActionId = ActionId, Title = Metadata.Name, CurrentValue = state.DisplayValue, NewValue = "Neuer Wiederherstellungspunkt", WillChange = true, Note = Metadata.SideEffects };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(new BackupData(ActionId, "{}", "–", "Wiederherstellungspunkt"));

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        return Task.Run(() =>
        {
            _last = _restore.CreateRestorePoint(parameters.GetString("description") ?? "NEXUS Optimizer AI");
            return _last.Success ? OperationResult.Ok(_last.Message) : OperationResult.Fail(_last.Message);
        }, CancellationToken.None);
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
        => Task.FromResult(_last is { Success: true }
            ? new VerificationResult(true, "Wiederherstellungspunkt angelegt", _last.Message, "Windows hat die Erstellung bestätigt.")
            : VerificationResult.Failure("Wiederherstellungspunkt angelegt", _last?.Message ?? "kein Ergebnis"));

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
        => Task.FromResult(NotReversible());
}

// ───────────────────────── Prozesse ─────────────────────────

public enum ProcessPriorityLevel
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
}

public sealed record ProcessIdentity(int ProcessId, string Name, long StartTimeTicks, ProcessPriorityLevel? Priority, bool IsProtected);

public interface IProcessControl
{
    /// <summary>null, wenn der Prozess nicht (mehr) existiert oder nicht geöffnet werden kann.</summary>
    ProcessIdentity? Find(int processId);
    void SetPriority(int processId, ProcessPriorityLevel priority);
}

public static class ProcessProtection
{
    /// <summary>Prozesse, die nie beendet oder umpriorisiert werden: Windows-Kern und Sicherheitssoftware.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "fontdrvhost", "dwm", "sihost", "ctfmon", "audiodg", "spoolsv", "WUDFHost", "conhost", "RuntimeBroker", "taskhostw", "SearchHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "explorer", "MsMpEng", "NisSrv", "MpDefenderCoreService", "SecurityHealthService", "SecurityHealthSystray", "MsSense", "SgrmBroker",
        "smartscreen", "TrustedInstaller", "TiWorker", "WmiPrvSE", "dllhost", "NexusOptimizer.App", "NexusOptimizer.Elevated",
    };

    public static bool IsProtected(string processName) => Protected.Contains(Path.GetFileNameWithoutExtension(processName));
}

/// <summary>Ändert die Prioritätsklasse eines laufenden Prozesses für dessen aktuelle Laufzeit. „Echtzeit“ wird bewusst nicht angeboten.</summary>
public sealed class ProcessPriorityAction : OptimizationActionBase
{
    public const string ActionId = "process.priority.set";
    private readonly IProcessControl _processes;

    public ProcessPriorityAction(IProcessControl processes) => _processes = processes;

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "Prozesspriorität ändern",
        Description = "Setzt die Prioritätsklasse eines laufenden Prozesses. Die Änderung gilt nur, bis der Prozess beendet wird.",
        Category = ActionCategory.Process,
        Risk = RiskLevel.Low,
        ExpectedEffect = "Der Scheduler bevorzugt oder benachteiligt den Prozess bei knapper CPU-Zeit. Ohne CPU-Engpass ist kein Effekt zu erwarten.",
        SideEffects = "Eine hohe Priorität kann andere Programme und die Eingabeverarbeitung ausbremsen.",
        BackupStrategy = "Die bisherige Prioritätsklasse wird zusammen mit Prozess-ID und Startzeit gesichert.",
        RollbackStrategy = "Die gesicherte Prioritätsklasse wird wiederhergestellt, sofern derselbe Prozess noch läuft.",
        VerificationStrategy = "Die Prioritätsklasse wird nach der Änderung erneut abgefragt.",
        Parameters = new[]
        {
            new ParameterSpec("pid", ParameterType.Integer, "Prozess-ID", Min: 5, Max: int.MaxValue, IsTargetKey: true),
            new ParameterSpec("priority", ParameterType.Choice, "Neue Prioritätsklasse", Choices: Enum.GetNames<ProcessPriorityLevel>()),
        },
        Tags = new[] { "process", "gaming" },
    };

    public static string ToGerman(ProcessPriorityLevel? level) => level switch
    {
        ProcessPriorityLevel.Idle => "Niedrig",
        ProcessPriorityLevel.BelowNormal => "Niedriger als normal",
        ProcessPriorityLevel.Normal => "Normal",
        ProcessPriorityLevel.AboveNormal => "Höher als normal",
        ProcessPriorityLevel.High => "Hoch",
        _ => "Unbekannt",
    };

    private static ProcessPriorityLevel Wanted(ActionParameters p) => Enum.Parse<ProcessPriorityLevel>(p.RequireString("priority"), ignoreCase: true);
    private ProcessIdentity? Process(ActionParameters p) => _processes.Find((int)p.GetInt("pid")!.Value);

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        var process = Process(parameters);
        if (process is null) return SupportResult.NotSupported("Der Prozess läuft nicht mehr oder kann nicht geöffnet werden.");
        if (process.IsProtected || ProcessProtection.IsProtected(process.Name)) return SupportResult.NotSupported("Windows- und Sicherheitsprozesse werden nicht umpriorisiert.");
        return SupportResult.Supported;
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var process = Process(parameters);
        if (process is null) return Task.FromResult(ActionState.Unavailable("Prozess nicht gefunden."));
        return Task.FromResult(new ActionState { DisplayValue = ToGerman(process.Priority), IsApplied = process.Priority == Wanted(parameters) });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var process = Process(parameters);
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new ChangePreview
        {
            ActionId = ActionId, Title = $"Priorität von {process?.Name ?? "Prozess"} (PID {parameters.GetInt("pid")})",
            CurrentValue = state.DisplayValue, NewValue = ToGerman(Wanted(parameters)), WillChange = state.IsApplied != true,
            Note = "Gilt nur für die aktuelle Laufzeit des Prozesses.",
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var process = Process(parameters) ?? throw new InvalidOperationException("Prozess nicht gefunden.");
        var payload = new PriorityBackup(process.ProcessId, process.StartTimeTicks, (process.Priority ?? ProcessPriorityLevel.Normal).ToString(), Wanted(parameters).ToString());
        return Task.FromResult(new BackupData(ActionId, JsonSerializer.Serialize(payload), ToGerman(process.Priority), ToGerman(Wanted(parameters))));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var process = Process(parameters);
        if (process is null) return Task.FromResult(OperationResult.Fail("Der Prozess läuft nicht mehr."));
        if (process.IsProtected || ProcessProtection.IsProtected(process.Name)) return Task.FromResult(OperationResult.Fail("Geschützte Prozesse werden nicht verändert."));
        try
        {
            _processes.SetPriority(process.ProcessId, Wanted(parameters));
            return Task.FromResult(OperationResult.Ok($"Priorität von {process.Name} auf „{ToGerman(Wanted(parameters))}“ gesetzt."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(OperationResult.Fail("Die Priorität konnte nicht geändert werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var process = Process(parameters);
        var wanted = ToGerman(Wanted(parameters));
        if (process is null) return Task.FromResult(VerificationResult.Failure(wanted, "Prozess beendet"));
        return Task.FromResult(process.Priority == Wanted(parameters) ? VerificationResult.Success(wanted, ToGerman(process.Priority)) : VerificationResult.Failure(wanted, ToGerman(process.Priority)));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        PriorityBackup? payload;
        try { payload = JsonSerializer.Deserialize<PriorityBackup>(backup.PayloadJson); }
        catch (JsonException) { payload = null; }
        if (payload is null || payload.ProcessId != parameters.GetInt("pid") || !Enum.TryParse<ProcessPriorityLevel>(payload.Previous, out var previous) || !Enum.TryParse<ProcessPriorityLevel>(payload.Applied, out var applied))
            return Task.FromResult(RollbackResult.Fail("Die Sicherungsdaten sind beschädigt."));

        var process = _processes.Find(payload.ProcessId);
        // Gleiche PID, aber andere Startzeit → die PID wurde neu vergeben; der ursprüngliche Prozess existiert nicht mehr.
        if (process is null || process.StartTimeTicks != payload.StartTimeTicks)
            return Task.FromResult(RollbackResult.Ok("Der Prozess wurde bereits beendet – die Prioritätsänderung ist damit hinfällig."));
        if (process.Priority == previous) return Task.FromResult(RollbackResult.Ok("Die ursprüngliche Priorität liegt bereits vor."));
        if (!force && process.Priority != applied)
            return Task.FromResult(RollbackResult.Conflict("Die Priorität wurde seit der Optimierung erneut geändert. Wiederherstellung nur nach ausdrücklicher Bestätigung."));

        try
        {
            _processes.SetPriority(payload.ProcessId, previous);
            return Task.FromResult(RollbackResult.Ok($"Priorität „{ToGerman(previous)}“ wiederhergestellt."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(RollbackResult.Fail("Die ursprüngliche Priorität konnte nicht wiederhergestellt werden.", ex.Message));
        }
    }

    private sealed record PriorityBackup(int ProcessId, long StartTimeTicks, string Previous, string Applied);
}
