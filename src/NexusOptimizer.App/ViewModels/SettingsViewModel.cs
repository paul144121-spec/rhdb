using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NexusOptimizer.AI;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Logging;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Data;
using NexusOptimizer.Windows.SystemAccess;

namespace NexusOptimizer.App.ViewModels;

/// <summary>Einstellungen: Änderungen werden sofort übernommen und gespeichert. API-Schlüssel liegen ausschließlich DPAPI-verschlüsselt im Benutzerprofil.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly IAiProviderFactory _providers;
    private readonly ChatEngine _chat;
    private readonly NexusDatabase _database;
    private readonly DialogService _dialogs;
    private readonly ThemeService _theme;
    private readonly FileLoggerProvider _logs;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, ISecretStore secrets, IAiProviderFactory providers, ChatEngine chat, NexusDatabase database, DialogService dialogs, ThemeService theme,
        FileLoggerProvider logs, ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _providers = providers;
        _chat = chat;
        _database = database;
        _dialogs = dialogs;
        _theme = theme;
        _logs = logs;
        _logger = logger;

        Themes = new[] { new Choice<string>("Dark", "Dunkel"), new Choice<string>("Light", "Hell") };
        AiProviders = new[]
        {
            new Choice<AiProviderKind>(AiProviderKind.RuleBased, "Nur Regelwerk (ohne Sprachmodell, vollständig lokal)"),
            new Choice<AiProviderKind>(AiProviderKind.Ollama, "Ollama (lokales Sprachmodell)"),
            new Choice<AiProviderKind>(AiProviderKind.Anthropic, "Claude (Anthropic, Cloud)"),
            new Choice<AiProviderKind>(AiProviderKind.OpenAiCompatible, "OpenAI-kompatibler Dienst"),
        };
        PrivacyModes = new[] { new Choice<PrivacyMode>(PrivacyMode.Strict, "Streng – keine Gerätenamen, Pfade, Prozess- oder Netzwerkdaten"), new Choice<PrivacyMode>(PrivacyMode.Balanced, "Ausgewogen – Hardwaremodelle und Prozessnamen, keine Kennungen") };
        Risks = new[] { RiskLevel.VeryLow, RiskLevel.Low, RiskLevel.Moderate, RiskLevel.High }.Select(r => new Choice<RiskLevel>(r, r.ToGerman())).ToList();
        UnattendedRisks = new[] { RiskLevel.VeryLow, RiskLevel.Low }.Select(r => new Choice<RiskLevel>(r, r.ToGerman())).ToList();
        LogLevels = new[] { "Debug", "Information", "Warning", "Error" }.Select(l => new Choice<string>(l, l)).ToList();
        Goals = Enum.GetValues<OptimizationGoal>().Select(g => new Choice<OptimizationGoal>(g, g.ToGerman())).ToList();
        Accents = ThemeService.AccentChoices.Select(a => new Choice<string>(a.Hex, a.Name)).ToList();
        Load();
    }

    public override string Key => "settings";
    public override string Title => "Einstellungen";
    public override string Subtitle => "Alle Änderungen werden sofort gespeichert. Es gibt keine versteckte Datenübermittlung.";
    protected override ILogger Logger => _logger;

    public IReadOnlyList<Choice<string>> Themes { get; }
    public IReadOnlyList<Choice<string>> Accents { get; }
    public IReadOnlyList<Choice<AiProviderKind>> AiProviders { get; }
    public IReadOnlyList<Choice<PrivacyMode>> PrivacyModes { get; }
    public IReadOnlyList<Choice<RiskLevel>> Risks { get; }
    public IReadOnlyList<Choice<RiskLevel>> UnattendedRisks { get; }
    public IReadOnlyList<Choice<string>> LogLevels { get; }
    public IReadOnlyList<Choice<OptimizationGoal>> Goals { get; }
    public string LogDirectory => _logs.Directory;
    public string DataDirectory => Path.GetDirectoryName(_database.FilePath) ?? "";
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    // Darstellung
    [ObservableProperty] private Choice<string>? _themeChoice;
    [ObservableProperty] private Choice<string>? _accent;
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private bool _animations;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _notifications;
    // Überwachung
    [ObservableProperty] private string _interval = "1";
    [ObservableProperty] private string _retention = "7";
    [ObservableProperty] private bool _persistHistory;
    [ObservableProperty] private bool _latencyProbe;
    [ObservableProperty] private string _latencyEndpoints = "";
    [ObservableProperty] private bool _nvidiaSensors;
    // Sicherheit
    [ObservableProperty] private Choice<RiskLevel>? _maxInteractive;
    [ObservableProperty] private Choice<RiskLevel>? _maxUnattended;
    [ObservableProperty] private bool _confirmEveryChange;
    [ObservableProperty] private bool _restorePointBeforeBatch;
    [ObservableProperty] private bool _restoreProfileOnExit;
    [ObservableProperty] private Choice<OptimizationGoal>? _goal;
    // KI
    [ObservableProperty] private Choice<AiProviderKind>? _aiProvider;
    [ObservableProperty] private Choice<Core.Settings.PrivacyMode>? _privacyChoice;
    [ObservableProperty] private string _ollamaEndpoint = "";
    [ObservableProperty] private string _ollamaModel = "";
    [ObservableProperty] private string _anthropicModel = "";
    [ObservableProperty] private string _openAiEndpoint = "";
    [ObservableProperty] private string _openAiModel = "";
    [ObservableProperty] private bool _cloudConsent;
    [ObservableProperty] private bool _saveChat;
    [ObservableProperty] private string _anthropicKey = "";
    [ObservableProperty] private string _openAiKey = "";
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _aiTestResult = "";
    [ObservableProperty] private string _contextPreview = "";
    [ObservableProperty] private bool _isTesting;
    // Erweitert
    [ObservableProperty] private Choice<string>? _logLevel;
    [ObservableProperty] private string _presentMonPath = "";
    [ObservableProperty] private string? _validation;

    public bool IsOllama => AiProvider?.Value == AiProviderKind.Ollama;
    public bool IsAnthropic => AiProvider?.Value == AiProviderKind.Anthropic;
    public bool IsOpenAi => AiProvider?.Value == AiProviderKind.OpenAiCompatible;
    public bool IsCloudCapable => AiProvider?.Value is AiProviderKind.Anthropic or AiProviderKind.OpenAiCompatible or AiProviderKind.Ollama;
    public bool HasLanguageModel => AiProvider?.Value != AiProviderKind.RuleBased;

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;
        ThemeChoice = Themes.First(t => t.Value == s.Theme);
        Accent = Accents.FirstOrDefault(a => string.Equals(a.Value, s.AccentColor, StringComparison.OrdinalIgnoreCase));
        FontScale = s.FontScale;
        Animations = s.AnimationsEnabled;
        StartWithWindows = s.StartWithWindows;
        StartMinimized = s.StartMinimized;
        Notifications = s.NotificationsEnabled;
        Interval = s.MonitoringIntervalSeconds.ToString("0.##");
        Retention = s.HistoryRetentionDays.ToString();
        PersistHistory = s.PersistHistory;
        LatencyProbe = s.LatencyProbeEnabled;
        LatencyEndpoints = string.Join(", ", s.LatencyEndpoints);
        NvidiaSensors = s.NvidiaSensorsEnabled;
        MaxInteractive = Risks.FirstOrDefault(r => r.Value == s.MaxInteractiveRisk) ?? Risks[1];
        MaxUnattended = UnattendedRisks.FirstOrDefault(r => r.Value == s.MaxUnattendedRisk) ?? UnattendedRisks[0];
        ConfirmEveryChange = s.ConfirmEveryChange;
        RestorePointBeforeBatch = s.CreateRestorePointBeforeBatch;
        RestoreProfileOnExit = s.RestoreProfileOnExit;
        Goal = Goals.First(g => g.Value == s.PrimaryGoal);
        AiProvider = AiProviders.First(p => p.Value == s.AiProvider);
        PrivacyChoice = PrivacyModes.First(p => p.Value == s.AiPrivacyMode);
        OllamaEndpoint = s.OllamaEndpoint;
        OllamaModel = s.OllamaModel;
        AnthropicModel = s.AnthropicModel;
        OpenAiEndpoint = s.OpenAiEndpoint;
        OpenAiModel = s.OpenAiModel;
        CloudConsent = s.CloudAiConsent;
        SaveChat = s.SaveChatHistory;
        LogLevel = LogLevels.First(l => l.Value == s.LogLevel);
        PresentMonPath = s.PresentMonPath ?? "";
        AnthropicKey = "";
        OpenAiKey = "";
        RefreshKeyStatus();
        _loading = false;
        OnPropertyChanged(nameof(IsOllama));
        OnPropertyChanged(nameof(IsAnthropic));
        OnPropertyChanged(nameof(IsOpenAi));
        OnPropertyChanged(nameof(IsCloudCapable));
        OnPropertyChanged(nameof(HasLanguageModel));
    }

    protected override Task OnNavigatedToAsync(object? parameter)
    {
        Load();
        return Task.CompletedTask;
    }

    private void RefreshKeyStatus()
        => KeyStatus = $"Claude: {(_secrets.Has(AiProviderFactory.AnthropicKeyName) ? "Schlüssel hinterlegt" : "kein Schlüssel")} · OpenAI-kompatibel: {(_secrets.Has(AiProviderFactory.OpenAiKeyName) ? "Schlüssel hinterlegt" : "kein Schlüssel")}";

    private void Apply(Func<AppSettings, AppSettings> change)
    {
        if (_loading) return;
        _settings.Update(change);
    }

    partial void OnThemeChoiceChanged(Choice<string>? value) { if (value is not null) Apply(s => s with { Theme = value.Value }); }
    partial void OnAccentChanged(Choice<string>? value) { if (value is not null) Apply(s => s with { AccentColor = value.Value }); }
    partial void OnFontScaleChanged(double value) => Apply(s => s with { FontScale = Math.Round(value, 2) });
    partial void OnAnimationsChanged(bool value) => Apply(s => s with { AnimationsEnabled = value });
    partial void OnStartWithWindowsChanged(bool value) => Apply(s => s with { StartWithWindows = value });
    partial void OnStartMinimizedChanged(bool value) => Apply(s => s with { StartMinimized = value });
    partial void OnNotificationsChanged(bool value) => Apply(s => s with { NotificationsEnabled = value });
    partial void OnPersistHistoryChanged(bool value) => Apply(s => s with { PersistHistory = value });
    partial void OnLatencyProbeChanged(bool value) => Apply(s => s with { LatencyProbeEnabled = value });
    partial void OnNvidiaSensorsChanged(bool value) => Apply(s => s with { NvidiaSensorsEnabled = value });
    partial void OnMaxInteractiveChanged(Choice<RiskLevel>? value) { if (value is not null) Apply(s => s with { MaxInteractiveRisk = value.Value }); }
    partial void OnMaxUnattendedChanged(Choice<RiskLevel>? value) { if (value is not null) Apply(s => s with { MaxUnattendedRisk = value.Value }); }
    partial void OnConfirmEveryChangeChanged(bool value) => Apply(s => s with { ConfirmEveryChange = value });
    partial void OnRestorePointBeforeBatchChanged(bool value) => Apply(s => s with { CreateRestorePointBeforeBatch = value });
    partial void OnRestoreProfileOnExitChanged(bool value) => Apply(s => s with { RestoreProfileOnExit = value });
    partial void OnGoalChanged(Choice<OptimizationGoal>? value) { if (value is not null) Apply(s => s with { PrimaryGoal = value.Value }); }
    partial void OnPrivacyChoiceChanged(Choice<Core.Settings.PrivacyMode>? value) { if (value is not null) Apply(s => s with { AiPrivacyMode = value.Value }); }
    partial void OnSaveChatChanged(bool value) => Apply(s => s with { SaveChatHistory = value });
    partial void OnLogLevelChanged(Choice<string>? value) { if (value is not null) Apply(s => s with { LogLevel = value.Value }); }

    partial void OnAiProviderChanged(Choice<AiProviderKind>? value)
    {
        OnPropertyChanged(nameof(IsOllama)); OnPropertyChanged(nameof(IsAnthropic)); OnPropertyChanged(nameof(IsOpenAi)); OnPropertyChanged(nameof(IsCloudCapable)); OnPropertyChanged(nameof(HasLanguageModel));
        if (value is not null) Apply(s => s with { AiProvider = value.Value });
        AiTestResult = "";
    }

    partial void OnCloudConsentChanged(bool value)
    {
        if (_loading) return;
        if (value) _ = ConfirmConsentAsync();
        else Apply(s => s with { CloudAiConsent = false });
    }

    private async Task ConfirmConsentAsync()
    {
        var ok = await _dialogs.ConfirmAsync("Datenübermittlung zustimmen",
            "Bei einem Cloud-Dienst (oder einem entfernten Ollama-/OpenAI-kompatiblen Server) verlässt der unten angezeigte Systemkontext Ihren Computer: Hardware- und Auslastungsdaten, je nach Datenschutzmodus auch Prozessnamen.\n\n" +
            "Nie übermittelt werden Passwörter, Zugangsdaten, persönliche Dokumente, Dateiinhalte oder Benutzer- und Computernamen. Die Zustimmung lässt sich jederzeit widerrufen.",
            "Zustimmen", acknowledgement: "Ich habe die Vorschau des Kontexts gelesen und stimme der Übermittlung zu.").ConfigureAwait(true);
        _loading = true;
        CloudConsent = ok;
        _loading = false;
        if (ok) _settings.Update(s => s with { CloudAiConsent = true });
    }

    // Texteingaben werden erst nach Prüfung übernommen.
    [RelayCommand]
    private void ApplyMonitoring()
    {
        Validation = null;
        if (!double.TryParse(Interval.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var interval) || interval is < 0.5 or > 30) { Validation = "Das Abtastintervall muss zwischen 0,5 und 30 Sekunden liegen."; return; }
        if (!int.TryParse(Retention, out var days) || days is < 1 or > 90) { Validation = "Die Aufbewahrung muss zwischen 1 und 90 Tagen liegen."; return; }
        var endpoints = LatencyEndpoints.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (endpoints.Count > 5 || endpoints.Any(e => e.Length > 253 || Uri.CheckHostName(e) == UriHostNameType.Unknown)) { Validation = "Bitte höchstens 5 gültige Hostnamen oder IP-Adressen für die Latenzmessung angeben."; return; }
        _settings.Update(s => s with { MonitoringIntervalSeconds = interval, HistoryRetentionDays = days, LatencyEndpoints = endpoints });
        Validation = "Überwachung gespeichert.";
    }

    [RelayCommand]
    private void ApplyAiEndpoints()
    {
        Validation = null;
        try
        {
            if (IsOllama) AiEndpointRules.Validate(OllamaEndpoint);
            if (IsOpenAi) AiEndpointRules.Validate(OpenAiEndpoint);
        }
        catch (AiProviderException ex)
        {
            Validation = ex.Message;
            return;
        }
        _settings.Update(s => s with { OllamaEndpoint = OllamaEndpoint.Trim(), OllamaModel = OllamaModel.Trim(), AnthropicModel = AnthropicModel.Trim(), OpenAiEndpoint = OpenAiEndpoint.Trim(), OpenAiModel = OpenAiModel.Trim() });
        Validation = "KI-Einstellungen gespeichert.";
    }

    [RelayCommand]
    private void SaveKeys()
    {
        if (AnthropicKey.Length > 0) _secrets.Set(AiProviderFactory.AnthropicKeyName, AnthropicKey.Trim());
        if (OpenAiKey.Length > 0) _secrets.Set(AiProviderFactory.OpenAiKeyName, OpenAiKey.Trim());
        AnthropicKey = "";
        OpenAiKey = "";
        RefreshKeyStatus();
    }

    [RelayCommand]
    private async Task RemoveKeysAsync()
    {
        if (!await _dialogs.ConfirmAsync("API-Schlüssel entfernen", "Alle gespeicherten API-Schlüssel werden gelöscht.", "Entfernen", danger: true).ConfigureAwait(true)) return;
        _secrets.Set(AiProviderFactory.AnthropicKeyName, null);
        _secrets.Set(AiProviderFactory.OpenAiKeyName, null);
        RefreshKeyStatus();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        AiTestResult = "Verbindung wird geprüft …";
        try
        {
            var provider = _providers.Create(_settings.Current, out var note);
            if (provider is null) { AiTestResult = note ?? "Es ist kein Sprachmodell eingestellt – es wird das Regelwerk verwendet."; return; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await provider.ValidateAsync(cts.Token).ConfigureAwait(true);
            AiTestResult = (result.Success ? "Erfolgreich: " : "Fehlgeschlagen: ") + result.Message + (result.Models.Count > 0 ? " Verfügbare Modelle: " + string.Join(", ", result.Models.Take(12)) : "");
        }
        catch (Exception ex) { AiTestResult = "Fehlgeschlagen: " + ex.Message; }
        finally { IsTesting = false; }
    }

    [RelayCommand]
    private async Task PreviewContextAsync()
    {
        try { ContextPreview = await _chat.PreviewContextAsync().ConfigureAwait(true); }
        catch (Exception ex) { ContextPreview = "Der Kontext konnte nicht erstellt werden: " + ex.Message; }
    }

    [RelayCommand]
    private void ApplyPresentMon()
    {
        var path = PresentMonPath.Trim().Trim('"');
        if (path.Length > 0 && (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !Path.GetFileName(path).StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase)))
        {
            Validation = "Bitte den vollständigen Pfad zu einer vorhandenen PresentMon-Programmdatei angeben.";
            return;
        }
        _settings.Update(s => s with { PresentMonPath = path.Length == 0 ? null : path });
        Validation = "Gespeichert. Die Programmdatei wird nicht mitgeliefert – laden Sie PresentMon selbst von der offiziellen Projektseite.";
    }

    [RelayCommand]
    private void BrowsePresentMon()
    {
        if (_dialogs.OpenFile("PresentMon (*.exe)|*.exe", "PresentMon auswählen") is { } path) PresentMonPath = path;
    }

    [RelayCommand]
    private void OpenLogs() => PageHelpers.ShowInExplorer(LogDirectory);

    [RelayCommand]
    private void OpenData() => PageHelpers.ShowInExplorer(DataDirectory);

    [RelayCommand]
    private async Task DeleteDataAsync()
    {
        var ok = await _dialogs.ConfirmAsync("Anwendungsdaten entfernen",
            "Gelöscht werden Einstellungen, Verlauf, Messwerte, Profile, Regeln, Chatverlauf und die gesicherten Ausgangszustände. Bereits angewendete Änderungen an Windows bleiben bestehen, lassen sich danach aber nicht mehr über diese Anwendung zurücknehmen.",
            "Alles löschen", danger: true, acknowledgement: "Ich verstehe, dass gesicherte Ausgangszustände unwiderruflich verloren gehen.").ConfigureAwait(true);
        if (!ok) return;
        try
        {
            await Task.Run(() => _database.DeleteAllData()).ConfigureAwait(true);
            _secrets.Set(AiProviderFactory.AnthropicKeyName, null);
            _secrets.Set(AiProviderFactory.OpenAiKeyName, null);
            _settings.Update(_ => new AppSettings());
            Load();
            await _dialogs.ShowMessageAsync("Daten entfernt", "Alle Anwendungsdaten wurden gelöscht. Die Anwendung verwendet wieder ihre Standardeinstellungen.").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Die Daten konnten nicht vollständig gelöscht werden: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (!await _dialogs.ConfirmAsync("Einstellungen zurücksetzen", "Alle Einstellungen werden auf die Standardwerte gesetzt. Verlauf und Profile bleiben erhalten.", "Zurücksetzen").ConfigureAwait(true)) return;
        _settings.Update(_ => new AppSettings());
        Load();
    }
}
