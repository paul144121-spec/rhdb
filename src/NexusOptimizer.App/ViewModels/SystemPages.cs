using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Diagnostics;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Windows.SystemAccess;
using ServiceStartMode = NexusOptimizer.Core.Services.ServiceStartMode;

namespace NexusOptimizer.App.ViewModels;

// ───────────────────────── Autostart ─────────────────────────

public sealed partial class StartupItemViewModel : ObservableObject
{
    private readonly ActionCoordinator _coordinator;

    public StartupItemViewModel(StartupEntry entry, ActionCoordinator coordinator)
    {
        Entry = entry;
        _coordinator = coordinator;
    }

    public StartupEntry Entry { get; }
    public string Name => Entry.DisplayName;
    public string Publisher => string.IsNullOrWhiteSpace(Entry.Publisher) ? "Herausgeber unbekannt" : Entry.Publisher!;
    public string Command => Entry.Command;
    public string SourceText => StartupApproval.ToGerman(Entry.Source);
    public bool IsEnabled => Entry.Enabled;
    public bool RequiresAdmin => Entry.RequiresAdministrator;
    public bool IsMissing => !Entry.FileExists && Entry.FilePath is not null;
    public string StateText => Entry.Enabled ? "Aktiviert" : "Deaktiviert";
    public string ToggleText => Entry.Enabled ? "Deaktivieren" : "Aktivieren";
    public string SizeText => Entry.FileSizeBytes is { } size ? ByteFormat.Format(size) : "";
    public string SearchText => $"{Name} {Publisher} {Command}".ToLowerInvariant();

    [ObservableProperty] private string _signatureText = "Signatur wird geprüft …";
    [ObservableProperty] private bool _signatureWarning;

    public void CheckSignature()
    {
        if (Entry.FilePath is not { } path || !Entry.FileExists)
        {
            SignatureText = Entry.FilePath is null ? "Programmdatei nicht ermittelbar" : "Programmdatei fehlt";
            SignatureWarning = Entry.FilePath is not null;
            return;
        }
        try
        {
            var status = AuthenticodeVerifier.Verify(path);
            SignatureText = AuthenticodeVerifier.ToGerman(status);
            SignatureWarning = status is SignatureStatus.Invalid;
        }
        catch (Exception)
        {
            SignatureText = "Signatur nicht prüfbar";
        }
    }

    [RelayCommand]
    private Task ToggleAsync()
        => _coordinator.RunAsync(new[] { new PlanRequest(StartupEntryAction.ActionId, ActionParameters.Of(("source", Entry.Source.ToString()), ("name", Entry.Name), ("enabled", (!Entry.Enabled).ToString()))) },
            ExecutionSource.User, (Entry.Enabled ? "Autostart deaktivieren: " : "Autostart aktivieren: ") + Name);

    [RelayCommand]
    private void ShowFile() => PageHelpers.ShowInExplorer(Entry.FilePath);
}

/// <summary>Autostart: Einträge aus Registrierung und Autostart-Ordnern, umschaltbar über denselben Mechanismus wie der Task-Manager.</summary>
public sealed partial class StartupViewModel : PageViewModel
{
    private readonly IStartupInventory _inventory;
    private readonly ActionCoordinator _coordinator;
    private readonly ILogger<StartupViewModel> _logger;
    private List<StartupItemViewModel> _all = new();
    private bool _subscribed;

    public StartupViewModel(IStartupInventory inventory, ActionCoordinator coordinator, ILogger<StartupViewModel> logger)
    {
        _inventory = inventory;
        _coordinator = coordinator;
        _logger = logger;
    }

    public override string Key => "startup";
    public override string Title => "Autostart";
    public override string Subtitle => "Programme, die mit der Anmeldung starten. Deaktivieren entfernt nichts – der Eintrag bleibt erhalten und lässt sich jederzeit wieder aktivieren.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<StartupItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;

