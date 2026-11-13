using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NexusOptimizer.AI;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Automation;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Reporting;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Updates;
using NexusOptimizer.Data;

namespace NexusOptimizer.App.ViewModels;

// ───────────────────────── Profile ─────────────────────────

public sealed record ProfileRow(OptimizationProfile Profile, bool IsActive)
{
    public string Name => Profile.Name;
    public string Description => string.IsNullOrWhiteSpace(Profile.Description) ? "Keine Beschreibung." : Profile.Description;
    public bool IsBuiltIn => Profile.IsBuiltIn;
    public bool IsCustom => !Profile.IsBuiltIn;
    public string Goal => "Ziel: " + Profile.Goal.ToGerman();
    public string Count => $"{Profile.Items.Count} Einstellung(en)" + (Profile.PowerPlan is { } ? " + Energieplan" : "");
}

/// <summary>Optimierungsprofile: aktivieren (mit Vorschau), zurücksetzen, klonen, vergleichen, importieren und exportieren.</summary>
public sealed partial class ProfilesViewModel : PageViewModel
{
    private readonly ProfileManager _profiles;
    private readonly ActionCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ActionCatalog _catalog;
    private readonly ILogger<ProfilesViewModel> _logger;

    public ProfilesViewModel(ProfileManager profiles, ActionCoordinator coordinator, SettingsService settings, DialogService dialogs, ActionCatalog catalog, ILogger<ProfilesViewModel> logger)
    {
        _profiles = profiles;
        _coordinator = coordinator;
        _settings = settings;
        _dialogs = dialogs;
        _catalog = catalog;
        _logger = logger;
        _profiles.ActiveProfileChanged += (_, _) => Ui.Post(Reload);
    }

    public override string Key => "profiles";
    public override string Title => "Optimierungsprofile";
    public override string Subtitle => "Gespeicherte Zusammenstellungen von Einstellungen. Beim Wechsel werden die Änderungen des bisherigen Profils zuerst zurückgenommen.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<ProfileRow> Rows { get; } = new();
    public ObservableCollection<ProfileDifference> Differences { get; } = new();
    [ObservableProperty] private ProfileRow? _selected;
    [ObservableProperty] private ProfileRow? _compareWith;
    [ObservableProperty] private string _activeText = "Kein Profil aktiv.";
    [ObservableProperty] private bool _hasActive;
    [ObservableProperty] private string _compareText = "";

    partial void OnSelectedChanged(ProfileRow? value) => UpdateCompare();
    partial void OnCompareWithChanged(ProfileRow? value) => UpdateCompare();

