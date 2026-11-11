namespace NexusOptimizer.Core.Models;

/// <summary>Fünfstufige Risikoskala gemäß Spezifikation (Abschnitt 17.1).</summary>
public enum RiskLevel
{
    VeryLow = 1,
    Low = 2,
    Moderate = 3,
    High = 4,
    Critical = 5,
}

public enum ActionCategory
{
    Interface,
    Explorer,
    Responsiveness,
    Gaming,
    Power,
    BackgroundApps,
    Startup,
    Network,
    Storage,
    Privacy,
    Notifications,
    VisualEffects,
    Search,
    Taskbar,
    Compatibility,
    AdvancedSystem,
    Cpu,
    Gpu,
    Memory,
    Services,
    Cleanup,
    Repair,
    Backup,
    Process,
}

public enum PrivilegeLevel
{
    User = 0,
    Administrator = 1,
}

public enum RestartRequirement
{
    None = 0,
    ExplorerRestart = 1,
    SignOut = 2,
    Reboot = 3,
}

/// <summary>Woher eine Ausführungsanforderung stammt. Bestimmt, welche Risikoregeln gelten.</summary>
public enum ExecutionSource
{
    User,
    OneClick,
    Chat,
    AiAssisted,
    AiAuto,
    Automation,
    Profile,
    Gaming,
    Tuning,
}

public enum TransactionStatus
{
    /// <summary>Im Journal angelegt, noch keine Systemänderung.</summary>
    Pending,
    /// <summary>Ursprungszustand gesichert, Änderung noch nicht (vollständig) angewendet.</summary>
    BackedUp,
    /// <summary>Änderung angewendet, Verifikation steht aus.</summary>
    Applied,
    /// <summary>Änderung angewendet und durch Rücklesen bestätigt.</summary>
    Verified,
    /// <summary>Änderung angewendet, Rücklesen ergab jedoch einen abweichenden Zustand.</summary>
    VerificationFailed,
    /// <summary>Änderung fehlgeschlagen.</summary>
    Failed,
    RolledBack,
    RollbackFailed,
    /// <summary>Beim Programmstart als unvollständig erkannt (Absturz, Stromausfall).</summary>
    Interrupted,
}

public static class EnumDisplay
{
    public static string ToGerman(this RiskLevel level) => level switch
    {
        RiskLevel.VeryLow => "Sehr niedrig",
        RiskLevel.Low => "Niedrig",
        RiskLevel.Moderate => "Mittel",
        RiskLevel.High => "Hoch",
        RiskLevel.Critical => "Experimentell / Kritisch",
        _ => level.ToString(),
    };

    public static string ToGermanLong(this RiskLevel level) => level switch
    {
        RiskLevel.VeryLow => "Stufe 1 – Sehr niedriges Risiko: einfache, leicht umkehrbare Einstellung.",
        RiskLevel.Low => "Stufe 2 – Niedriges Risiko: unterstützte Konfigurationsänderung mit begrenzten Nebenwirkungen.",
        RiskLevel.Moderate => "Stufe 3 – Mittleres Risiko: kann Funktionen oder den Ressourcenverbrauch beeinflussen.",
        RiskLevel.High => "Stufe 4 – Hohes Risiko: kann Kompatibilität, Zuverlässigkeit, Stabilität, Sicherheit oder Konnektivität beeinträchtigen.",
        RiskLevel.Critical => "Stufe 5 – Experimentell/Kritisch: betrifft Startverhalten, Sicherheitsfunktionen oder kernelnahe Konfiguration.",
        _ => level.ToString(),
    };

    public static string ToGerman(this ActionCategory category) => category switch
    {
        ActionCategory.Interface => "Windows-Oberfläche",
        ActionCategory.Explorer => "Explorer-Verhalten",
        ActionCategory.Responsiveness => "Reaktionsverhalten des Desktops",
        ActionCategory.Gaming => "Gaming",
        ActionCategory.Power => "Energieverwaltung",
        ActionCategory.BackgroundApps => "Hintergrund-Apps",
        ActionCategory.Startup => "Autostart",
        ActionCategory.Network => "Netzwerk",
        ActionCategory.Storage => "Speicher",
        ActionCategory.Privacy => "Datenschutz",
        ActionCategory.Notifications => "Benachrichtigungen",
        ActionCategory.VisualEffects => "Visuelle Effekte",
        ActionCategory.Search => "Suche",
        ActionCategory.Taskbar => "Taskleiste",
        ActionCategory.Compatibility => "Anwendungskompatibilität",
        ActionCategory.AdvancedSystem => "Erweiterte Systemkonfiguration",
        ActionCategory.Cpu => "Prozessor",
        ActionCategory.Gpu => "Grafik",
        ActionCategory.Memory => "Arbeitsspeicher",
        ActionCategory.Services => "Dienste",
        ActionCategory.Cleanup => "Bereinigung",
        ActionCategory.Repair => "Reparatur",
        ActionCategory.Backup => "Sicherung",
        ActionCategory.Process => "Prozesse",
        _ => category.ToString(),
    };

    public static string ToGerman(this PrivilegeLevel level) => level switch
    {
        PrivilegeLevel.User => "Standardbenutzer",
        PrivilegeLevel.Administrator => "Administrator (UAC-Bestätigung)",
        _ => level.ToString(),
    };

    public static string ToGerman(this RestartRequirement restart) => restart switch
    {
        RestartRequirement.None => "Kein Neustart erforderlich",
        RestartRequirement.ExplorerRestart => "Explorer-Neustart oder erneutes Öffnen betroffener Fenster",
        RestartRequirement.SignOut => "Ab- und Anmelden erforderlich",
        RestartRequirement.Reboot => "Neustart des Computers erforderlich",
        _ => restart.ToString(),
    };

    public static string ToGerman(this TransactionStatus status) => status switch
    {
        TransactionStatus.Pending => "Vorbereitet",
        TransactionStatus.BackedUp => "Gesichert",
        TransactionStatus.Applied => "Angewendet (unbestätigt)",
        TransactionStatus.Verified => "Angewendet und verifiziert",
        TransactionStatus.VerificationFailed => "Verifikation fehlgeschlagen",
        TransactionStatus.Failed => "Fehlgeschlagen",
        TransactionStatus.RolledBack => "Rückgängig gemacht",
        TransactionStatus.RollbackFailed => "Rückgängigmachen fehlgeschlagen",
        TransactionStatus.Interrupted => "Unterbrochen",
        _ => status.ToString(),
    };

    public static string ToGerman(this ExecutionSource source) => source switch
    {
        ExecutionSource.User => "Manuell",
        ExecutionSource.OneClick => "Ein-Klick-Optimierung",
        ExecutionSource.Chat => "KI-Chat (bestätigt)",
        ExecutionSource.AiAssisted => "KI-assistiert (bestätigt)",
        ExecutionSource.AiAuto => "KI-Automatik",
        ExecutionSource.Automation => "Automatisierungsregel",
        ExecutionSource.Profile => "Profil",
        ExecutionSource.Gaming => "Gaming-Modus",
        ExecutionSource.Tuning => "Tuning-Experiment",
        _ => source.ToString(),
    };
}
