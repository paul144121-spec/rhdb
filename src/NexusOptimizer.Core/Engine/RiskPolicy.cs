using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Engine;

public sealed record PermissionDecision(bool Allowed, string Reason, bool RequiresIndividualConfirmation = false)
{
    public static PermissionDecision Allow(bool individualConfirmation = false) => new(true, "Zulässig.", individualConfirmation);
    public static PermissionDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Zentrale Risikofreigabe. Weder Profile noch KI-Vorschläge noch Automatisierungsregeln können diese Prüfung umgehen,
/// weil der Planer sie für jede Aktion unabhängig von der Quelle anwendet.
/// </summary>
public sealed record RiskPolicy
{
    /// <summary>Höchste vom Benutzer freigegebene Risikostufe für interaktive Ausführung.</summary>
    public RiskLevel MaxInteractiveRisk { get; init; } = RiskLevel.Low;
    /// <summary>Höchste Risikostufe für unbeaufsichtigte Ausführung (Automatisierung, KI-Automatik). Nie höher als „Niedrig“.</summary>
    public RiskLevel MaxUnattendedRisk { get; init; } = RiskLevel.VeryLow;
    public bool ExperimentalUnlocked { get; init; }
    /// <summary>Aktions-IDs, die der Benutzer ausdrücklich für die unbeaufsichtigte Ausführung freigegeben hat. Leer = alle zulässigen.</summary>
    public IReadOnlySet<string>? UnattendedAllowList { get; init; }
    /// <summary>Aktions-IDs, die der Benutzer dauerhaft abgelehnt hat.</summary>
    public IReadOnlySet<string> Blocked { get; init; } = new HashSet<string>();

    public static bool IsUnattended(ExecutionSource source) => source is ExecutionSource.Automation or ExecutionSource.AiAuto or ExecutionSource.Gaming;

    public PermissionDecision Evaluate(ActionMetadata metadata, RiskLevel effectiveRisk, ExecutionSource source)
    {
        if (Blocked.Contains(metadata.Id))
            return PermissionDecision.Deny("Diese Aktion wurde von Ihnen gesperrt.");

        var risk = (RiskLevel)Math.Max((int)metadata.Risk, (int)effectiveRisk);
        var unattended = IsUnattended(source);

        if (metadata.IsExperimental || risk >= RiskLevel.Critical)
        {
            if (unattended || source is ExecutionSource.OneClick or ExecutionSource.Profile or ExecutionSource.AiAssisted or ExecutionSource.Chat or ExecutionSource.Tuning)
                return PermissionDecision.Deny("Experimentelle Änderungen können nur einzeln und manuell im Expertenlabor ausgeführt werden.");
            if (!ExperimentalUnlocked)
                return PermissionDecision.Deny("Das Expertenlabor ist nicht freigeschaltet.");
            return PermissionDecision.Allow(individualConfirmation: true);
        }

        if (unattended)
        {
            var limit = (RiskLevel)Math.Min((int)MaxUnattendedRisk, (int)RiskLevel.Low);
            if (!metadata.AllowInAutomation)
                return PermissionDecision.Deny(metadata.IsReversible
                    ? "Diese Aktion ist für die unbeaufsichtigte Ausführung nicht freigegeben."
                    : "Nicht umkehrbare Aktionen werden nie unbeaufsichtigt ausgeführt.");
            if (risk > limit)
                return PermissionDecision.Deny($"Die Risikostufe „{risk.ToGerman()}“ übersteigt das Limit für automatische Ausführung („{limit.ToGerman()}“).");
            if (UnattendedAllowList is { Count: > 0 } && !UnattendedAllowList.Contains(metadata.Id))
                return PermissionDecision.Deny("Diese Aktion steht nicht auf Ihrer Freigabeliste für automatische Ausführung.");
            return PermissionDecision.Allow();
        }

        if (risk > MaxInteractiveRisk)
            return PermissionDecision.Deny($"Die Risikostufe „{risk.ToGerman()}“ übersteigt Ihre freigegebene Stufe „{MaxInteractiveRisk.ToGerman()}“.");

        // Hohe Risiken und nicht umkehrbare Aktionen verlangen eine gesonderte Bestätigung je Aktion.
        return PermissionDecision.Allow(individualConfirmation: risk >= RiskLevel.High || !metadata.IsReversible);
    }
}