    protected override Task OnNavigatedToAsync(object? parameter)
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var activeId = _profiles.ActiveProfile?.Id;
        var selectedId = Selected?.Profile.Id;
        Rows.Clear();
        foreach (var profile in _profiles.GetAll()) Rows.Add(new ProfileRow(profile, profile.Id == activeId));
        Selected = Rows.FirstOrDefault(r => r.Profile.Id == selectedId);
        HasActive = activeId is not null;
        ActiveText = _profiles.ActiveProfile is { } active ? $"Aktiv: {active.Name}" : "Kein Profil aktiv.";
    }

    private void UpdateCompare()
    {
        Differences.Clear();
        if (Selected is null || CompareWith is null) { CompareText = ""; return; }
        foreach (var difference in _profiles.Compare(Selected.Profile, CompareWith.Profile)) Differences.Add(difference);
        CompareText = Differences.Count == 0 ? "Die beiden Profile enthalten dieselben Einstellungen." : $"{Differences.Count} Unterschied(e) zwischen „{Selected.Name}“ und „{CompareWith.Name}“.";
    }

    [RelayCommand]
    private Task ActivateAsync(ProfileRow? row) => GuardAsync(async () =>
    {
        if (row is null) return;
        var settings = _settings.Current;
        var snapshotContext = await Task.Run(() => _coordinator.GetContextAsync()).ConfigureAwait(true);
        var (plan, note) = await _profiles.PreviewAsync(row.Profile, settings.ToRiskPolicy(), snapshotContext, ExecutionSource.User).ConfigureAwait(true);

        if (_profiles.ActiveProfile is { } current && current.Id != row.Profile.Id)
        {
            if (!await _dialogs.ConfirmAsync("Profil wechseln", $"Das aktive Profil „{current.Name}“ wird zuerst zurückgenommen, danach wird „{row.Name}“ angewendet.", "Wechseln").ConfigureAwait(true)) return;
        }
        var restored = await _profiles.ResetActiveAsync().ConfigureAwait(true);
        if (restored.Any(r => !r.Success && !r.Conflict))
        {
            await _dialogs.ShowMessageAsync("Profil nicht gewechselt", "Das bisherige Profil konnte nicht vollständig zurückgesetzt werden. Das neue Profil wurde nicht angewendet.", string.Join(Environment.NewLine, restored.Where(r => !r.Success).Select(r => r.Transaction.ActionName + ": " + r.Message))).ConfigureAwait(true);
            return;
        }
        var result = await _coordinator.RunPlanAsync(plan, "Profil „" + row.Name + "“", "Profil " + row.Name, note is null ? null : new[] { note }).ConfigureAwait(true);
        if (result is { Busy: false } && result.SucceededCount > 0) _profiles.MarkActive(row.Profile.Id, result.BatchId);
        else if (plan.IsEmpty && plan.Rejected.All(r => r.Kind == RejectionKind.AlreadyApplied)) _profiles.MarkActive(row.Profile.Id, null);
        Reload();
    }, "Das Profil konnte nicht aktiviert werden.");

    [RelayCommand]
    private Task ResetAsync() => GuardAsync(async () =>
    {
        if (_profiles.ActiveProfile is not { } active) return;
        if (!await _dialogs.ConfirmAsync("Profil zurücksetzen", $"Alle Änderungen des Profils „{active.Name}“ werden zurückgenommen.", "Zurücksetzen").ConfigureAwait(true)) return;
        var outcomes = await _profiles.ResetActiveAsync().ConfigureAwait(true);
        var failed = outcomes.Where(o => !o.Success).ToList();
        await _dialogs.ShowMessageAsync(failed.Count == 0 ? "Profil zurückgesetzt" : "Teilweise zurückgesetzt",
            failed.Count == 0 ? $"{outcomes.Count} Änderung(en) wurden zurückgenommen." : $"{outcomes.Count - failed.Count} zurückgenommen, {failed.Count} nicht.",
            failed.Count == 0 ? null : string.Join(Environment.NewLine, failed.Select(o => o.Transaction.ActionName + ": " + o.Message))).ConfigureAwait(true);
        Reload();
    }, "Das Profil konnte nicht zurückgesetzt werden.");

    [RelayCommand]
    private Task CloneAsync(ProfileRow? row) => GuardAsync(async () =>
    {
        if (row is null) return;
        var name = await _dialogs.PromptAsync("Profil klonen", "Name der Kopie:", row.Name + " (Kopie)").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        _profiles.Clone(row.Profile, name.Trim());
        Reload();
    }, "Das Profil konnte nicht geklont werden.");

    [RelayCommand]
    private Task DeleteAsync(ProfileRow? row) => GuardAsync(async () =>
    {
        if (row is not { IsCustom: true }) return;
        if (!await _dialogs.ConfirmAsync("Profil löschen", $"„{row.Name}“ wird gelöscht. Bereits angewendete Änderungen bleiben bestehen.", "Löschen", danger: true).ConfigureAwait(true)) return;
        _profiles.Delete(row.Profile);
        Reload();
    }, "Das Profil konnte nicht gelöscht werden.");

    [RelayCommand]
    private Task CreateFromActiveAsync() => GuardAsync(async () =>
    {
        var name = await _dialogs.PromptAsync("Neues Profil", "Name des Profils:", "Mein Profil").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = _profiles.Create(name.Trim(), "Eigenes Profil");
        Reload();
        Selected = Rows.FirstOrDefault(r => r.Profile.Id == profile.Id);
        await _dialogs.ShowMessageAsync("Profil angelegt", "Das neue Profil ist noch leer. Fügen Sie Einstellungen hinzu, indem Sie ein eingebautes Profil klonen, oder importieren Sie eine Profildatei.").ConfigureAwait(true);
    }, "Das Profil konnte nicht angelegt werden.");

    [RelayCommand]
    private Task ExportAsync(ProfileRow? row) => GuardAsync(async () =>
    {
        if (row is null) return;
        var path = _dialogs.SaveFile("Profildatei (*.json)|*.json", SafeName(row.Name) + ".json", "Profil exportieren");
        if (path is null) return;
        await File.WriteAllTextAsync(path, ProfileSerializer.Export(row.Profile), Encoding.UTF8).ConfigureAwait(true);
    }, "Das Profil konnte nicht exportiert werden.");

    [RelayCommand]
    private Task ImportAsync() => GuardAsync(async () =>
    {
        var path = _dialogs.OpenFile("Profildatei (*.json)|*.json", "Profil importieren");
        if (path is null) return;
        var info = new FileInfo(path);
        if (info.Length > ProfileSerializer.MaxImportBytes)
        {
            await _dialogs.ShowMessageAsync("Datei zu groß", "Profildateien dürfen höchstens 256 KB groß sein.").ConfigureAwait(true);
            return;
        }
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(true);
        var result = ProfileSerializer.Import(json, _catalog);
        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync("Import nicht möglich", "Die Datei enthält kein gültiges Profil. Es wurde nichts importiert.", string.Join(Environment.NewLine, result.Errors)).ConfigureAwait(true);
            return;
        }
        var clone = _profiles.Clone(result.Profile!, result.Profile!.Name);
        Reload();
        var warnings = result.Warnings.Count > 0 ? string.Join(Environment.NewLine, result.Warnings) : null;
        await _dialogs.ShowMessageAsync("Profil importiert", $"„{clone.Name}“ wurde importiert. Beim Aktivieren sehen Sie wie immer zuerst eine Vorschau; nicht erlaubte Einträge wurden verworfen.", warnings).ConfigureAwait(true);
    }, "Das Profil konnte nicht importiert werden.");

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

// ───────────────────────── Automatisierung ─────────────────────────

public sealed record RuleRow(AutomationRule Rule, string Summary, IReadOnlyList<string> Problems)
{
    public string Name => Rule.Name;
    public bool Enabled => Rule.Enabled;
    public bool HasProblems => Problems.Count > 0;
}

