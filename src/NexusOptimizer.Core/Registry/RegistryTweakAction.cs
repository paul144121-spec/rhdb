using System.Security;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Registry;

/// <summary>Stark typisierte Definition eines Registrierungs-Tweaks (Abschnitt 16.1).</summary>
public sealed record RegistryTweakDefinition
{
    /// <summary>Eindeutige ID ohne Präfix, z. B. "explorer.show-file-extensions". Die Aktions-ID lautet "reg." + Id.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ActionCategory Category { get; init; }
    public required RegistryRoot Root { get; init; }
    public RegistryViewKind View { get; init; } = RegistryViewKind.Registry64;
    public required string KeyPath { get; init; }
    public required string ValueName { get; init; }
    public required RegistryValueData OptimizedValue { get; init; }
    /// <summary>Wirksamer Windows-Standard (auch wenn der Wert fehlt); null, wenn nicht eindeutig dokumentiert.</summary>
    public RegistryValueData? WindowsDefault { get; init; }
    public required string OptimizedLabel { get; init; }
    public required string DefaultLabel { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.VeryLow;
    public RestartRequirement Restart { get; init; } = RestartRequirement.None;
    public WindowsVersionRange Versions { get; init; } = WindowsVersionRange.All;
    public string ExpectedBehavior { get; init; } = "";
    public string SideEffects { get; init; } = "Keine bekannten Nebenwirkungen.";
    public bool Experimental { get; init; }
    /// <summary>Der Tweak ist nur sinnvoll, wenn Windows/der Treiber den Wert bereits angelegt hat (z. B. HwSchMode).</summary>
    public bool RequiresExistingValue { get; init; }
    /// <summary>Optionaler Richtlinienwert, der diese Einstellung übersteuert.</summary>
    public RegistryTarget? PolicyOverride { get; init; }
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public bool ExcludeFromAutomation { get; init; }

    public string ActionId => "reg." + Id;
    public RegistryTarget Target => new(Root, View, KeyPath, ValueName);
}

/// <summary>Generische, vollständig umkehrbare Aktion für genau einen Katalog-Tweak.</summary>
public sealed class RegistryTweakAction : OptimizationActionBase
{
    private readonly IRegistryStore _store;

    public RegistryTweakDefinition Definition { get; }
    public override ActionMetadata Metadata { get; }

