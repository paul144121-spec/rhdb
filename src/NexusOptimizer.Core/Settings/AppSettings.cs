using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;

namespace NexusOptimizer.Core.Settings;

public enum AiProviderKind
{
    /// <summary>Deterministisches Regelwerk – immer verfügbar, ohne Netzwerk.</summary>
    RuleBased,
    /// <summary>Lokales Sprachmodell über einen Ollama-Dienst auf diesem Computer.</summary>
    Ollama,
    /// <summary>Cloud-Sprachmodell über die Anthropic-API (Opt-in).</summary>
    Anthropic,
    /// <summary>Cloud- oder Eigenbetrieb über eine OpenAI-kompatible Chat-API (Opt-in).</summary>
    OpenAiCompatible,
}

public enum AiMode
{
    Analysis,
    Recommendation,
    Assisted,
    Auto,
    Gaming,
    Troubleshooting,
    Learning,
}

public enum PrivacyMode
{
    /// <summary>Keine Gerätenamen, Pfade, Prozessnamen oder Netzwerkdetails im KI-Kontext.</summary>
    Strict,
    /// <summary>Hardwaremodelle und Prozessnamen, aber keine Benutzer-, Geräte- oder Netzwerkkennungen.</summary>
    Balanced,
}

/// <summary>Sämtliche Anwendungseinstellungen (Abschnitt 29). Wird als Ganzes dauerhaft gespeichert.</summary>
public sealed record AppSettings
{
    // Allgemein
    public string Language { get; init; } = "de-DE";
    public string Theme { get; init; } = "Dark";
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public string? DefaultProfileId { get; init; }

    // Darstellung
    public string AccentColor { get; init; } = "#7C5CFF";
    public bool AnimationsEnabled { get; init; } = true;
    public double FontScale { get; init; } = 1.0;
    public List<string> DashboardWidgetOrder { get; init; } = new();
    public List<string> DashboardHiddenWidgets { get; init; } = new();

    // Überwachung
    public double MonitoringIntervalSeconds { get; init; } = 1.0;
    public int HistoryRetentionDays { get; init; } = 7;
    public bool PersistHistory { get; init; } = true;
    public List<AlertRule> AlertRules { get; init; } = AlertEngine.DefaultRules.ToList();
    public bool LatencyProbeEnabled { get; init; }
    /// <summary>Vom Benutzer freigegebene Ziele für die Latenzmessung (IP-Adressen oder Hostnamen).</summary>
    public List<string> LatencyEndpoints { get; init; } = new();

    // KI
    public AiProviderKind AiProvider { get; init; } = AiProviderKind.RuleBased;
    public AiMode AiMode { get; init; } = AiMode.Recommendation;
    public string OllamaEndpoint { get; init; } = "http://localhost:11434";
    public string OllamaModel { get; init; } = "";
    public string AnthropicModel { get; init; } = "claude-sonnet-5-5";
    public string OpenAiEndpoint { get; init; } = "";
    public string OpenAiModel { get; init; } = "";
    /// <summary>Ausdrückliche Zustimmung, Systemkontext an einen Cloud-Dienst zu senden.</summary>
    public bool CloudAiConsent { get; init; }
    public PrivacyMode AiPrivacyMode { get; init; } = PrivacyMode.Strict;
    public bool SaveChatHistory { get; init; } = true;
    public OptimizationGoal PrimaryGoal { get; init; } = OptimizationGoal.BalancedPerformance;

    // Optimierung
    public RiskLevel MaxInteractiveRisk { get; init; } = RiskLevel.Low;
    public RiskLevel MaxUnattendedRisk { get; init; } = RiskLevel.VeryLow;
    public bool AutomationEnabled { get; init; }
    public bool AiAutoEnabled { get; init; }
    public List<string> UnattendedAllowList { get; init; } = new();
    public List<string> BlockedActions { get; init; } = new();
    public bool CreateRestorePointBeforeBatch { get; init; }
    public bool RestoreProfileOnExit { get; init; }