public sealed record RunRow(AutomationRunRecord Record)
{
    public string Time => Record.Timestamp.ToLocalTime().ToString("dd.MM. HH:mm:ss");
    public string Title => Record.RuleName + (Record.Skipped ? " (übersprungen)" : Record.Success ? "" : " (fehlgeschlagen)");
    public string Message => Record.Message;
}

/// <summary>Automatisierungscenter: Wenn-Dann-Regeln mit Bedingungen, Abklingzeit, Konflikterkennung und Protokoll. Unbeaufsichtigt gelten stets die Risikogrenzen.</summary>
public sealed partial class AutomationViewModel : PageViewModel
{
    private readonly IAutomationStore _store;
    private readonly SettingsService _settings;
    private readonly ProfileManager _profiles;
    private readonly ActionCatalog _catalog;
    private readonly DialogService _dialogs;
    private readonly ILogger<AutomationViewModel> _logger;

    public AutomationViewModel(IAutomationStore store, SettingsService settings, ProfileManager profiles, ActionCatalog catalog, DialogService dialogs, ILogger<AutomationViewModel> logger)
    {
        _store = store;
        _settings = settings;
        _profiles = profiles;
        _catalog = catalog;
        _dialogs = dialogs;
        _logger = logger;

        Triggers = new[]
        {
            new Choice<TriggerType>(TriggerType.GameStarted, "Ein Spiel startet"), new Choice<TriggerType>(TriggerType.GameExited, "Ein Spiel wird beendet"),
            new Choice<TriggerType>(TriggerType.ProcessStarted, "Ein Programm startet"), new Choice<TriggerType>(TriggerType.ProcessExited, "Ein Programm wird beendet"),
            new Choice<TriggerType>(TriggerType.AcConnected, "Netzteil angeschlossen"), new Choice<TriggerType>(TriggerType.BatteryMode, "Wechsel in den Akkubetrieb"),
            new Choice<TriggerType>(TriggerType.CpuHigh, "CPU-Auslastung über Schwelle"), new Choice<TriggerType>(TriggerType.MemoryPressure, "RAM-Auslastung über Schwelle"),
            new Choice<TriggerType>(TriggerType.ThermalHigh, "Temperatur über Schwelle"), new Choice<TriggerType>(TriggerType.Schedule, "Zu einer Uhrzeit"),
        };
        ActionTypes = new[]
        {
            new Choice<RuleActionType>(RuleActionType.ActivateProfile, "Profil aktivieren"), new Choice<RuleActionType>(RuleActionType.RestoreRuleChanges, "Änderungen dieser Regel zurücknehmen"),
            new Choice<RuleActionType>(RuleActionType.Notify, "Nur benachrichtigen"),
        };
        _selectedTrigger = Triggers[0];
        _selectedActionType = ActionTypes[0];
    }