    partial void OnFilterChanged(string value) => ApplyFilter();

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = LoadAsync(); });
        }
        if (parameter is string text) Filter = text;
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() => _inventory.GetEntries()).ConfigureAwait(true);
            _all = entries.OrderByDescending(e => e.Enabled).ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(e => new StartupItemViewModel(e, _coordinator)).ToList();
            ApplyFilter();
            var enabled = entries.Count(e => e.Enabled);
            Summary = entries.Count == 0 ? "Es wurden keine Autostarteinträge gefunden." : $"{entries.Count} Einträge · {enabled} aktiviert · {entries.Count - enabled} deaktiviert";
            var items = _all.ToList();
            _ = Task.Run(() => { foreach (var item in items) item.CheckSignature(); });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autostarteinträge konnten nicht gelesen werden.");
            ErrorMessage = "Die Autostarteinträge konnten nicht gelesen werden: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var query = Filter.Trim().ToLowerInvariant();
        Items.Clear();
        foreach (var item in _all.Where(i => query.Length == 0 || i.SearchText.Contains(query, StringComparison.Ordinal))) Items.Add(item);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void OpenWindowsStartupSettings() => PageHelpers.OpenUri("ms-settings:startupapps");
}

// ───────────────────────── Prozesse ─────────────────────────

public sealed partial class ProcessRowViewModel : ObservableObject
{
    public ProcessRowViewModel(ProcessSnapshot snapshot)
    {
        ProcessId = snapshot.ProcessId;
        _snapshot = snapshot;
    }

    public int ProcessId { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Cpu), nameof(CpuText), nameof(Memory), nameof(MemoryText), nameof(PrivateText), nameof(Threads), nameof(DiskText), nameof(PriorityText), nameof(Publisher), nameof(Path), nameof(IsProtected), nameof(ServicesText), nameof(Detail))]
    private ProcessSnapshot _snapshot;

    public string Name => Snapshot.Name;
    public double Cpu => Snapshot.CpuPercent;
    public string CpuText => Snapshot.IsAccessDenied ? "–" : Snapshot.CpuPercent.ToString("0.0") + " %";
    public long Memory => Snapshot.WorkingSetBytes;
    public string MemoryText => ByteFormat.Format(Snapshot.WorkingSetBytes);
    public string PrivateText => ByteFormat.Format(Snapshot.PrivateBytes);
    public int Threads => Snapshot.ThreadCount;
    public string DiskText => Snapshot.DiskBytesPerSecond is { } rate ? ByteFormat.Rate(rate) : "–";
    public string PriorityText => ProcessPriorityAction.ToGerman(Snapshot.Priority);
    public string Publisher => Snapshot.Publisher ?? Snapshot.Description ?? "";
    public string? Path => Snapshot.ExecutablePath;
    public bool IsProtected => Snapshot.IsProtected;
    public string ServicesText => Snapshot.Services.Count > 0 ? string.Join(", ", Snapshot.Services) : "";
    public string Detail => (Snapshot.Description ?? Snapshot.Name) + (Snapshot.Services.Count > 0 ? " · Dienste: " + ServicesText : "");
}

/// <summary>Prozesse: Live-Liste mit Auslastung, Prioritätsänderung und Beenden – Windows- und Sicherheitsprozesse sind geschützt.</summary>
public sealed partial class ProcessesViewModel : PageViewModel
{
    private readonly IProcessMonitor _processes;
    private readonly ActionCoordinator _coordinator;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ILogger<ProcessesViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private bool _refreshing;

    public ProcessesViewModel(IProcessMonitor processes, ActionCoordinator coordinator, DialogService dialogs, ToastService toasts, ILogger<ProcessesViewModel> logger)
    {
        _processes = processes;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _toasts = toasts;
        _logger = logger;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _ = RefreshAsync();

        SortModes = new[] { new Choice<string>("cpu", "Nach CPU-Last"), new Choice<string>("memory", "Nach Arbeitsspeicher"), new Choice<string>("name", "Nach Name") };
        _selectedSort = SortModes[0];
        Priorities = Enum.GetValues<ProcessPriorityLevel>().Select(p => new Choice<ProcessPriorityLevel>(p, ProcessPriorityAction.ToGerman(p))).ToList();
        _selectedPriority = Priorities.First(p => p.Value == ProcessPriorityLevel.Normal);
    }

