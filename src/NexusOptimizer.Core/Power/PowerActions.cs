using System.Text.Json;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Power;

/// <summary>Aktiviert einen vorhandenen Windows-Energiesparplan. Der vorherige Plan wird gesichert und kann wiederhergestellt werden.</summary>
public sealed class PowerPlanActivateAction : OptimizationActionBase
{
    public const string ActionId = "power.plan.activate";
    private readonly IPowerPolicy _power;

    public PowerPlanActivateAction(IPowerPolicy power) => _power = power;

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "Energiesparplan aktivieren",
        Description = "Wechselt zum ausgewählten Windows-Energiesparplan.",
        Category = ActionCategory.Power,
        Risk = RiskLevel.Low,
        ExpectedEffect = "Leistungsorientierte Pläne verringern leistungsbedingte Einschränkungen bei bestimmten Workloads; sparsame Pläne senken Verbrauch und Lautstärke.",
        SideEffects = "Leistungsorientierte Pläne erhöhen Stromverbrauch, Abwärme und Lüftergeräusch und verkürzen die Akkulaufzeit.",
        BackupStrategy = "Die GUID des zuvor aktiven Plans wird im Transaktionsjournal gespeichert.",
        RollbackStrategy = "Der zuvor aktive Plan wird wieder aktiviert, sofern er noch existiert.",
        VerificationStrategy = "Der aktive Plan wird nach dem Wechsel erneut abgefragt.",
        Parameters = new[] { new ParameterSpec("plan", ParameterType.Guid, "GUID des zu aktivierenden Energiesparplans") },
        Tags = new[] { "power" },
    };

    private PowerPlanInfo? FindPlan(Guid id) => _power.GetPlans().FirstOrDefault(p => p.Id == id);

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        var plan = parameters.GetGuid("plan");
        if (plan is null || FindPlan(plan.Value) is null)
            return SupportResult.NotSupported("Der gewünschte Energiesparplan ist auf diesem System nicht vorhanden. Geräte mit Modern Standby bieten häufig nur „Ausbalanciert“ an.");
        return SupportResult.Supported;
    }

    public override RiskAssessment EvaluateRisk(ActionParameters parameters, SystemContext context)
    {
        var reasons = new List<string> { Metadata.SideEffects };
        var plan = parameters.GetGuid("plan");
        var performance = plan == WellKnownPowerPlans.HighPerformance || plan == WellKnownPowerPlans.UltimatePerformance;
        if (performance && context.OnBatteryPower) reasons.Add("Das Gerät läuft derzeit im Akkubetrieb – die Laufzeit sinkt deutlich.");
        if (performance && context.ThermalThrottlingObserved) reasons.Add("Es wurde bereits thermische Drosselung beobachtet – ein Leistungsplan kann das Problem verschärfen.");
        return new RiskAssessment(RiskLevel.Low, reasons);
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var active = _power.GetActivePlan();
        var name = FindPlan(active)?.Name ?? active.ToString();
        return Task.FromResult(new ActionState { DisplayValue = name, RawValue = active.ToString(), IsApplied = parameters.GetGuid("plan") == active });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        var target = parameters.GetGuid("plan") is { } id ? FindPlan(id) : null;
        return new ChangePreview
        {
            ActionId = ActionId, Title = Metadata.Name, CurrentValue = state.DisplayValue, NewValue = target?.Name ?? "Unbekannter Plan",
            WillChange = state.IsApplied != true,
            TechnicalDetails = new[] { $"Aktive Plan-GUID: {state.RawValue}", $"Neue Plan-GUID: {target?.Id}" },
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var active = _power.GetActivePlan();
        var targetId = parameters.GetGuid("plan")!.Value;
        return Task.FromResult(new BackupData(ActionId, JsonSerializer.Serialize(new PlanBackup(active, targetId)),
            FindPlan(active)?.Name ?? active.ToString(), FindPlan(targetId)?.Name ?? targetId.ToString()));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var target = parameters.GetGuid("plan")!.Value;
        var plan = FindPlan(target);
        if (plan is null) return Task.FromResult(OperationResult.Fail("Der Energiesparplan existiert nicht."));
        try
        {
            _power.SetActivePlan(target);
            return Task.FromResult(OperationResult.Ok($"Energiesparplan „{plan.Name}“ aktiviert."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(OperationResult.Fail("Der Energiesparplan konnte nicht aktiviert werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var target = parameters.GetGuid("plan");
        var active = _power.GetActivePlan();
        var activeName = FindPlan(active)?.Name ?? active.ToString();
        var targetName = target is { } id ? FindPlan(id)?.Name ?? id.ToString() : "?";
        return Task.FromResult(active == target ? VerificationResult.Success(targetName, activeName) : VerificationResult.Failure(targetName, activeName));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        PlanBackup? payload;
        try { payload = JsonSerializer.Deserialize<PlanBackup>(backup.PayloadJson); }
        catch (JsonException) { payload = null; }
        if (payload is null || payload.Previous == Guid.Empty) return Task.FromResult(RollbackResult.Fail("Die Sicherungsdaten sind beschädigt."));

        var active = _power.GetActivePlan();
        if (active == payload.Previous) return Task.FromResult(RollbackResult.Ok("Der ursprüngliche Energiesparplan ist bereits aktiv."));
        if (!force && active != payload.Applied)
            return Task.FromResult(RollbackResult.Conflict("Der Energiesparplan wurde seit der Optimierung erneut gewechselt. Wiederherstellung nur nach ausdrücklicher Bestätigung."));

        var previous = FindPlan(payload.Previous);
        if (previous is null) return Task.FromResult(RollbackResult.Fail("Der ursprüngliche Energiesparplan existiert nicht mehr."));

        try
        {
            _power.SetActivePlan(payload.Previous);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(RollbackResult.Fail("Der ursprüngliche Energiesparplan konnte nicht aktiviert werden.", ex.Message));
        }

        return Task.FromResult(_power.GetActivePlan() == payload.Previous
            ? RollbackResult.Ok($"Energiesparplan „{previous.Name}“ wiederhergestellt.")
            : RollbackResult.Fail("Die Wiederherstellung des Energiesparplans konnte nicht bestätigt werden."));
    }

    private sealed record PlanBackup(Guid Previous, Guid Applied);
}

/// <summary>Setzt eine Energieeinstellung aus der Allowlist (<see cref="PowerSettingCatalog"/>) für Netz- und/oder Akkubetrieb.</summary>
public sealed class PowerSettingAction : OptimizationActionBase
{
    public const string ActionId = "power.setting.set";
    private readonly IPowerPolicy _power;

    public PowerSettingAction(IPowerPolicy power) => _power = power;

    public override ActionMetadata Metadata { get; } = new()
    {
        Id = ActionId,
        Name = "Energieeinstellung ändern",
        Description = "Ändert eine unterstützte Einstellung des aktiven oder eines ausgewählten Energiesparplans.",
        Category = ActionCategory.Power,
        Risk = RiskLevel.Low,
        ExpectedEffect = "Die gewählte Energieeinstellung gilt sofort für den betroffenen Plan.",
        SideEffects = "Abhängig von der Einstellung: veränderter Stromverbrauch, Abwärme, Lüftergeräusch oder Akkulaufzeit.",
        BackupStrategy = "Die bisherigen Werte für Netz- und Akkubetrieb werden im Transaktionsjournal gespeichert.",
        RollbackStrategy = "Die gesicherten Werte werden in denselben Plan zurückgeschrieben.",
        VerificationStrategy = "Die Werte werden nach dem Schreiben erneut aus dem Plan gelesen.",
        Parameters = new[]
        {
            new ParameterSpec("setting", ParameterType.Choice, "Alias der Einstellung", Choices: PowerSettingCatalog.All.Select(s => s.Alias).ToArray(), IsTargetKey: true),
            new ParameterSpec("plan", ParameterType.Guid, "GUID des Plans (Standard: aktiver Plan)", Required: false, IsTargetKey: true),
            new ParameterSpec("ac", ParameterType.Integer, "Wert im Netzbetrieb", Required: false, Min: 0, Max: 36000),
            new ParameterSpec("dc", ParameterType.Integer, "Wert im Akkubetrieb", Required: false, Min: 0, Max: 36000),
        },
        ExcludeFromAutomation = false,
        Tags = new[] { "power" },
    };

    private static PowerSettingDefinition Definition(ActionParameters parameters) => PowerSettingCatalog.Find(parameters.RequireString("setting"))!;
    private Guid Plan(ActionParameters parameters) => parameters.GetGuid("plan") ?? _power.GetActivePlan();

    public override ValidationResult ValidateParameters(ActionParameters parameters)
    {
        var result = base.ValidateParameters(parameters);
        if (!result.IsValid) return result;

        var definition = Definition(parameters);
        var errors = new List<string>();
        if (!parameters.Has("ac") && !parameters.Has("dc")) errors.Add("Es muss mindestens ein Wert für Netz- oder Akkubetrieb angegeben werden.");
        foreach (var name in new[] { "ac", "dc" })
        {
            if (parameters.GetInt(name) is { } value && (value < definition.Min || value > definition.Max))
                errors.Add($"Der Wert {value} liegt außerhalb des zulässigen Bereichs {definition.Min}–{definition.Max} für „{definition.Name}“.");
        }
        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(false, errors);
    }

    public override async Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var result = await base.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSupported) return result;
        var definition = Definition(parameters);
        var plan = Plan(parameters);
        if (_power.GetPlans().All(p => p.Id != plan)) return SupportResult.NotSupported("Der angegebene Energiesparplan existiert nicht.");
        if (_power.ReadAcValue(plan, definition.Subgroup, definition.Setting) is null)
            return SupportResult.NotSupported($"Die Einstellung „{definition.Name}“ wird von diesem System oder Energiesparplan nicht bereitgestellt.");
        return SupportResult.Supported;
    }

    public override RiskAssessment EvaluateRisk(ActionParameters parameters, SystemContext context)
    {
        var definition = Definition(parameters);
        var reasons = new List<string>();
        if (!string.IsNullOrEmpty(definition.Warning)) reasons.Add(definition.Warning);
        if (context.ThermalThrottlingObserved && definition.Subgroup == PowerSettingCatalog.SubProcessor)
            reasons.Add("Es wurde thermische Drosselung beobachtet – aggressivere Prozessoreinstellungen sind nicht empfehlenswert.");
        return new RiskAssessment(definition.Risk, reasons);
    }

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var definition = Definition(parameters);
        var plan = Plan(parameters);
        var ac = _power.ReadAcValue(plan, definition.Subgroup, definition.Setting);
        var dc = _power.ReadDcValue(plan, definition.Subgroup, definition.Setting);
        if (ac is null && dc is null) return Task.FromResult(ActionState.Unavailable("Die Einstellung ist auf diesem System nicht vorhanden."));

        var applied = (parameters.GetInt("ac") is not { } wantAc || ac == wantAc) && (parameters.GetInt("dc") is not { } wantDc || dc == wantDc);
        return Task.FromResult(new ActionState
        {
            DisplayValue = $"Netz: {definition.Format(ac)} · Akku: {definition.Format(dc)}",
            RawValue = $"AC={ac};DC={dc}",
            IsApplied = applied,
        });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var definition = Definition(parameters);
        var state = await DetectCurrentStateAsync(parameters, cancellationToken).ConfigureAwait(false);
        var planName = _power.GetPlans().FirstOrDefault(p => p.Id == Plan(parameters))?.Name ?? "Unbekannter Plan";
        return new ChangePreview
        {
            ActionId = ActionId,
            Title = $"{definition.Name} ({planName})",
            CurrentValue = state.DisplayValue,
            NewValue = DescribeTarget(definition, parameters),
            WillChange = state.IsApplied != true,
            Note = string.IsNullOrEmpty(definition.Warning) ? null : definition.Warning,
            TechnicalDetails = new[] { $"Plan: {Plan(parameters)}", $"Untergruppe: {definition.Subgroup}", $"Einstellung: {definition.Setting}", $"Aktuelle Rohwerte: {state.RawValue}" },
        };
    }

    private static string DescribeTarget(PowerSettingDefinition definition, ActionParameters parameters)
    {
        var parts = new List<string>();
        if (parameters.GetInt("ac") is { } ac) parts.Add($"Netz: {definition.Format((uint)ac)}");
        if (parameters.GetInt("dc") is { } dc) parts.Add($"Akku: {definition.Format((uint)dc)}");
        return string.Join(" · ", parts);
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var definition = Definition(parameters);
        var plan = Plan(parameters);
        var payload = new SettingBackup(plan,
            _power.ReadAcValue(plan, definition.Subgroup, definition.Setting), _power.ReadDcValue(plan, definition.Subgroup, definition.Setting),
            (uint?)parameters.GetInt("ac"), (uint?)parameters.GetInt("dc"));
        return Task.FromResult(new BackupData(ActionId, JsonSerializer.Serialize(payload),
            $"Netz: {definition.Format(payload.PreviousAc)} · Akku: {definition.Format(payload.PreviousDc)}", DescribeTarget(definition, parameters)));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        var definition = Definition(parameters);
        var plan = Plan(parameters);
        try
        {
            if (parameters.GetInt("ac") is { } ac) _power.WriteAcValue(plan, definition.Subgroup, definition.Setting, (uint)ac);
            if (parameters.GetInt("dc") is { } dc) _power.WriteDcValue(plan, definition.Subgroup, definition.Setting, (uint)dc);
            // Änderungen am aktiven Plan werden erst wirksam, wenn der Plan erneut aktiviert wird.
            if (_power.GetActivePlan() == plan) _power.SetActivePlan(plan);
            return Task.FromResult(OperationResult.Ok($"„{definition.Name}“ gesetzt: {DescribeTarget(definition, parameters)}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(OperationResult.Fail("Die Energieeinstellung konnte nicht geschrieben werden.", ex.Message));
        }
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var definition = Definition(parameters);
        var plan = Plan(parameters);
        var ac = _power.ReadAcValue(plan, definition.Subgroup, definition.Setting);
        var dc = _power.ReadDcValue(plan, definition.Subgroup, definition.Setting);
        var ok = (parameters.GetInt("ac") is not { } wantAc || ac == wantAc) && (parameters.GetInt("dc") is not { } wantDc || dc == wantDc);
        var actual = $"Netz: {definition.Format(ac)} · Akku: {definition.Format(dc)}";
        return Task.FromResult(ok ? VerificationResult.Success(DescribeTarget(definition, parameters), actual) : VerificationResult.Failure(DescribeTarget(definition, parameters), actual));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        var validation = ValidateParameters(parameters);
        if (!validation.IsValid) return Task.FromResult(RollbackResult.Fail(validation.ErrorText));
        var definition = Definition(parameters);

        SettingBackup? payload;
        try { payload = JsonSerializer.Deserialize<SettingBackup>(backup.PayloadJson); }
        catch (JsonException) { payload = null; }
        if (payload is null || payload.Plan == Guid.Empty) return Task.FromResult(RollbackResult.Fail("Die Sicherungsdaten sind beschädigt."));
        if (payload.PreviousAc > definition.Max || payload.PreviousDc > definition.Max)
            return Task.FromResult(RollbackResult.Fail("Die Sicherungsdaten enthalten Werte außerhalb des zulässigen Bereichs."));
        if (_power.GetPlans().All(p => p.Id != payload.Plan)) return Task.FromResult(RollbackResult.Fail("Der betroffene Energiesparplan existiert nicht mehr."));

        var ac = _power.ReadAcValue(payload.Plan, definition.Subgroup, definition.Setting);
        var dc = _power.ReadDcValue(payload.Plan, definition.Subgroup, definition.Setting);
        var isOriginal = (payload.AppliedAc is null || ac == payload.PreviousAc) && (payload.AppliedDc is null || dc == payload.PreviousDc);
        if (isOriginal) return Task.FromResult(RollbackResult.Ok("Die ursprünglichen Werte liegen bereits vor."));

        var untouched = (payload.AppliedAc is null || ac == payload.AppliedAc) && (payload.AppliedDc is null || dc == payload.AppliedDc);
        if (!force && !untouched)
            return Task.FromResult(RollbackResult.Conflict("Die Energieeinstellung wurde seit der Optimierung erneut geändert. Wiederherstellung nur nach ausdrücklicher Bestätigung."));

        try
        {
            if (payload.AppliedAc is not null && payload.PreviousAc is { } previousAc) _power.WriteAcValue(payload.Plan, definition.Subgroup, definition.Setting, previousAc);
            if (payload.AppliedDc is not null && payload.PreviousDc is { } previousDc) _power.WriteDcValue(payload.Plan, definition.Subgroup, definition.Setting, previousDc);
            if (_power.GetActivePlan() == payload.Plan) _power.SetActivePlan(payload.Plan);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(RollbackResult.Fail("Die ursprünglichen Werte konnten nicht zurückgeschrieben werden.", ex.Message));
        }

        return Task.FromResult(RollbackResult.Ok($"„{definition.Name}“ auf Netz: {definition.Format(payload.PreviousAc)} · Akku: {definition.Format(payload.PreviousDc)} zurückgesetzt."));
    }

    private sealed record SettingBackup(Guid Plan, uint? PreviousAc, uint? PreviousDc, uint? AppliedAc, uint? AppliedDc);
}
