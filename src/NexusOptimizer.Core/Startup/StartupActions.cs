using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Registry;

namespace NexusOptimizer.Core.Startup;

public enum StartupSource
{
    RegistryRunUser,
    RegistryRunMachine,
    RegistryRunMachine32,
    StartupFolderUser,
    StartupFolderCommon,
}

public sealed record StartupEntry
{
    public required StartupSource Source { get; init; }
    /// <summary>Wertname unter „Run“ bzw. Dateiname im Autostart-Ordner.</summary>
    public required string Name { get; init; }
    public string Command { get; init; } = "";
    public string? FilePath { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Publisher { get; init; }
    public string? ProductName { get; init; }
    public bool FileExists { get; init; }
    public long? FileSizeBytes { get; init; }

    public string Id => $"{Source}|{Name}";
    public bool RequiresAdministrator => StartupApproval.RequiresAdministrator(Source);
    public string DisplayName => string.IsNullOrWhiteSpace(ProductName) ? Path.GetFileNameWithoutExtension(Name) : ProductName!;
}

public interface IStartupInventory
{
    IReadOnlyList<StartupEntry> GetEntries();
}

/// <summary>
/// Windows merkt sich den Aktiviert-/Deaktiviert-Zustand von Autostarteinträgen unter „StartupApproved“ – derselbe Mechanismus,
/// den der Task-Manager verwendet. Der eigentliche Autostarteintrag bleibt dabei unverändert erhalten und ist vollständig wiederherstellbar.
/// </summary>
public static class StartupApproval
{
    private const string Base = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static readonly RegistryValueData EnabledValue = RegistryValueData.Binary(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    public static readonly RegistryValueData DisabledValue = RegistryValueData.Binary(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

    public static bool RequiresAdministrator(StartupSource source)
        => source is StartupSource.RegistryRunMachine or StartupSource.RegistryRunMachine32 or StartupSource.StartupFolderCommon;

    public static RegistryTarget Target(StartupSource source, string name) => source switch
    {
        StartupSource.RegistryRunUser => new RegistryTarget(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, Base + "Run", name),
        StartupSource.RegistryRunMachine => new RegistryTarget(RegistryRoot.LocalMachine, RegistryViewKind.Registry64, Base + "Run", name),
        StartupSource.RegistryRunMachine32 => new RegistryTarget(RegistryRoot.LocalMachine, RegistryViewKind.Registry64, Base + "Run32", name),
        StartupSource.StartupFolderUser => new RegistryTarget(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, Base + "StartupFolder", name),
        StartupSource.StartupFolderCommon => new RegistryTarget(RegistryRoot.LocalMachine, RegistryViewKind.Registry64, Base + "StartupFolder", name),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>Fehlt der Wert, gilt der Eintrag als aktiviert. Ein ungerades erstes Byte bedeutet „deaktiviert“.</summary>
    public static bool IsEnabled(RegistryValueData? data)
    {
        var bytes = data?.AsBinary();
        return bytes is null || bytes.Length == 0 || (bytes[0] & 1) == 0;
    }

    public static string ToGerman(StartupSource source) => source switch
    {
        StartupSource.RegistryRunUser => "Registrierung (Benutzer)",
        StartupSource.RegistryRunMachine => "Registrierung (alle Benutzer)",
        StartupSource.RegistryRunMachine32 => "Registrierung (alle Benutzer, 32 Bit)",
        StartupSource.StartupFolderUser => "Autostart-Ordner (Benutzer)",
        StartupSource.StartupFolderCommon => "Autostart-Ordner (alle Benutzer)",
        _ => source.ToString(),
    };
}

/// <summary>Aktiviert oder deaktiviert einen vorhandenen Autostarteintrag, ohne ihn zu löschen.</summary>
public sealed class StartupEntryAction : OptimizationActionBase
{
    public const string ActionId = "startup.entry.set";
    private readonly IRegistryStore _registry;
    private readonly IStartupInventory _inventory;

    public StartupEntryAction(IRegistryStore registry, IStartupInventory inventory)
    {
        _registry = registry;
        _inventory = inventory;
    }

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "Autostarteintrag aktivieren/deaktivieren",
        Description = "Schaltet einen Autostarteintrag ein oder aus. Der Eintrag selbst bleibt erhalten; Windows überspringt ihn lediglich bei der Anmeldung.",
        Category = ActionCategory.Startup,
        Risk = RiskLevel.Low,
        Restart = RestartRequirement.SignOut,
        ExpectedEffect = "Deaktivierte Programme starten nicht mehr automatisch mit; das kann die Anmeldung verkürzen und Hintergrundlast senken.",
        SideEffects = "Funktionen des Programms, die den Autostart voraussetzen (z. B. Synchronisierung, Tastenkürzel, automatische Updates), stehen erst nach manuellem Start zur Verfügung.",
        BackupStrategy = "Der bisherige StartupApproved-Wert (oder dessen Fehlen) wird im Transaktionsjournal gesichert.",
        RollbackStrategy = "Der gesicherte Wert wird zurückgeschrieben bzw. der neu angelegte Wert gelöscht.",
        VerificationStrategy = "Der StartupApproved-Wert wird zurückgelesen und der Aktiviert-Zustand geprüft.",
        ExcludeFromAutomation = true,
        Parameters = new[]
        {
            new ParameterSpec("source", ParameterType.Choice, "Herkunft des Eintrags", Choices: Enum.GetNames<StartupSource>(), IsTargetKey: true),
            new ParameterSpec("name", ParameterType.Text, "Name des Eintrags", Pattern: @"^[^\\/:*?""<>|\r\n]{1,200}$", MaxLength: 200, IsTargetKey: true),
            new ParameterSpec("enabled", ParameterType.Boolean, "Zielzustand"),
        },
        Tags = new[] { "startup" },
    };

    private static StartupSource Source(ActionParameters p) => Enum.Parse<StartupSource>(p.RequireString("source"), ignoreCase: true);
    private static RegistryTarget Target(ActionParameters p) => StartupApproval.Target(Source(p), p.RequireString("name"));
    private static RegistryValueData Intended(ActionParameters p) => p.GetBool("enabled") == true ? StartupApproval.EnabledValue : StartupApproval.DisabledValue;

    public override PrivilegeLevel GetRequiredPrivilege(ActionParameters parameters)
        => StartupApproval.RequiresAdministrator(Source(parameters)) ? PrivilegeLevel.Administrator : PrivilegeLevel.User;

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        var source = Source(parameters);
        var name = parameters.RequireString("name");
        return _inventory.GetEntries().Any(e => e.Source == source && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            ? SupportResult.Supported
            : SupportResult.NotSupported("Dieser Autostarteintrag existiert nicht (mehr).");
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var target = Target(parameters);
        try
        {
            var enabled = StartupApproval.IsEnabled(_registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName));
            return Task.FromResult(new ActionState { DisplayValue = enabled ? "Aktiviert" : "Deaktiviert", IsApplied = enabled == parameters.GetBool("enabled") });
        }
        catch (Exception ex) when (RegistryTweakAction.IsAccessError(ex))
        {
            return Task.FromResult(ActionState.Unavailable("Zugriff auf die Registrierung verweigert."));
        }
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new ChangePreview
        {
            ActionId = ActionId,
            Title = $"Autostart „{parameters.RequireString("name")}“",
            CurrentValue = state.DisplayValue,
            NewValue = parameters.GetBool("enabled") == true ? "Aktiviert" : "Deaktiviert",
            WillChange = state.IsApplied != true,
            TechnicalDetails = new[] { $"Herkunft: {StartupApproval.ToGerman(Source(parameters))}", $"Zustandswert: {Target(parameters).Display}" },
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var target = Target(parameters);
        var payload = RegistryValueTransaction.Capture(_registry, target, Intended(parameters));
        var wasEnabled = StartupApproval.IsEnabled(payload.Original);
        return Task.FromResult(new BackupData(ActionId, RegistryValueTransaction.Serialize(payload), wasEnabled ? "Aktiviert" : "Deaktiviert", parameters.GetBool("enabled") == true ? "Aktiviert" : "Deaktiviert"));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var target = Target(parameters);
        try
        {
            _registry.WriteValue(target.Root, target.View, target.KeyPath, target.ValueName, Intended(parameters));
            return Task.FromResult(OperationResult.Ok($"Autostart „{target.ValueName}“ {(parameters.GetBool("enabled") == true ? "aktiviert" : "deaktiviert")}."));
        }
        catch (Exception ex) when (RegistryTweakAction.IsAccessError(ex))
        {
            return Task.FromResult(OperationResult.Fail("Zugriff verweigert. Der Autostarteintrag konnte nicht geändert werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var target = Target(parameters);
        var wanted = parameters.GetBool("enabled") == true;
        try
        {
            var actual = StartupApproval.IsEnabled(_registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName));
            string Text(bool enabled) => enabled ? "Aktiviert" : "Deaktiviert";
            return Task.FromResult(actual == wanted ? VerificationResult.Success(Text(wanted), Text(actual)) : VerificationResult.Failure(Text(wanted), Text(actual)));
        }
        catch (Exception ex) when (RegistryTweakAction.IsAccessError(ex))
        {
            return Task.FromResult(VerificationResult.Failure(wanted ? "Aktiviert" : "Deaktiviert", "nicht lesbar"));
        }
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        var validation = ValidateParameters(parameters);
        if (!validation.IsValid) return Task.FromResult(RollbackResult.Fail(validation.ErrorText));
        try
        {
            var payload = RegistryValueTransaction.Parse(backup.PayloadJson);
            if (payload.Original is { Type: not RegistryValueType.Binary }) return Task.FromResult(RollbackResult.Fail("Die Sicherungsdaten haben einen unerwarteten Datentyp."));
            return Task.FromResult(RegistryValueTransaction.Rollback(_registry, Target(parameters), payload, force));
        }
        catch (FormatException ex)
        {
            return Task.FromResult(RollbackResult.Fail(ex.Message));
        }
        catch (Exception ex) when (RegistryTweakAction.IsAccessError(ex))
        {
            return Task.FromResult(RollbackResult.Fail("Zugriff verweigert. Der Autostarteintrag konnte nicht wiederhergestellt werden.", ex.Message));
        }
    }
}

/// <summary>Legt pro Anwendung fest, welche Grafikeinheit Windows bevorzugen soll (Einstellungen → Anzeige → Grafik).</summary>
public sealed class GpuPreferenceAction : OptimizationActionBase
{
    public const string ActionId = "gpu.preference.set";
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static readonly string[] Choices = { "default", "power-saving", "high-performance" };
    private readonly IRegistryStore _registry;

    public GpuPreferenceAction(IRegistryStore registry) => _registry = registry;

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "Grafikeinstellung pro Anwendung",
        Description = "Weist einer Anwendung die energiesparende oder die leistungsstarke Grafikeinheit zu. Entspricht der Windows-Einstellung „Grafikleistungseinstellung“.",
        Category = ActionCategory.Gpu,
        Risk = RiskLevel.VeryLow,
        ExpectedEffect = "Auf Systemen mit integrierter und dedizierter Grafik startet die Anwendung auf der gewählten Grafikeinheit.",
        SideEffects = "„Hohe Leistung“ erhöht Stromverbrauch und Abwärme. Auf Systemen mit nur einer Grafikeinheit bleibt die Einstellung wirkungslos.",
        BackupStrategy = "Der bisherige Präferenzwert der Anwendung wird im Transaktionsjournal gesichert.",
        RollbackStrategy = "Der gesicherte Wert wird zurückgeschrieben bzw. der neu angelegte Wert gelöscht.",
        VerificationStrategy = "Der Präferenzwert wird zurückgelesen.",
        Parameters = new[]
        {
            new ParameterSpec("path", ParameterType.FilePath, "Vollständiger Pfad der ausführbaren Datei", Pattern: @"(?i)\.exe$", IsTargetKey: true),
            new ParameterSpec("preference", ParameterType.Choice, "Gewünschte Grafikeinheit", Choices: Choices),
        },
        Tags = new[] { "gpu", "gaming" },
    };

    private static RegistryTarget Target(ActionParameters p) => new(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, KeyPath, p.RequireString("path"));
    private static int Index(ActionParameters p) => Array.IndexOf(Choices, p.RequireString("preference").ToLowerInvariant());

    public static string Describe(int? preference) => preference switch
    {
        1 => "Energiesparen (integrierte Grafik)",
        2 => "Hohe Leistung (dedizierte Grafik)",
        _ => "Windows entscheidet",
    };

    /// <summary>Liest „GpuPreference=N;“ aus dem Wert; andere Schlüssel im selben Wert bleiben unberührt.</summary>
    public static int? Parse(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("GpuPreference", StringComparison.OrdinalIgnoreCase) && int.TryParse(pair[1], out var value)) return value;
        }
        return null;
    }

    public static string Compose(string? existing, int preference)
    {
        var parts = (existing ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Add($"GpuPreference={preference}");
        return string.Join(";", parts) + ";";
    }

    private RegistryValueData Intended(ActionParameters p)
    {
        var target = Target(p);
        var existing = _registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName);
        return RegistryValueData.Text(Compose(existing?.Type == RegistryValueType.String ? existing.Data : null, Index(p)));
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var target = Target(parameters);
        var current = Parse(_registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName)?.Data);
        return Task.FromResult(new ActionState { DisplayValue = Describe(current), IsApplied = (current ?? 0) == Index(parameters) });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new ChangePreview
        {
            ActionId = ActionId,
            Title = $"Grafikeinheit für {Path.GetFileName(parameters.RequireString("path"))}",
            CurrentValue = state.DisplayValue,
            NewValue = Describe(Index(parameters)),
            WillChange = state.IsApplied != true,
            TechnicalDetails = new[] { $"Wert: {Target(parameters).Display}" },
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var payload = RegistryValueTransaction.Capture(_registry, Target(parameters), Intended(parameters));
        return Task.FromResult(new BackupData(ActionId, RegistryValueTransaction.Serialize(payload), Describe(Parse(payload.OriginalData)), Describe(Index(parameters))));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var target = Target(parameters);
        try
        {
            _registry.WriteValue(target.Root, target.View, target.KeyPath, target.ValueName, Intended(parameters));
            return Task.FromResult(OperationResult.Ok($"Grafikeinstellung gesetzt: {Describe(Index(parameters))}."));
        }
        catch (Exception ex) when (RegistryTweakAction.IsAccessError(ex))
        {
            return Task.FromResult(OperationResult.Fail("Die Grafikeinstellung konnte nicht geschrieben werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var target = Target(parameters);
        var actual = Parse(_registry.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName)?.Data);
        var wanted = Index(parameters);
        return Task.FromResult(actual == wanted ? VerificationResult.Success(Describe(wanted), Describe(actual)) : VerificationResult.Failure(Describe(wanted), Describe(actual)));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        var validation = ValidateParameters(parameters);
        if (!validation.IsValid) return Task.FromResult(RollbackResult.Fail(validation.ErrorText));
        try
        {
            return Task.FromResult(RegistryValueTransaction.Rollback(_registry, Target(parameters), RegistryValueTransaction.Parse(backup.PayloadJson), force));
        }
        catch (FormatException ex)
        {
            return Task.FromResult(RollbackResult.Fail(ex.Message));
        }
        catch (Exception ex) when (RegistryTweakAction.IsAccessError(ex))
        {
            return Task.FromResult(RollbackResult.Fail("Die Grafikeinstellung konnte nicht wiederhergestellt werden.", ex.Message));
        }
    }
}