    public override string Key => "processes";
    public override string Title => "Prozesse";
    public override string Subtitle => "Laufende Prozesse mit gemessener Auslastung. Prozesse von Windows und Sicherheitssoftware lassen sich hier weder beenden noch umpriorisieren.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = new();
    public IReadOnlyList<Choice<string>> SortModes { get; }
    public IReadOnlyList<Choice<ProcessPriorityLevel>> Priorities { get; }

    [ObservableProperty] private Choice<string> _selectedSort;
    [ObservableProperty] private Choice<ProcessPriorityLevel> _selectedPriority;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanChangeSelected))]
    private ProcessRowViewModel? _selected;

    public bool HasSelection => Selected is not null;
    public bool CanChangeSelected => Selected is { IsProtected: false };

    partial void OnSelectedSortChanged(Choice<string> value) => _ = RefreshAsync();
    partial void OnFilterChanged(string value) => _ = RefreshAsync();
    partial void OnIsPausedChanged(bool value)
    {
        if (value) _timer.Stop();
        else if (IsActive) _timer.Start();
    }

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is string text) Filter = text;
        await RefreshAsync().ConfigureAwait(true);
        if (!IsPaused) _timer.Start();
    }

    protected override void OnNavigatedFrom() => _timer.Stop();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing || !IsActive) return;
        _refreshing = true;
        try
        {
            var query = Filter.Trim();
            var sort = SelectedSort.Value;
            var (snapshots, total) = await Task.Run(() =>
            {
                var all = _processes.GetProcesses();
                IEnumerable<ProcessSnapshot> filtered = query.Length == 0
                    ? all
                    : all.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (p.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || p.ProcessId.ToString() == query);
                filtered = sort switch
                {
                    "memory" => filtered.OrderByDescending(p => p.WorkingSetBytes),
                    "name" => filtered.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.ProcessId),
                    _ => filtered.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSetBytes),
                };
                return (filtered.ToList(), all.Count);
            }).ConfigureAwait(true);

            // Zeilen bleiben erhalten (Auswahl und Bildlaufposition), nur Werte und Reihenfolge werden angepasst.
            var existing = Rows.ToDictionary(r => r.ProcessId);
            for (var i = 0; i < snapshots.Count; i++)
            {
                var snapshot = snapshots[i];
                if (i < Rows.Count && Rows[i].ProcessId == snapshot.ProcessId)
                {
                    Rows[i].Snapshot = snapshot;
                }
                else if (existing.TryGetValue(snapshot.ProcessId, out var row) && Rows.IndexOf(row) is var index and >= 0)
                {
                    row.Snapshot = snapshot;
                    if (index != i) Rows.Move(index, Math.Min(i, Rows.Count - 1));
                }
                else
                {
                    Rows.Insert(Math.Min(i, Rows.Count), new ProcessRowViewModel(snapshot));
                }
            }
            var keep = snapshots.Select(s => s.ProcessId).ToHashSet();
            for (var i = Rows.Count - 1; i >= 0; i--)
                if (!keep.Contains(Rows[i].ProcessId)) Rows.RemoveAt(i);

            Summary = $"{total} Prozesse" + (snapshots.Count != total ? $" · {snapshots.Count} angezeigt" : "") + (IsPaused ? " · Aktualisierung angehalten" : " · Aktualisierung alle 2 Sekunden");
            OnPropertyChanged(nameof(CanChangeSelected));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prozessliste konnte nicht gelesen werden.");
            ErrorMessage = "Die Prozessliste konnte nicht gelesen werden: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task SetPriorityAsync()
    {
        if (Selected is not { IsProtected: false } row) return;
        await _coordinator.RunAsync(new[] { new PlanRequest(ProcessPriorityAction.ActionId, ActionParameters.Of(("pid", row.ProcessId.ToString()), ("priority", SelectedPriority.Value.ToString()))) },
            ExecutionSource.User, $"Priorität von {row.Name} ändern").ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task TerminateAsync()
    {
        if (Selected is not { IsProtected: false } row) return;
        var confirmed = await _dialogs.ConfirmAsync("Prozess beenden",
            $"„{row.Name}“ (PID {row.ProcessId}) wird sofort beendet. Nicht gespeicherte Daten dieses Programms gehen verloren. Dieser Schritt lässt sich nicht rückgängig machen.",
            "Prozess beenden", danger: true, details: row.Detail).ConfigureAwait(true);
        if (!confirmed) return;

        var pid = row.ProcessId;
        var result = await Task.Run(() => _processes.Terminate(pid)).ConfigureAwait(true);
        _logger.LogInformation("Prozess beenden (PID {Pid}, {Name}): {Result}", pid, row.Name, result.Message);
        _toasts.Show(result.Success ? "Prozess beendet" : "Prozess nicht beendet", result.Message, result.Success ? NotificationSeverity.Success : NotificationSeverity.Warning);
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowFile() => PageHelpers.ShowInExplorer(Selected?.Path);
}

// ───────────────────────── Dienste ─────────────────────────

public sealed class ServiceRowViewModel
{
    public ServiceRowViewModel(ServiceInfo info)
    {
        Info = info;
        IsProtected = ServiceProtection.IsProtected(info.Name, out var reason);
        ProtectionReason = reason;
        Advice = ServiceAdviceCatalog.Find(info.Name);
    }

    public ServiceInfo Info { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Info.DisplayName) ? Info.Name : Info.DisplayName;
    public string Name => Info.Name;
    public string State => Info.IsRunning ? "Wird ausgeführt" : string.IsNullOrWhiteSpace(Info.State) ? "Beendet" : Info.State;
    public bool IsRunning => Info.IsRunning;
    public string StartModeText => Info.StartMode.ToGerman();
    public bool IsProtected { get; }
    public string ProtectionReason { get; }
    public ServiceAdvice? Advice { get; }
    public bool HasAdvice => Advice is not null;
    public string Marker => IsProtected ? "Geschützt" : HasAdvice ? "Einschätzung" : "";
    public string Description => string.IsNullOrWhiteSpace(Info.Description) ? "Windows liefert zu diesem Dienst keine Beschreibung." : Info.Description;
    public string DependsOnText => Info.DependsOn.Count > 0 ? string.Join(", ", Info.DependsOn) : "Keine";
    public string DependentsText => Info.Dependents.Count > 0 ? string.Join(", ", Info.Dependents) : "Keine";
    public string AccountText => Info.Account ?? "Nicht verfügbar";
    public string PathText => Info.BinaryPath ?? "Nicht verfügbar";
    public string SearchText => $"{DisplayName} {Name} {Info.Description}".ToLowerInvariant();
}

/// <summary>Windows-Dienste: Starttyp ändern mit Sicherung; geschützte Dienste (Sicherheit, Updates, Kerninfrastruktur) sind gesperrt.</summary>
public sealed partial class ServicesViewModel : PageViewModel
{
    private readonly IServiceManager _services;
    private readonly ActionCoordinator _coordinator;
    private readonly ILogger<ServicesViewModel> _logger;
    private List<ServiceRowViewModel> _all = new();
    private bool _subscribed;

    public ServicesViewModel(IServiceManager services, ActionCoordinator coordinator, ILogger<ServicesViewModel> logger)
    {
        _services = services;
        _coordinator = coordinator;
        _logger = logger;
        Modes = new[] { ServiceStartMode.Automatic, ServiceStartMode.AutomaticDelayed, ServiceStartMode.Manual, ServiceStartMode.Disabled }.Select(m => new Choice<ServiceStartMode>(m, m.ToGerman())).ToList();
        _selectedMode = Modes[2];
    }

    public override string Key => "services";
    public override string Title => "Windows-Dienste";
    public override string Subtitle => "Starttyp von Diensten ändern – mit Sicherung des bisherigen Zustands. Pauschales Deaktivieren von Diensten bringt selten messbare Vorteile und wird hier nicht empfohlen.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<ServiceRowViewModel> Rows { get; } = new();
    public IReadOnlyList<Choice<ServiceStartMode>> Modes { get; }
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _onlyWithAdvice;
    [ObservableProperty] private bool _onlyRunning;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private Choice<ServiceStartMode> _selectedMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanChangeSelected))]
    private ServiceRowViewModel? _selected;

    public bool HasSelection => Selected is not null;
    public bool CanChangeSelected => Selected is { IsProtected: false, Info.IsDriver: false };

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyWithAdviceChanged(bool value) => ApplyFilter();
    partial void OnOnlyRunningChanged(bool value) => ApplyFilter();
    partial void OnSelectedChanged(ServiceRowViewModel? value)
    {
        if (value is null) return;
        var target = value.Advice?.SuggestedMode ?? value.Info.StartMode;
        SelectedMode = Modes.FirstOrDefault(m => m.Value == target) ?? Modes[2];
    }

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = LoadAsync(); });
        }
        if (parameter is string text) Filter = text;
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var selectedName = Selected?.Name;
            var services = await Task.Run(() => _services.GetServices()).ConfigureAwait(true);
            _all = services.Where(s => !s.IsDriver).OrderBy(s => string.IsNullOrWhiteSpace(s.DisplayName) ? s.Name : s.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(s => new ServiceRowViewModel(s)).ToList();
            ApplyFilter();
            Selected = Rows.FirstOrDefault(r => r.Name == selectedName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dienste konnten nicht gelesen werden.");
            ErrorMessage = "Die Dienste konnten nicht gelesen werden: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var query = Filter.Trim().ToLowerInvariant();
        var selectedName = Selected?.Name;
        Rows.Clear();
        foreach (var row in _all.Where(r => (query.Length == 0 || r.SearchText.Contains(query, StringComparison.Ordinal)) && (!OnlyWithAdvice || r.HasAdvice) && (!OnlyRunning || r.IsRunning)))
            Rows.Add(row);
        Selected = Rows.FirstOrDefault(r => r.Name == selectedName);
        Summary = $"{_all.Count} Dienste · {_all.Count(r => r.IsRunning)} laufen · {_all.Count(r => r.IsProtected)} geschützt · {Rows.Count} angezeigt";
    }

    [RelayCommand]
    private async Task ApplyModeAsync()
    {
        if (Selected is not { IsProtected: false } row) return;
        await _coordinator.RunAsync(new[] { new PlanRequest(ServiceStartupAction.ActionId, ActionParameters.Of(("service", row.Name), ("mode", SelectedMode.Value.ToString()))) },
            ExecutionSource.User, $"Starttyp von „{row.DisplayName}“ ändern").ConfigureAwait(true);
    }
}

