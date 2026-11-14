using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Windows.Hardware;

namespace NexusOptimizer.App.ViewModels;

public sealed record NavItem(string Key, string Title, string IconKey, string Group, Type PageType, string Keywords = "")
{
    public Geometry? Icon => Application.Current?.TryFindResource("Icon." + IconKey) as Geometry;
}

/// <summary>Alle Bereiche der Anwendung in Navigationsreihenfolge.</summary>
public static class PageRegistry
{
    private const string Overview = "Übersicht", Hardware = "Hardware", System = "System", Maintenance = "Wartung", Management = "Verwaltung";

    public static IReadOnlyList<NavItem> All { get; } = new NavItem[]
    {
        new("dashboard", "Dashboard", "Dashboard", Overview, typeof(DashboardViewModel), "übersicht start status zustand"),
        new("oneclick", "Ein-Klick-Optimierung", "Bolt", Overview, typeof(OneClickViewModel), "jetzt optimieren modus sicher ausgewogen maximale leistung"),
        new("ai", "KI-Optimierer", "Sparkle", Overview, typeof(AiOptimizerViewModel), "empfehlungen analyse automatik ziel lernmodus"),
        new("monitor", "Systemmonitor", "Pulse", Overview, typeof(MonitorViewModel), "live messwerte diagramm historie warnung temperatur auslastung"),
        new("hardware", "Hardware-Übersicht", "Layers", Hardware, typeof(HardwareOverviewViewModel), "alle hardware übersicht gesamt komponenten system mainboard bios modelle seriennummer"),
        new("cpu", "Prozessor", "Cpu", Hardware, typeof(CpuViewModel), "cpu kerne takt turbo core parking leistungszustand"),
        new("gpu", "Grafik", "Gpu", Hardware, typeof(GpuViewModel), "gpu grafikkarte vram treiber grafikspeicher"),
        new("ram", "Arbeitsspeicher", "Ram", Hardware, typeof(RamViewModel), "ram speicher auslagerungsdatei pagefile module"),
        new("storage", "Datenträger", "Drive", Hardware, typeof(StorageViewModel), "ssd festplatte laufwerk trim defragmentieren duplikate große dateien smart"),
        new("network", "Netzwerk", "Globe", Hardware, typeof(NetworkViewModel), "dns ping latenz adapter winsock traceroute"),
        new("power", "Energieverwaltung", "Battery", Hardware, typeof(PowerViewModel), "energieplan energiesparplan akku höchstleistung standby"),
        new("startup", "Autostart", "Play", System, typeof(StartupViewModel), "autostart programme anmeldung start"),
        new("processes", "Prozesse", "List", System, typeof(ProcessesViewModel), "task manager beenden priorität pid"),
        new("services", "Windows-Dienste", "Gear", System, typeof(ServicesViewModel), "dienste starttyp service"),
        new("registry", "Registry-Tweaks", "Tree", System, typeof(RegistryViewModel), "registrierung tweaks einstellungen explorer spielmodus"),
        new("privacy", "Datenschutz", "Shield", System, typeof(PrivacyViewModel), "datenschutz werbe-id telemetrie sicherheit defender"),
        new("advanced", "Expertenlabor", "Flask", System, typeof(AdvancedViewModel), "experimentell experten mythen"),
        new("gaming", "Gaming-Boost", "Gamepad", Maintenance, typeof(GamingViewModel), "spiele steam epic profil fps spielmodus"),
        new("cleanup", "Windows-Bereinigung", "Trash", Maintenance, typeof(CleanupViewModel), "temporäre dateien cache papierkorb aufräumen speicherplatz"),
        new("diagnostics", "Systemdiagnose", "Diagnose", Maintenance, typeof(DiagnosticsViewModel), "fehler ereignisprotokoll abstürze geräte befunde"),
        new("repair", "Windows-Reparatur", "Wrench", Maintenance, typeof(RepairViewModel), "sfc dism chkdsk reparieren wiederherstellungspunkt"),
        new("benchmark", "Benchmark-Center", "Stopwatch", Maintenance, typeof(BenchmarkViewModel), "benchmark messen vergleich leistungstest"),
        new("profiles", "Optimierungsprofile", "Layers", Management, typeof(ProfilesViewModel), "profil spiel büro akku leise streaming"),
        new("automation", "Automatisierungscenter", "Cycle", Management, typeof(AutomationViewModel), "regeln automatik wenn dann zeitplan"),
        new("backup", "Sicherung & Wiederherstellung", "Archive", Management, typeof(BackupViewModel), "backup wiederherstellen rollback wiederherstellungspunkt"),
        new("history", "Optimierungsverlauf", "History", Management, typeof(HistoryViewModel), "verlauf protokoll rückgängig änderungen journal"),
        new("chat", "KI-Assistent", "Chat", Management, typeof(ChatViewModel), "chat fragen assistent hilfe erklärung"),
        new("settings", "Einstellungen", "Gear", Management, typeof(SettingsViewModel), "einstellungen design datenschutz api schlüssel ki ollama claude"),
    };