    public override string Key => "automation";
    public override string Title => "Automatisierungscenter";
    public override string Subtitle => "Regeln wie „Wenn ein Spiel startet, aktiviere das Gaming-Profil“. Automatisch ausgeführt wird nur, was die Risikogrenzen zulassen.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<RuleRow> Rules { get; } = new();
    public ObservableCollection<RunRow> History { get; } = new();
    public ObservableCollection<string> Conflicts { get; } = new();
    public ObservableCollection<Choice<string>> ProfileChoices { get; } = new();
    public IReadOnlyList<Choice<TriggerType>> Triggers { get; }
    public IReadOnlyList<Choice<RuleActionType>> ActionTypes { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private Choice<TriggerType> _selectedTrigger;
    [ObservableProperty] private Choice<RuleActionType> _selectedActionType;
    [ObservableProperty] private Choice<string>? _selectedProfile;
    [ObservableProperty] private string _processName = "";
    [ObservableProperty] private string _threshold = "90";
    [ObservableProperty] private string _timeOfDay = "20:00";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string? _formError;
    [ObservableProperty] private bool _loading;
    public bool NeedsProcess => SelectedTrigger.Value is TriggerType.ProcessStarted or TriggerType.ProcessExited;
    public bool NeedsThreshold => SelectedTrigger.Value is TriggerType.CpuHigh or TriggerType.MemoryPressure or TriggerType.ThermalHigh;
    public bool NeedsTime => SelectedTrigger.Value == TriggerType.Schedule;
    public bool NeedsProfile => SelectedActionType.Value == RuleActionType.ActivateProfile;
    public bool NeedsMessage => SelectedActionType.Value == RuleActionType.Notify;

    partial void OnSelectedTriggerChanged(Choice<TriggerType> value) { OnPropertyChanged(nameof(NeedsProcess)); OnPropertyChanged(nameof(NeedsThreshold)); OnPropertyChanged(nameof(NeedsTime)); }
    partial void OnSelectedActionTypeChanged(Choice<RuleActionType> value) { OnPropertyChanged(nameof(NeedsProfile)); OnPropertyChanged(nameof(NeedsMessage)); }

    partial void OnEnabledChanged(bool value)
    {
        if (Loading) return;
        if (value && !_settings.Current.AutomationEnabled)
            _ = ConfirmEnableAsync();
        else
            _settings.Update(s => s with { AutomationEnabled = value });
    }

    private async Task ConfirmEnableAsync()
    {
        var ok = await _dialogs.ConfirmAsync("Automatisierung einschalten",
            "Regeln werden danach ohne Rückfrage ausgeführt. Dabei gilt stets: höchstens die in den Einstellungen freigegebene unbeaufsichtigte Risikostufe (nie höher als „Niedrig“), nur umkehrbare und für Automatik zugelassene Aktionen, Abklingzeit und Schutz vor Schwingen. Alles steht im Verlauf.",
            "Einschalten", acknowledgement: "Ich habe die Grenzen der Automatisierung verstanden.").ConfigureAwait(true);
        if (ok) _settings.Update(s => s with { AutomationEnabled = true });
        else { Loading = true; Enabled = false; Loading = false; }
    }

    protected override Task OnNavigatedToAsync(object? parameter)
    {
        Loading = true;
        Enabled = _settings.Current.AutomationEnabled;
        Loading = false;
        ProfileChoices.Clear();
        foreach (var profile in _profiles.GetAll()) ProfileChoices.Add(new Choice<string>(profile.Id, profile.Name));
        SelectedProfile ??= ProfileChoices.FirstOrDefault();
        Reload();
        return Task.CompletedTask;
    }

    private string Describe(AutomationRule rule)
    {
        var trigger = Triggers.FirstOrDefault(t => t.Value == rule.Trigger.Type)?.Label ?? rule.Trigger.Type.ToString();
        if (rule.Trigger.ProcessName is { } p) trigger += $" „{p}“";
        if (rule.Trigger.Threshold is { } t) trigger += $" ({t:0})";
        if (rule.Trigger.TimeOfDay is { } time) trigger += $" ({time})";
        var actions = string.Join(", ", rule.Actions.Select(a => a.Type switch
        {
            RuleActionType.ActivateProfile => "Profil „" + (_profiles.Find(a.ProfileId ?? "")?.Name ?? "unbekannt") + "“ aktivieren",
            RuleActionType.ApplyAction => "Aktion " + (a.ActionId is { } id && _catalog.Find(id) is { } action ? action.Metadata.Name : a.ActionId),
            RuleActionType.RestoreRuleChanges => "Änderungen zurücknehmen",
            RuleActionType.Notify => "benachrichtigen",
            _ => a.Type.ToString(),
        }));
        return $"Wenn: {trigger} → Dann: {actions} · Abklingzeit {rule.CooldownSeconds} s";
    }

    private void Reload()
    {
        var rules = _store.GetRules();
        Rules.Clear();
        foreach (var rule in rules) Rules.Add(new RuleRow(rule, Describe(rule), AutomationAnalysis.Validate(rule)));
        Conflicts.Clear();
        foreach (var conflict in AutomationAnalysis.DetectConflicts(rules)) Conflicts.Add(conflict.Description);
        History.Clear();
        foreach (var record in _store.GetHistory(null, 60)) History.Add(new RunRow(record));
    }

    [RelayCommand]
    private void AddRule()
    {
        FormError = null;
        if (string.IsNullOrWhiteSpace(NewName)) { FormError = "Bitte einen Namen für die Regel angeben."; return; }
        var trigger = new AutomationTrigger { Type = SelectedTrigger.Value };
        if (NeedsProcess)
        {
            var name = ProcessName.Trim();
            if (name.Length == 0 || name.Length > 100 || name.Any(c => Path.GetInvalidFileNameChars().Contains(c))) { FormError = "Bitte einen gültigen Prozessnamen angeben (z. B. game.exe)."; return; }
            trigger = trigger with { ProcessName = name };
        }
        if (NeedsThreshold)
        {
            if (!double.TryParse(Threshold, out var value) || value is <= 0 or > 120) { FormError = "Die Schwelle muss eine Zahl zwischen 1 und 120 sein."; return; }
            trigger = trigger with { Threshold = value };
        }
        if (NeedsTime)
        {
            if (!TimeOnly.TryParseExact(TimeOfDay.Trim(), "HH:mm", null, System.Globalization.DateTimeStyles.None, out var time)) { FormError = "Bitte die Uhrzeit im Format HH:mm angeben."; return; }
            trigger = trigger with { TimeOfDay = time.ToString("HH:mm") };
        }

        var action = new AutomationAction { Type = SelectedActionType.Value };
        if (NeedsProfile)
        {
            if (SelectedProfile is null) { FormError = "Bitte ein Profil wählen."; return; }
            action = action with { ProfileId = SelectedProfile.Value };
        }
        if (NeedsMessage) action = action with { Message = string.IsNullOrWhiteSpace(Message) ? NewName.Trim() : Message.Trim() };

        var rule = new AutomationRule { Name = NewName.Trim(), Trigger = trigger, Actions = new List<AutomationAction> { action } };
        var problems = AutomationAnalysis.Validate(rule);
        if (problems.Count > 0) { FormError = string.Join(" ", problems); return; }
        _store.SaveRule(rule);
        NewName = "";
        ProcessName = "";
        Reload();
    }

    [RelayCommand]
    private void Toggle(RuleRow? row)
    {
        if (row is null) return;
        _store.SaveRule(row.Rule with { Enabled = !row.Rule.Enabled });
        Reload();
    }

    [RelayCommand]
    private async Task DeleteAsync(RuleRow? row)
    {
        if (row is null) return;
        if (!await _dialogs.ConfirmAsync("Regel löschen", $"Die Regel „{row.Name}“ wird gelöscht. Bereits ausgeführte Änderungen bleiben bestehen.", "Löschen", danger: true).ConfigureAwait(true)) return;
        _store.DeleteRule(row.Rule.Id);
        Reload();
    }
}

// ───────────────────────── Verlauf / Sicherung ─────────────────────────

public sealed partial class TransactionRowViewModel : ObservableObject
{
    public TransactionRowViewModel(TransactionRecord record) => Record = record;
    public TransactionRecord Record { get; }
    public string Time => Record.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public string Name => Record.ActionName;
    public string Source => Record.Source.ToGerman() + (string.IsNullOrWhiteSpace(Record.SourceLabel) ? "" : " · " + Record.SourceLabel);
    public string Status => Record.Status.ToGerman();
    public string Change => $"{Record.OriginalDisplay}  →  {Record.IntendedDisplay}";
    public string Actual => string.IsNullOrWhiteSpace(Record.ActualDisplay) ? "" : "Tatsächlich: " + Record.ActualDisplay;
    public string Verification => Record.VerificationMessage;
    public string? Error => Record.ErrorDetails;
    public RiskLevel Risk => Record.Risk;
    public bool CanUndo => Record.IsActive && Record.CanRollback;
    public bool IsFailed => Record.Status is TransactionStatus.Failed or TransactionStatus.VerificationFailed or TransactionStatus.RollbackFailed or TransactionStatus.Interrupted;
    public string SearchText => $"{Name} {Source} {Status} {Change}".ToLowerInvariant();
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Optimierungsverlauf: jede Änderung mit Ausgangswert, Zielwert, Prüfergebnis und Rückgängig-Möglichkeit – auch nach einem Neustart der Anwendung.</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly ITransactionJournal _journal;
    private readonly ActionCoordinator _coordinator;
    private readonly DialogService _dialogs;
    private readonly ILogger<HistoryViewModel> _logger;
    private List<TransactionRowViewModel> _all = new();
    private bool _subscribed;