// ───────────────────────── Registry-Tweaks, Datenschutz, Expertenlabor ─────────────────────────

/// <summary>Gemeinsame Logik für Seiten, die Tweak-Karten mit Filter und Sammelanwendung zeigen.</summary>
public abstract partial class TweakListPageViewModel : PageViewModel
{
    private readonly List<TweakItemViewModel> _all = new();
    private bool _subscribed;

    protected TweakListPageViewModel(ActionCatalog catalog, ActionCoordinator coordinator, ITransactionJournal journal, SystemSnapshotService snapshots)
    {
        Catalog = catalog;
        Coordinator = coordinator;
        Journal = journal;
        Snapshots = snapshots;
    }

    protected ActionCatalog Catalog { get; }
    protected ActionCoordinator Coordinator { get; }
    protected ITransactionJournal Journal { get; }
    protected SystemSnapshotService Snapshots { get; }
    protected IReadOnlyList<TweakItemViewModel> AllTweaks => _all;

    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = new();
    public ObservableCollection<Choice<ActionCategory?>> Categories { get; } = new();
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private Choice<ActionCategory?>? _selectedCategory;
    [ObservableProperty] private bool _onlyOpen;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty;

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(Choice<ActionCategory?>? value) => ApplyFilter();
    partial void OnOnlyOpenChanged(bool value) => ApplyFilter();