    public static NavItem? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}

public sealed class NavigationService : INavigationService
{
    public event Action<string, object?>? Requested;
    public void Navigate(string pageKey, object? parameter = null) => Ui.Post(() => Requested?.Invoke(pageKey, parameter));
}

public sealed record SearchResult(string Title, string Subtitle, string PageKey, object? Parameter, Geometry? Icon);

public sealed record NotificationViewModel(NotificationItem Item)
{
    public string Title => Item.Title;
    public string Message => Item.Message;
    public NotificationSeverity Severity => Item.Severity;
    public bool IsUnread => !Item.IsRead;
    public string Time => Item.Timestamp.ToLocalTime().ToString("dd.MM. HH:mm");
    public bool HasPage => Item.PageKey is not null;
}

/// <summary>Hauptfenster: Navigation, globale Suche, Benachrichtigungen, Dialog- und Hinweisebene.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly NotificationCenter _notifications;
    private readonly ToastService _toasts;
    private readonly ActionCatalog _catalog;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly Dictionary<string, PageViewModel> _pages = new();
    private bool _suppressSelection;

    public ShellViewModel(IServiceProvider services, NavigationService navigation, UiState ui, NotificationCenter notifications, ToastService toasts, ActionCatalog catalog,
        ThemeService theme, ILogger<ShellViewModel> logger)
    {
        _services = services;
        _notifications = notifications;
        _toasts = toasts;
        _catalog = catalog;
        _logger = logger;
        State = ui;
        Theme = theme;

        NavItems = new ObservableCollection<NavItem>(PageRegistry.All);
        NavView = CollectionViewSource.GetDefaultView(NavItems);
        NavView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));

        navigation.Requested += (key, parameter) => _ = NavigateAsync(key, parameter);
        _notifications.Changed += (_, _) => Ui.Post(RefreshNotifications);
        _notifications.Published += (_, _) => Ui.Post(RefreshNotifications);
        theme.Changed += (_, _) => OnPropertyChanged(nameof(FontScale));
        RefreshNotifications();
    }

    public UiState State { get; }
    public ThemeService Theme { get; }
    public double FontScale => Theme.FontScale;
    public ObservableCollection<NavItem> NavItems { get; }
    public ICollectionView NavView { get; }
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<NotificationViewModel> Notifications { get; } = new();
    public bool IsElevated => ProcessElevation.IsElevated;
    public string PrivilegeText => IsElevated ? "Läuft mit Administratorrechten" : "Läuft mit Standardrechten";
    public string PrivilegeHint => IsElevated
        ? "Die Anwendung wurde als Administrator gestartet. Empfohlen ist der Start mit Standardrechten – Administratorrechte werden bei Bedarf einzeln angefragt."
        : "Administratorrechte werden nur für einzelne Änderungen über die Windows-Benutzerkontensteuerung angefragt – nie im Hintergrund.";
    public string VersionText => "Version " + (typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private bool _isNotificationsOpen;
    [ObservableProperty] private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;
    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (_suppressSelection || value is null) return;
        _ = NavigateAsync(value.Key, null);
    }

    public async Task NavigateAsync(string key, object? parameter = null)
    {
        var item = PageRegistry.Find(key);
        if (item is null)
        {
            _logger.LogWarning("Unbekannte Seite angefordert: {Key}", key);
            return;
        }

        PageViewModel page;
        try
        {
            if (!_pages.TryGetValue(key, out page!))
            {
                page = (PageViewModel)_services.GetRequiredService(item.PageType);
                _pages[key] = page;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seite {Key} konnte nicht erstellt werden.", key);
            _toasts.Show("Seite nicht verfügbar", ex.Message, NotificationSeverity.Error);
            return;
        }

        if (!ReferenceEquals(CurrentPage, page))
        {
            CurrentPage?.Deactivate();
            CurrentPage = page;
        }

        _suppressSelection = true;
        SelectedNav = item;
        _suppressSelection = false;
        IsSearchOpen = false;
        await page.ActivateAsync(parameter).ConfigureAwait(true);
    }

    // ── Globale Suche ──
    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        var query = value.Trim();
        if (query.Length < 2)
        {
            IsSearchOpen = false;
            return;
        }

        foreach (var page in PageRegistry.All.Where(p => Matches(p.Title, query) || Matches(p.Keywords, query)).Take(6))
            SearchResults.Add(new SearchResult(page.Title, "Bereich · " + page.Group, page.Key, null, page.Icon));

        foreach (var action in _catalog.All.Where(a => !a.Metadata.IsDiagnosticOnly && a.Metadata.Parameters.Count == 0 && (Matches(a.Metadata.Name, query) || Matches(a.Metadata.Description, query))).Take(8))
        {
            var target = TweakPageFor(action.Metadata);
            if (PageRegistry.Find(target) is { } page)
                SearchResults.Add(new SearchResult(action.Metadata.Name, "Einstellung · " + page.Title, page.Key, action.Metadata.Name, page.Icon));
        }

        IsSearchOpen = SearchResults.Count > 0;
    }

    private static bool Matches(string text, string query) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    internal static string TweakPageFor(Core.Models.ActionMetadata metadata)
        => metadata.IsExperimental ? "advanced" : metadata.Category == Core.Models.ActionCategory.Privacy ? "privacy" : "registry";

    [RelayCommand]
    private void OpenSearchResult(SearchResult? result)
    {
        if (result is null) return;
        SearchText = "";
        _ = NavigateAsync(result.PageKey, result.Parameter);
    }

    // ── Benachrichtigungen ──
    private void RefreshNotifications()
    {
        Notifications.Clear();
        foreach (var item in _notifications.GetRecent(40)) Notifications.Add(new NotificationViewModel(item));
        UnreadCount = _notifications.UnreadCount;
    }

    [RelayCommand]
    private void ToggleNotifications()
    {
        IsNotificationsOpen = !IsNotificationsOpen;
        if (!IsNotificationsOpen && UnreadCount > 0) _notifications.MarkAllRead();
    }

    [RelayCommand]
    private void MarkNotificationsRead() => _notifications.MarkAllRead();

    [RelayCommand]
    private void ClearNotifications() => _notifications.Clear();

    [RelayCommand]
    private void OpenNotification(NotificationViewModel? notification)
    {
        if (notification?.Item.PageKey is not { } key) return;
        IsNotificationsOpen = false;
        _notifications.MarkAllRead();
        _ = NavigateAsync(key);
    }

    [RelayCommand]
    private void DismissToast(ToastViewModel? toast)
    {
        if (toast is not null) _toasts.Dismiss(toast);
    }

    [RelayCommand]
    private void OpenToast(ToastViewModel? toast)
    {
        if (toast is null) return;
        _toasts.Dismiss(toast);
        if (toast.PageKey is { } key) _ = NavigateAsync(key);
    }

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (!string.IsNullOrEmpty(key)) _ = NavigateAsync(key);
    }

    public void DeactivateCurrent() => CurrentPage?.Deactivate();
    public void ReactivateCurrent()
    {
        if (CurrentPage is { IsActive: false } page) _ = page.ActivateAsync(null);
    }
}