    // Sicherheit
    public bool ExperimentalUnlocked { get; init; }
    public DateTimeOffset? ExperimentalUnlockedAt { get; init; }
    public bool ConfirmEveryChange { get; init; } = true;

    // Gaming
    public bool AutoGamingMode { get; init; }
    public string? PresentMonPath { get; init; }

    // Erweitert
    public string LogLevel { get; init; } = "Information";
    public bool NvidiaSensorsEnabled { get; init; } = true;
    public bool DeveloperDiagnostics { get; init; }
    public string? UpdateManifestUrl { get; init; }

    /// <summary>Leitet die wirksame Risikofreigabe aus den Einstellungen ab. Unbeaufsichtigt nie höher als „Niedrig“.</summary>
    public RiskPolicy ToRiskPolicy() => new()
    {
        MaxInteractiveRisk = ExperimentalUnlocked ? MaxInteractiveRisk : (RiskLevel)Math.Min((int)MaxInteractiveRisk, (int)RiskLevel.High),
        MaxUnattendedRisk = (RiskLevel)Math.Min((int)MaxUnattendedRisk, (int)RiskLevel.Low),
        ExperimentalUnlocked = ExperimentalUnlocked,
        UnattendedAllowList = UnattendedAllowList.Count > 0 ? UnattendedAllowList.ToHashSet(StringComparer.Ordinal) : null,
        Blocked = BlockedActions.ToHashSet(StringComparer.Ordinal),
    };

    /// <summary>Korrigiert außerhalb zulässiger Bereiche liegende Werte (z. B. nach manueller Bearbeitung oder beschädigter Datei).</summary>
    public AppSettings Sanitize() => this with
    {
        Theme = Theme is "Light" or "Dark" ? Theme : "Dark",
        AccentColor = IsHexColor(AccentColor) ? AccentColor : "#7C5CFF",
        FontScale = Math.Clamp(double.IsFinite(FontScale) ? FontScale : 1.0, 0.85, 1.5),
        MonitoringIntervalSeconds = Math.Clamp(double.IsFinite(MonitoringIntervalSeconds) ? MonitoringIntervalSeconds : 1.0, 0.5, 30),
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 1, 90),
        MaxInteractiveRisk = (RiskLevel)Math.Clamp((int)MaxInteractiveRisk, 1, 5),
        MaxUnattendedRisk = (RiskLevel)Math.Clamp((int)MaxUnattendedRisk, 1, 2),
        LatencyEndpoints = LatencyEndpoints.Where(e => !string.IsNullOrWhiteSpace(e) && e.Length <= 253 && Uri.CheckHostName(e) != UriHostNameType.Unknown).Distinct().Take(5).ToList(),
        AlertRules = AlertRules ?? AlertEngine.DefaultRules.ToList(),
        LogLevel = LogLevel is "Debug" or "Information" or "Warning" or "Error" ? LogLevel : "Information",
        // Automatik setzt voraus, dass die jeweilige Funktion ausdrücklich eingeschaltet wurde.
        ExperimentalUnlockedAt = ExperimentalUnlocked ? ExperimentalUnlockedAt : null,
    };

    private static bool IsHexColor(string? value)
        => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>Sichere Ablage von Geheimnissen (API-Schlüsseln) über Betriebssystemmittel.</summary>
public interface ISecretStore
{
    void Set(string name, string? secret);
    string? Get(string name);
    bool Has(string name);
}

/// <summary>Hält die aktuellen Einstellungen und benachrichtigt bei Änderungen.</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private AppSettings _current;
    private readonly object _gate = new();

    public SettingsService(ISettingsStore store)
    {
        _store = store;
        try { _current = store.Load().Sanitize(); }
        catch (Exception) { _current = new AppSettings(); }
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current { get { lock (_gate) return _current; } }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_gate)
        {
            updated = change(_current).Sanitize();
            if (updated == _current) return updated;
            _current = updated;
            _store.Save(updated);
        }
        Changed?.Invoke(this, updated);
        return updated;
    }
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings = new();
    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) => _settings = settings;
}