    protected void SetTweaks(IEnumerable<IOptimizationAction> actions)
    {
        _all.Clear();
        _all.AddRange(actions.OrderBy(a => a.Metadata.Category.ToGerman()).ThenBy(a => a.Metadata.Risk).ThenBy(a => a.Metadata.Name).Select(a => new TweakItemViewModel(a, Coordinator, Journal)));
        Categories.Clear();
        Categories.Add(new Choice<ActionCategory?>(null, "Alle Kategorien"));
        foreach (var category in _all.Select(t => t.Category).Distinct().OrderBy(c => c.ToGerman())) Categories.Add(new Choice<ActionCategory?>(category, category.ToGerman()));
        SelectedCategory = Categories[0];
    }

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            Coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = RefreshStatesAsync(); });
        }
        if (parameter is string text) Filter = text;
        await RefreshStatesAsync().ConfigureAwait(true);
    }

    protected virtual async Task RefreshStatesAsync()
    {
        IsLoading = true;
        try
        {
            var context = await Snapshots.GetContextAsync().ConfigureAwait(true);
            await PageHelpers.RefreshTweaksAsync(_all, context).ConfigureAwait(true);
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected virtual bool IsVisible(TweakItemViewModel tweak) => true;

    protected void ApplyFilter()
    {
        var query = Filter.Trim().ToLowerInvariant();
        var category = SelectedCategory?.Value;
        Tweaks.Clear();
        foreach (var tweak in _all.Where(t => IsVisible(t) && (query.Length == 0 || t.SearchText.Contains(query, StringComparison.Ordinal)) && (category is null || t.Category == category) && (!OnlyOpen || t.CanApply)))
            Tweaks.Add(tweak);
        IsEmpty = Tweaks.Count == 0;
        var applied = _all.Count(t => t.IsApplied);
        Summary = $"{_all.Count} Einstellungen · {applied} im Zielzustand · {_all.Count(t => t.CanApply)} offen · {_all.Count(t => !t.IsSupported)} auf diesem System nicht verfügbar · {Tweaks.Count} angezeigt";
    }

    [RelayCommand]
    private void ClearFilter()
    {
        Filter = "";
        OnlyOpen = false;
        SelectedCategory = Categories.FirstOrDefault();
    }

    /// <summary>Öffnet die Vorschau für alle angezeigten, noch offenen Einstellungen. Ausgeführt wird erst nach Bestätigung.</summary>
    [RelayCommand]
    private async Task ApplyVisibleAsync()
    {
        var requests = Tweaks.Where(t => t.CanApply).Select(t => new PlanRequest(t.Id)).ToList();
        if (requests.Count == 0) return;
        await Coordinator.RunAsync(requests, ExecutionSource.User, BulkTitle).ConfigureAwait(true);
    }

    protected virtual string BulkTitle => "Angezeigte Einstellungen anwenden";
}

/// <summary>Registry-Tweaks: geprüfte, dokumentierte Windows-Einstellungen aus der festen Allowlist.</summary>
public sealed class RegistryViewModel : TweakListPageViewModel
{
    public RegistryViewModel(ActionCatalog catalog, ActionCoordinator coordinator, ITransactionJournal journal, SystemSnapshotService snapshots) : base(catalog, coordinator, journal, snapshots)
        => SetTweaks(catalog.All.Where(a => a.Metadata.Id.StartsWith("reg.", StringComparison.Ordinal) && !a.Metadata.IsExperimental));

    public override string Key => "registry";
    public override string Title => "Registry-Tweaks";
    public override string Subtitle => "Dokumentierte Windows-Einstellungen aus einer festen, geprüften Liste. Freie Registrierungsbearbeitung gibt es hier bewusst nicht – sicherheitsrelevante Schlüssel sind gesperrt.";
}

/// <summary>Datenschutz: Windows-Datenschutzeinstellungen plus schreibgeschützte Übersicht der Sicherheitsfunktionen.</summary>
public sealed partial class PrivacyViewModel : TweakListPageViewModel
{
    private readonly IHardwareInfoProvider _hardware;

    public PrivacyViewModel(ActionCatalog catalog, ActionCoordinator coordinator, ITransactionJournal journal, SystemSnapshotService snapshots, IHardwareInfoProvider hardware) : base(catalog, coordinator, journal, snapshots)
    {
        _hardware = hardware;
        SetTweaks(catalog.All.Where(a => a.Metadata.Parameters.Count == 0 && !a.Metadata.IsExperimental && (a.Metadata.Category == ActionCategory.Privacy || a.Metadata.Tags.Contains("privacy", StringComparer.OrdinalIgnoreCase))));
        Links = new[]
        {
            new InfoRow("Allgemeine Datenschutzeinstellungen", "ms-settings:privacy"),
            new InfoRow("Diagnose und Feedback", "ms-settings:privacy-feedback"),
            new InfoRow("Aktivitätsverlauf", "ms-settings:privacy-activityhistory"),
            new InfoRow("App-Berechtigungen: Standort", "ms-settings:privacy-location"),
            new InfoRow("App-Berechtigungen: Kamera", "ms-settings:privacy-webcam"),
            new InfoRow("App-Berechtigungen: Mikrofon", "ms-settings:privacy-microphone"),
            new InfoRow("Windows-Sicherheit", "windowsdefender:"),
        };
    }

    public override string Key => "privacy";
    public override string Title => "Datenschutz";
    public override string Subtitle => "Werbe-, Vorschlags- und Diagnoseeinstellungen von Windows. Diese Anwendung schaltet keine Sicherheitsfunktionen ab.";
    protected override string BulkTitle => "Datenschutzeinstellungen anwenden";

    public ObservableCollection<InfoRow> Security { get; } = new();
    public IReadOnlyList<InfoRow> Links { get; }

    protected override async Task RefreshStatesAsync()
    {
        await base.RefreshStatesAsync().ConfigureAwait(true);
        try
        {
            var info = await Task.Run(() => _hardware.GetSystemInfoAsync()).ConfigureAwait(true);
            Security.Clear();
            Security.Add(InfoRow.Of("Sicherer Start (Secure Boot)", info.SecureBootEnabled, "Aktiv", "Nicht aktiv"));
            Security.Add(InfoRow.Of("TPM", info.TpmPresent is null ? null : info.TpmPresent == true ? "Vorhanden" + (info.TpmVersion is { } v ? " (Version " + v + ")" : "") : "Nicht vorhanden"));
            foreach (var feature in info.SecurityFeatures)
                Security.Add(feature.Enabled is null ? new InfoRow(feature.Name, "Nicht verfügbar", feature.Detail) : new InfoRow(feature.Name, (feature.Enabled.Value ? "Aktiv" : "Nicht aktiv") + (string.IsNullOrWhiteSpace(feature.Detail) ? "" : " – " + feature.Detail)));
        }
        catch (Exception ex)
        {
            Security.Clear();
            Security.Add(new InfoRow("Sicherheitsstatus", "Nicht verfügbar", ex.Message));
        }
    }

    [RelayCommand]
    private void OpenLink(string? uri) => PageHelpers.OpenUri(uri);
}

public sealed record DiagnosticGroup(string Name, IReadOnlyList<AdvancedDiagnosticItem> Items);

/// <summary>Expertenlabor: experimentelle Einstellungen (nach Freischaltung), reine Diagnosewerte zu verbreiteten „Tuning-Mythen“ und geführte manuelle Abläufe.</summary>
public sealed partial class AdvancedViewModel : TweakListPageViewModel
{
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ISystemDiagnostics _diagnostics;
    private readonly ILogger<AdvancedViewModel> _logger;

    public AdvancedViewModel(ActionCatalog catalog, ActionCoordinator coordinator, ITransactionJournal journal, SystemSnapshotService snapshots, SettingsService settings, DialogService dialogs,
        ISystemDiagnostics diagnostics, ILogger<AdvancedViewModel> logger) : base(catalog, coordinator, journal, snapshots)
    {
        _settings = settings;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        _logger = logger;
        SetTweaks(catalog.All.Where(a => a.Metadata.Parameters.Count == 0 && a.Metadata.IsExperimental));
        Workflows = GuidedWorkflows.All.Select(w => new GuidedWorkflowViewModel(w)).ToList();
    }

    public override string Key => "advanced";
    public override string Title => "Expertenlabor";
    public override string Subtitle => "Experimentelle Einstellungen mit unbelegtem Nutzen oder möglichen Nebenwirkungen – nur nach Freischaltung, nur einzeln bestätigt, nie automatisch.";
    protected override ILogger Logger => _logger;
    protected override string BulkTitle => "Experimentelle Einstellungen anwenden";

    public ObservableCollection<DiagnosticGroup> DiagnosticGroups { get; } = new();
    public IReadOnlyList<GuidedWorkflowViewModel> Workflows { get; }
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private string _unlockText = "";

    protected override bool IsVisible(TweakItemViewModel tweak) => IsUnlocked;

    protected override async Task RefreshStatesAsync()
    {
        var settings = _settings.Current;
        IsUnlocked = settings.ExperimentalUnlocked;
        UnlockText = IsUnlocked
            ? "Freigeschaltet" + (settings.ExperimentalUnlockedAt is { } at ? $" seit {at.ToLocalTime():dd.MM.yyyy HH:mm}" : "") + ". Experimentelle Änderungen werden weiterhin einzeln bestätigt und nie unbeaufsichtigt ausgeführt."
            : "Gesperrt. Ohne Freischaltung verwirft der Planer jede experimentelle Änderung – unabhängig davon, von wo sie angefordert wird.";
        await base.RefreshStatesAsync().ConfigureAwait(true);

        if (DiagnosticGroups.Count > 0) return;
        try
        {
            var items = await _diagnostics.GetAdvancedDiagnosticsAsync().ConfigureAwait(true);
            foreach (var group in items.GroupBy(i => i.Group)) DiagnosticGroups.Add(new DiagnosticGroup(group.Key, group.ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erweiterte Diagnosewerte konnten nicht gelesen werden.");
        }
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync("Expertenlabor freischalten",
            "Experimentelle Einstellungen können Stabilität, Kompatibilität oder das Verhalten von Windows beeinträchtigen. Ihr Nutzen ist nicht belegt und hängt stark von Hardware und Treibern ab.\n\n" +
            "Nach der Freischaltung gilt weiterhin:\n• Jede Änderung wird einzeln angezeigt und muss einzeln ausgewählt werden.\n• Der Ausgangszustand wird gesichert und lässt sich wiederherstellen.\n• Automatik, Regeln und KI können experimentelle Änderungen nie ausführen.\n• Sicherheitsfunktionen von Windows bleiben gesperrt.",
            "Freischalten", danger: true, acknowledgement: "Ich habe die Risiken verstanden und übernehme die Verantwortung für experimentelle Änderungen.").ConfigureAwait(true);
        if (!confirmed) return;
        _settings.Update(s => s with { ExperimentalUnlocked = true, ExperimentalUnlockedAt = DateTimeOffset.Now });
        _logger.LogWarning("Expertenlabor wurde freigeschaltet.");
        await RefreshStatesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LockAsync()
    {
        _settings.Update(s => s with { ExperimentalUnlocked = false, ExperimentalUnlockedAt = null });
        await RefreshStatesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenWorkflowLink(GuidedWorkflowViewModel? workflow) => PageHelpers.OpenUri(workflow?.Workflow.SettingsUri);
}