    public HistoryViewModel(ITransactionJournal journal, ActionCoordinator coordinator, DialogService dialogs, ILogger<HistoryViewModel> logger)
    {
        _journal = journal;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _logger = logger;
    }

    public override string Key => "history";
    public override string Title => "Optimierungsverlauf";
    public override string Subtitle => "Jede Änderung mit Ausgangswert, Zielwert und Prüfergebnis. Einträge bleiben nach einem Neustart erhalten und lassen sich dort zurücknehmen.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<TransactionRowViewModel> Rows { get; } = new();
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _onlyActive;
    [ObservableProperty] private bool _onlyProblems;
    [ObservableProperty] private string _summary = "";

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyActiveChanged(bool value) => ApplyFilter();
    partial void OnOnlyProblemsChanged(bool value) => ApplyFilter();

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = LoadAsync(); });
        }
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var records = await Task.Run(() => _journal.GetRecent(500)).ConfigureAwait(true);
        _all = records.Select(r => new TransactionRowViewModel(r)).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = Filter.Trim().ToLowerInvariant();
        Rows.Clear();
        foreach (var row in _all.Where(r => (query.Length == 0 || r.SearchText.Contains(query, StringComparison.Ordinal)) && (!OnlyActive || r.CanUndo) && (!OnlyProblems || r.IsFailed))) Rows.Add(row);
        Summary = $"{_all.Count} Einträge · {_all.Count(r => r.CanUndo)} aktiv und umkehrbar · {_all.Count(r => r.IsFailed)} mit Problem · {Rows.Count} angezeigt";
    }

    [RelayCommand]
    private Task UndoAsync(TransactionRowViewModel? row) => GuardAsync(async () =>
    {
        if (row is { CanUndo: true }) await _coordinator.UndoAsync(row.Record).ConfigureAwait(true);
    }, "Die Änderung konnte nicht rückgängig gemacht werden.");

    [RelayCommand]
    private Task UndoSelectedAsync() => GuardAsync(async () =>
    {
        var selected = Rows.Where(r => r.IsSelected && r.CanUndo).Select(r => r.Record).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Keine Auswahl", "Wählen Sie mindestens einen aktiven, umkehrbaren Eintrag aus.").ConfigureAwait(true);
            return;
        }
        await _coordinator.UndoSelectedAsync(selected).ConfigureAwait(true);
    }, "Die Änderungen konnten nicht wiederhergestellt werden.");

    [RelayCommand]
    private Task UndoBatchAsync(TransactionRowViewModel? row) => GuardAsync(async () =>
    {
        if (row is not null) await _coordinator.UndoBatchAsync(row.Record.BatchId, row.Record.SourceLabel.Length > 0 ? row.Record.SourceLabel : row.Name).ConfigureAwait(true);
    }, "Der Lauf konnte nicht rückgängig gemacht werden.");

    [RelayCommand]
    private Task UndoLastAsync() => GuardAsync(async () => { await _coordinator.UndoLastAsync().ConfigureAwait(true); }, "Die letzte Änderung konnte nicht rückgängig gemacht werden.");

    [RelayCommand]
    private Task ExportAsync() => GuardAsync(async () =>
    {
        var path = _dialogs.SaveFile("Textbericht (*.txt)|*.txt|JSON (*.json)|*.json|CSV (*.csv)|*.csv", $"nexus-verlauf-{DateTime.Now:yyyyMMdd-HHmm}.txt", "Verlauf exportieren");
        if (path is null) return;
        var format = Path.GetExtension(path).ToLowerInvariant() switch { ".json" => ReportFormat.Json, ".csv" => ReportFormat.Csv, _ => ReportFormat.Text };
        await File.WriteAllTextAsync(path, ReportExporter.ExportTransactions(Rows.Select(r => r.Record).ToList(), format), Encoding.UTF8).ConfigureAwait(true);
    }, "Der Verlauf konnte nicht exportiert werden.");
}