    public RegistryTweakAction(RegistryTweakDefinition definition, IRegistryStore store)
    {
        Definition = definition;
        _store = store;

        var guard = RegistryPathGuard.Validate(definition.Target);
        if (!guard.IsValid) throw new ArgumentException($"Tweak '{definition.Id}' ist unzulässig: {guard.ErrorText}");
        if (!definition.OptimizedValue.IsWellFormed()) throw new ArgumentException($"Tweak '{definition.Id}' hat einen ungültigen Zielwert.");
        if (definition.WindowsDefault is not null && !definition.WindowsDefault.IsWellFormed()) throw new ArgumentException($"Tweak '{definition.Id}' hat einen ungültigen Standardwert.");

        var isPolicyKey = definition.KeyPath.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase);
        Metadata = new ActionMetadata
        {
            Id = definition.ActionId,
            Name = definition.Name,
            Description = definition.Description,
            Category = definition.Category,
            Risk = definition.Risk,
            RequiredPrivilege = definition.Root == RegistryRoot.LocalMachine ? PrivilegeLevel.Administrator : PrivilegeLevel.User,
            Restart = definition.Restart,
            IsReversible = true,
            IsExperimental = definition.Experimental,
            SupportedVersions = definition.Versions,
            ExpectedEffect = definition.ExpectedBehavior,
            SideEffects = definition.SideEffects,
            BackupStrategy = "Vor der Änderung werden Existenz, Datentyp und Daten des Werts im lokalen Transaktionsjournal gesichert.",
            RollbackStrategy = "Der gesicherte Wert wird zurückgeschrieben; existierte der Wert ursprünglich nicht, wird er wieder gelöscht.",
            VerificationStrategy = "Der Wert wird nach dem Schreiben zurückgelesen und mit dem Zielwert verglichen.",
            Conflicts = definition.Conflicts,
            Tags = definition.Tags,
            ExcludeFromAutomation = definition.ExcludeFromAutomation,
            TechnicalDetails = new[]
            {
                $"Registrierungspfad: {definition.Target.Display}",
                $"Datentyp: {RegistryValueData.TypeName(definition.OptimizedValue.Type)}",
                $"Zielwert: {definition.OptimizedValue.ToDisplay()} ({definition.OptimizedLabel})",
                definition.WindowsDefault is null
                    ? "Windows-Standard: nicht eindeutig dokumentiert"
                    : $"Windows-Standard: {definition.WindowsDefault.ToDisplay()} ({definition.DefaultLabel})",
                isPolicyKey ? "Hinweis: Dieser Wert liegt in einem Richtlinienzweig; Windows zeigt die Einstellung danach als „von Ihrer Organisation verwaltet“ an." : "Kein Richtlinienzweig.",
            },
        };
    }

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var baseResult = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!baseResult.IsSupported) return baseResult;

        if (Definition.RequiresExistingValue)
        {
            try
            {
                if (_store.ReadValue(Definition.Root, Definition.View, Definition.KeyPath, Definition.ValueName) is null)
                    return SupportResult.NotSupported("Windows bzw. der Gerätetreiber stellt diese Einstellung auf diesem System nicht bereit.");
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                return SupportResult.NotSupported("Die Einstellung konnte nicht gelesen werden (Zugriff verweigert).");
            }
        }
        return SupportResult.Supported;
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            var current = _store.ReadValue(Definition.Root, Definition.View, Definition.KeyPath, Definition.ValueName);
            var managed = Definition.PolicyOverride is { } policy && _store.ReadValue(policy.Root, policy.View, policy.KeyPath, policy.ValueName) is not null;
            // Fehlt der Wert, gilt der dokumentierte Windows-Standard als wirksamer Zustand.
            var effective = current ?? Definition.WindowsDefault;
            var applied = effective is not null && RegistryValueTransaction.ValuesEqual(effective, Definition.OptimizedValue);

            string display;
            if (applied) display = current is null ? $"{Definition.OptimizedLabel} (Windows-Standard)" : Definition.OptimizedLabel;
            else if (current is null) display = Definition.WindowsDefault is null ? $"{Definition.DefaultLabel} (Wert nicht gesetzt)" : $"{Definition.DefaultLabel} (Windows-Standard)";
            else if (Definition.WindowsDefault is not null && RegistryValueTransaction.ValuesEqual(current, Definition.WindowsDefault)) display = Definition.DefaultLabel;
            else if (current.AsDWord() is 0 or 1 && Definition.OptimizedValue.AsDWord() is 0 or 1) display = Definition.DefaultLabel;
            else display = $"Benutzerdefiniert: {current.ToDisplay()}";

            return Task.FromResult(new ActionState
            {
                DisplayValue = display,
                IsApplied = applied,
                RawValue = current is null ? null : RegistryValueTransaction.Describe(current),
                ManagedByPolicy = managed,
                Note = managed ? "Diese Einstellung wird durch eine Organisationsrichtlinie gesteuert; eine Änderung hier bleibt möglicherweise wirkungslos." : null,
            });
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            return Task.FromResult(ActionState.Unavailable("Zugriff auf die Registrierung verweigert."));
        }
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new ChangePreview
        {
            ActionId = Metadata.Id,
            Title = Metadata.Name,
            CurrentValue = state.DisplayValue,
            NewValue = Definition.OptimizedLabel,
            WillChange = state.IsApplied != true,
            Note = state.Note,
            TechnicalDetails = new[]
            {
                $"Pfad: {Definition.Target.Display}",
                $"Aktueller Rohwert: {state.RawValue ?? "nicht vorhanden"}",
                $"Neuer Rohwert: {RegistryValueTransaction.Describe(Definition.OptimizedValue)}",
            },
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var payload = RegistryValueTransaction.Capture(_store, Definition.Target, Definition.OptimizedValue);
        var originalDisplay = payload.Original is { } original ? RegistryValueTransaction.Describe(original) : "Wert nicht vorhanden";
        return Task.FromResult(new BackupData(Metadata.Id, RegistryValueTransaction.Serialize(payload), originalDisplay, RegistryValueTransaction.Describe(Definition.OptimizedValue)));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        try
        {
            _store.WriteValue(Definition.Root, Definition.View, Definition.KeyPath, Definition.ValueName, Definition.OptimizedValue);
            return Task.FromResult(OperationResult.Ok($"Wert geschrieben: {Definition.Target.Display} = {Definition.OptimizedValue.ToDisplay()}"));
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            return Task.FromResult(OperationResult.Fail("Zugriff verweigert. Der Wert konnte nicht geschrieben werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(RegistryValueTransaction.Verify(_store, Definition.Target, Definition.OptimizedValue));
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            return Task.FromResult(VerificationResult.Failure(Definition.OptimizedValue.ToDisplay(), "nicht lesbar", "Der Wert konnte zur Verifikation nicht gelesen werden."));
        }
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = RegistryValueTransaction.Parse(backup.PayloadJson);
            return Task.FromResult(RegistryValueTransaction.Rollback(_store, Definition.Target, payload, force));
        }
        catch (FormatException ex)
        {
            return Task.FromResult(RollbackResult.Fail(ex.Message));
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            return Task.FromResult(RollbackResult.Fail("Zugriff verweigert. Der Ursprungswert konnte nicht wiederhergestellt werden.", ex.Message));
        }
    }

    internal static bool IsAccessError(Exception ex) => ex is UnauthorizedAccessException or SecurityException or IOException;
}