/// <summary>Sicherung und Wiederherstellung: gesicherte Ausgangszustände, Windows-Wiederherstellungspunkte, Datenverzeichnis.</summary>
public sealed partial class BackupViewModel : PageViewModel
{
    private readonly ITransactionJournal _journal;
    private readonly ActionCoordinator _coordinator;
    private readonly ProfileManager _profiles;
    private readonly NexusDatabase _database;
    private readonly DialogService _dialogs;
    private readonly ILogger<BackupViewModel> _logger;
    private bool _subscribed;

    public BackupViewModel(ITransactionJournal journal, ActionCoordinator coordinator, ProfileManager profiles, NexusDatabase database, DialogService dialogs, ILogger<BackupViewModel> logger)
    {
        _journal = journal;
        _coordinator = coordinator;
        _profiles = profiles;
        _database = database;
        _dialogs = dialogs;
        _logger = logger;
    }

    public override string Key => "backup";
    public override string Title => "Sicherung & Wiederherstellung";
    public override string Subtitle => "Vor jeder Änderung sichert die Anwendung den Ausgangszustand. Hier stellen Sie ihn wieder her – einzeln, gesammelt oder für ein ganzes Profil.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<TransactionRowViewModel> Restorable { get; } = new();
    public ObservableCollection<InfoRow> Storage { get; } = new();
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _activeProfile = "Kein Profil aktiv.";

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = LoadAsync(); });
        }
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        var records = await Task.Run(() => _journal.GetRecent(500).Where(r => r.IsActive && r.CanRollback).ToList()).ConfigureAwait(true);
        Restorable.Clear();
        foreach (var record in records) Restorable.Add(new TransactionRowViewModel(record));
        Summary = records.Count == 0 ? "Es gibt keine aktiven, wiederherstellbaren Änderungen." : $"{records.Count} Änderung(en) können auf den gesicherten Ausgangszustand zurückgesetzt werden.";
        ActiveProfile = _profiles.ActiveProfile is { } profile ? $"Aktives Profil: {profile.Name}" : "Kein Profil aktiv.";
        Storage.Clear();
        Storage.Add(new InfoRow("Datenverzeichnis", Path.GetDirectoryName(_database.FilePath) ?? ""));
        try { Storage.Add(new InfoRow("Größe der Datenbank", Core.Storage.ByteFormat.Format(new FileInfo(_database.FilePath).Length))); }
        catch (IOException) { Storage.Add(new InfoRow("Größe der Datenbank", "Nicht lesbar")); }
    }

    [RelayCommand]
    private Task RestoreSelectedAsync() => GuardAsync(async () =>
    {
        var selected = Restorable.Where(r => r.IsSelected).Select(r => r.Record).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Keine Auswahl", "Wählen Sie mindestens eine Änderung aus.").ConfigureAwait(true);
            return;
        }
        await _coordinator.UndoSelectedAsync(selected).ConfigureAwait(true);
    }, "Die Änderungen konnten nicht wiederhergestellt werden.");

    [RelayCommand]
    private Task RestoreAllAsync() => GuardAsync(async () =>
    {
        if (Restorable.Count == 0) return;
        await _coordinator.UndoSelectedAsync(Restorable.Select(r => r.Record).ToList()).ConfigureAwait(true);
    }, "Die Änderungen konnten nicht wiederhergestellt werden.");

    [RelayCommand]
    private Task UndoLastAsync() => GuardAsync(async () => { await _coordinator.UndoLastAsync().ConfigureAwait(true); }, "Die letzte Änderung konnte nicht rückgängig gemacht werden.");

    [RelayCommand]
    private Task ResetProfileAsync() => GuardAsync(async () =>
    {
        if (_profiles.ActiveProfile is not { } active)
        {
            await _dialogs.ShowMessageAsync("Kein Profil aktiv", "Es ist kein Profil aktiv, das zurückgesetzt werden könnte.").ConfigureAwait(true);
            return;
        }
        if (!await _dialogs.ConfirmAsync("Profil zurücksetzen", $"Alle Änderungen des Profils „{active.Name}“ werden zurückgenommen.", "Zurücksetzen").ConfigureAwait(true)) return;
        var outcomes = await _profiles.ResetActiveAsync().ConfigureAwait(true);
        await _dialogs.ShowMessageAsync("Profil zurückgesetzt", $"{outcomes.Count(o => o.Success)} von {outcomes.Count} Änderung(en) zurückgenommen.").ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }, "Das Profil konnte nicht zurückgesetzt werden.");

    [RelayCommand]
    private Task CreateRestorePointAsync()
        => _coordinator.RunAsync(new[] { new PlanRequest(RestorePointAction.ActionId, ActionParameters.Of(("description", "NEXUS Optimizer AI – manuell"))) }, ExecutionSource.User, "Wiederherstellungspunkt erstellen");

    [RelayCommand]
    private void OpenDataFolder() => PageHelpers.ShowInExplorer(Path.GetDirectoryName(_database.FilePath));

    [RelayCommand]
    private void OpenWindowsRecovery() => PageHelpers.OpenUri("ms-settings:recovery");
}

// ───────────────────────── KI-Chat ─────────────────────────

public sealed partial class ProposalViewModel : ObservableObject
{
    private readonly ChatProposal _proposal;
    private readonly Func<ProposalViewModel, Task> _run;

    public ProposalViewModel(ChatProposal proposal, Func<ProposalViewModel, Task> run)
    {
        _proposal = proposal;
        _run = run;
    }

    public ChatProposal Proposal => _proposal;
    public string Title => _proposal.Title;
    public string Description => _proposal.Description;
    public string ButtonText => _proposal.Kind switch { ProposalKind.Action => "Vorschau öffnen …", ProposalKind.RollbackLast => "Rückgängig machen …", _ => "Seite öffnen" };
    [ObservableProperty] private string? _state;
    [ObservableProperty] private bool _isUsed;

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsUsed) return;
        await _run(this).ConfigureAwait(true);
    }

    public void Complete(string state)
    {
        State = state;
        IsUsed = true;
    }
}

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(ChatRole role, ChatMessageKind kind, string text, string? provider)
    {
        Role = role;
        Kind = kind;
        _text = text;
        Provider = provider;
    }

    public ChatRole Role { get; }
    public ChatMessageKind Kind { get; }
    public string? Provider { get; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System;
    public string KindText => Kind switch
    {
        ChatMessageKind.Explanation => "Erklärung", ChatMessageKind.Recommendation => "Empfehlung", ChatMessageKind.PlannedAction => "Geplant – noch nicht ausgeführt",
        ChatMessageKind.ConfirmedAction => "Bestätigt", ChatMessageKind.ExecutedAction => "Ausgeführt", ChatMessageKind.VerifiedResult => "Ausgeführt und verifiziert", _ => "Fehler",
    };
    [ObservableProperty] private string _text;
    public ObservableCollection<ProposalViewModel> Proposals { get; } = new();
    public bool HasProposals => Proposals.Count > 0;
    public void OnPropertyChangedPublic(string name) => OnPropertyChanged(name);

    [ObservableProperty] private ChatMessageKind _currentKind;
    [ObservableProperty] private string? _currentProvider;
    public void SetMeta(ChatMessageKind kind, string provider)
    {
        CurrentKind = kind;
        CurrentProvider = provider;
        OnPropertyChanged(nameof(KindTextCurrent));
    }
    public string KindTextCurrent => (CurrentProvider is null ? "" : CurrentProvider + " · ") + KindOf(CurrentKind == default ? Kind : CurrentKind);
    private static string KindOf(ChatMessageKind kind) => kind switch
    {
        ChatMessageKind.Explanation => "Erklärung", ChatMessageKind.Recommendation => "Empfehlung", ChatMessageKind.PlannedAction => "Geplant – noch nicht ausgeführt",
        ChatMessageKind.ConfirmedAction => "Bestätigt", ChatMessageKind.ExecutedAction => "Ausgeführt", ChatMessageKind.VerifiedResult => "Ausgeführt und verifiziert", _ => "Fehler",
    };
}

/// <summary>KI-Assistent: beantwortet Fragen zum System auf Deutsch. Er schlägt nur geprüfte Aktionen vor – ausgeführt wird erst nach Vorschau und Ihrer Bestätigung.</summary>
public sealed partial class ChatViewModel : PageViewModel
{
    private readonly ChatEngine _chat;
    private readonly ActionCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly ILogger<ChatViewModel> _logger;
    private CancellationTokenSource? _cancellation;

    public ChatViewModel(ChatEngine chat, ActionCoordinator coordinator, SettingsService settings, INavigationService navigation, ILogger<ChatViewModel> logger)
    {
        _chat = chat;
        _coordinator = coordinator;
        _settings = settings;
        _navigation = navigation;
        _logger = logger;
    }

    public override string Key => "chat";
    public override string Title => "KI-Assistent";
    public override string Subtitle => "Fragen Sie nach Auslastung, Engpässen und Einstellungen. Der Assistent erklärt und empfiehlt – er führt nichts selbst aus.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public IReadOnlyList<string> Suggestions => ChatEngine.SuggestedQuestions;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _input = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isBusy;
    [ObservableProperty] private string _providerText = "";

    protected override Task OnNavigatedToAsync(object? parameter)
    {
        var settings = _settings.Current;
        ProviderText = settings.AiProvider switch
        {
            AiProviderKind.RuleBased => "Antworten kommen aus dem lokalen Regelwerk – ohne Sprachmodell und ohne Netzwerkzugriff.",
            AiProviderKind.Ollama => "Antworten kommen von einem lokalen Sprachmodell (Ollama).",
            _ => "Antworten kommen von einem Cloud-Sprachmodell. Der gesendete Systemkontext ist in den Einstellungen einsehbar.",
        };
        if (Messages.Count == 0)
        {
            foreach (var record in _chat.LoadHistory()) Messages.Add(new ChatMessageViewModel(record.Role, record.Kind, record.Text, record.Provider));
            if (Messages.Count == 0)
                Messages.Add(new ChatMessageViewModel(ChatRole.System, ChatMessageKind.Explanation,
                    "Hallo! Ich beantworte Fragen zu Ihrem System auf Deutsch. Ich kann Änderungen vorschlagen; ausgeführt wird nichts, bevor Sie die Vorschau bestätigt haben. Wähle ich eine Änderung aus, kennzeichne ich klar, ob sie nur erklärt, geplant oder wirklich ausgeführt und verifiziert wurde.", "Anwendung"));
        }
        return Task.CompletedTask;
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        Input = "";
        await AskAsync(text).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AskSuggestionAsync(string? question)
    {
        if (!IsBusy && !string.IsNullOrWhiteSpace(question)) await AskAsync(question).ConfigureAwait(true);
    }

    private async Task AskAsync(string text)
    {
        Messages.Add(new ChatMessageViewModel(ChatRole.User, ChatMessageKind.Explanation, text, null));
        var answer = new ChatMessageViewModel(ChatRole.Assistant, ChatMessageKind.Explanation, "", null);
        Messages.Add(answer);
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            await foreach (var item in _chat.SendAsync(text, _cancellation.Token).ConfigureAwait(true))
            {
                switch (item)
                {
                    case ChatTextChunk chunk: answer.Text += chunk.Text; break;
                    case ChatNotice notice: Messages.Insert(Messages.IndexOf(answer), new ChatMessageViewModel(ChatRole.System, notice.IsError ? ChatMessageKind.Error : ChatMessageKind.Explanation, notice.Text, "Anwendung")); break;
                    case ChatProposalItem proposal:
                        answer.Proposals.Add(new ProposalViewModel(proposal.Proposal, RunProposalAsync));
                        answer.OnPropertyChangedPublic(nameof(ChatMessageViewModel.HasProposals));
                        break;
                    case ChatCompleted done:
                        answer.SetMeta(done.Kind, done.ProviderLabel);
                        break;
                }
            }
            if (string.IsNullOrWhiteSpace(answer.Text)) answer.Text = answer.HasProposals ? "Ich habe folgende Schritte vorbereitet:" : "Dazu habe ich keine Antwort.";
        }
        catch (OperationCanceledException) { answer.Text += " (abgebrochen)"; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat fehlgeschlagen.");
            answer.Text = "Die Anfrage ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private async Task RunProposalAsync(ProposalViewModel vm)
    {
        var proposal = vm.Proposal;
        switch (proposal.Kind)
        {
            case ProposalKind.OpenPage when proposal.PageKey is { } key:
                _navigation.Navigate(key);
                vm.Complete("Seite geöffnet.");
                break;
            case ProposalKind.RollbackLast:
            {
                var ok = await _coordinator.UndoLastAsync().ConfigureAwait(true);
                var text = ok ? "Die letzte Änderung wurde zurückgenommen." : "Es wurde nichts zurückgenommen.";
                vm.Complete(text);
                _chat.RecordSystemMessage(text, ok ? ChatMessageKind.VerifiedResult : ChatMessageKind.Explanation);
                Messages.Add(new ChatMessageViewModel(ChatRole.System, ok ? ChatMessageKind.VerifiedResult : ChatMessageKind.Explanation, text, "Anwendung"));
                break;
            }
            case ProposalKind.Action:
            {
                var result = await _coordinator.RunAsync(proposal.Requests, ExecutionSource.Chat, proposal.Title, "KI-Chat").ConfigureAwait(true);
                // Das Ergebnis stammt ausschließlich aus der Ausführungs-Engine – nie aus Modelltext.
                string text;
                ChatMessageKind kind;
                if (result is null) { text = "Nichts ausgeführt – die Vorschau wurde abgebrochen oder es gab nichts zu ändern."; kind = ChatMessageKind.Explanation; }
                else if (result.AllSucceeded) { text = $"{result.SucceededCount} Änderung(en) ausgeführt und durch erneutes Lesen verifiziert."; kind = ChatMessageKind.VerifiedResult; }
                else { text = $"{result.SucceededCount} von {result.Steps.Count} Änderung(en) ausgeführt. " + string.Join(" ", result.Steps.Where(s => !s.Success).Select(s => s.Step.Preview.Title + ": " + s.Message)); kind = ChatMessageKind.Error; }
                vm.Complete(text);
                _chat.RecordSystemMessage(text, kind);
                Messages.Add(new ChatMessageViewModel(ChatRole.System, kind, text, "Anwendung"));
                break;
            }
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void ClearHistory()
    {
        _chat.ClearHistory();
        Messages.Clear();
        _ = OnNavigatedToAsync(null);
    }
}
