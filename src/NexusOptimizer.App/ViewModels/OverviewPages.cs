using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Storage;

namespace NexusOptimizer.App.ViewModels;

public sealed partial class DashboardTileOption : ObservableObject
{
    private readonly Action<DashboardTileOption> _changed;

    public DashboardTileOption(string key, MetricTileViewModel tile, bool isVisible, Action<DashboardTileOption> changed)
    {
        Key = key;
        Tile = tile;
        _isVisible = isVisible;
        _changed = changed;
    }

    public string Key { get; }
    public MetricTileViewModel Tile { get; }
    public string Title => Tile.Title;

    [ObservableProperty] private bool _isVisible;
    partial void OnIsVisibleChanged(bool value) => _changed(this);
}

/// <summary>Startseite: gemessener Systemzustand, wichtigste Empfehlungen und der Einstieg in die Ein-Klick-Optimierung.</summary>
public sealed partial class DashboardViewModel : LivePageViewModel
{
    private readonly SystemSnapshotService _snapshots;
    private readonly RuleBasedAdvisor _advisor;
    private readonly ActionCoordinator _coordinator;
    private readonly INavigationService _navigation;
    private readonly RollbackService _rollback;
    private readonly ProfileManager _profiles;
    private readonly SettingsService _settings;
    private readonly AppRuntime _runtime;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly MetricTileViewModel _cpu = new("Prozessor", "Brush.Accent");
    private readonly MetricTileViewModel _gpu = new("Grafikkarte", "Brush.Cyan");
    private readonly MetricTileViewModel _ram = new("Arbeitsspeicher", "Brush.Blue");
    private readonly MetricTileViewModel _disk = new("Datenträger", "Brush.Green");
    private readonly MetricTileViewModel _network = new("Netzwerk", "Brush.Cyan", double.NaN);
    private readonly MetricTileViewModel _temperature = new("Temperatur", "Brush.Amber", 110);
    private TransactionRecord? _lastChange;
    private DateTimeOffset _lastAnalysis = DateTimeOffset.MinValue;
    private bool _analysisRunning;

    public DashboardViewModel(MonitoringService monitoring, SystemSnapshotService snapshots, RuleBasedAdvisor advisor, ActionCoordinator coordinator, INavigationService navigation,
        RollbackService rollback, ProfileManager profiles, SettingsService settings, AppRuntime runtime, ILogger<DashboardViewModel> logger) : base(monitoring)
    {
        _snapshots = snapshots;
        _advisor = advisor;
        _coordinator = coordinator;
        _navigation = navigation;
        _rollback = rollback;
        _profiles = profiles;
        _settings = settings;
        _runtime = runtime;
        _logger = logger;

        var hidden = settings.Current.DashboardHiddenWidgets;
        foreach (var (key, tile) in new[] { ("cpu", _cpu), ("gpu", _gpu), ("ram", _ram), ("disk", _disk), ("network", _network), ("temperature", _temperature) })
            TileOptions.Add(new DashboardTileOption(key, tile, !hidden.Contains(key), OnTileOptionChanged));
        RebuildTiles();

        _coordinator.SystemChanged += (_, _) => Ui.Post(() =>
        {
            _lastAnalysis = DateTimeOffset.MinValue;
            if (IsActive) _ = RefreshStateAsync();
        });
        _profiles.ActiveProfileChanged += (_, _) => Ui.Post(RefreshQuickFacts);
    }

    public override string Key => "dashboard";
    public override string Title => "Dashboard";
    public override string Subtitle => "Aktueller Zustand Ihres Systems. Alle Werte sind gemessen – nichts wird geschätzt oder beschönigt.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<MetricTileViewModel> Tiles { get; } = new();
    public ObservableCollection<DashboardTileOption> TileOptions { get; } = new();
    public ObservableCollection<RecommendationViewModel> Recommendations { get; } = new();
    public ObservableCollection<string> Notices { get; } = new();

    [ObservableProperty] private string _healthTitle = "Systemzustand wird ermittelt …";
    [ObservableProperty] private string _healthDetail = "Die erste Analyse läuft. Sie liest nur Daten und verändert nichts.";
    [ObservableProperty] private string _healthLevel = "info";
    [ObservableProperty] private string _systemSummary = "";
    [ObservableProperty] private string _activeProfile = "Kein Profil aktiv";
    [ObservableProperty] private string _powerSummary = "–";
    [ObservableProperty] private string _lastChangeTitle = "Noch keine Änderung";
    [ObservableProperty] private string _lastChangeDetail = "Sobald Sie etwas ändern, erscheint es hier und im Verlauf.";
    [ObservableProperty] private bool _canUndoLast;
    [ObservableProperty] private bool _hasRecommendations;
    [ObservableProperty] private bool _hasNotices;
    [ObservableProperty] private string _recommendationSummary = "";
    [ObservableProperty] private string _uptime = "–";

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        await base.OnNavigatedToAsync(parameter).ConfigureAwait(true);
        await RefreshStateAsync().ConfigureAwait(true);
    }

    protected override void OnMetrics(SystemMetrics m)
    {
        Fill(_cpu, MetricId.CpuTotal, m, m.CpuFrequencyMhz is { } mhz ? $"Takt {mhz / 1000:0.00} GHz · {m.ProcessCount?.ToString() ?? "–"} Prozesse" : $"{m.ProcessCount?.ToString() ?? "–"} Prozesse");
        Fill(_gpu, MetricId.GpuUsage, m, m.GpuDedicatedUsedBytes is { } used ? "Grafikspeicher " + ByteFormat.Format(used) + (m.GpuDedicatedTotalBytes is > 0 ? " von " + ByteFormat.Format(m.GpuDedicatedTotalBytes.Value) : "") : "");
        Fill(_ram, MetricId.MemoryUsedPercent, m, m.MemoryTotalBytes > 0 ? $"{ByteFormat.Format((double)(m.MemoryTotalBytes - Math.Min(m.MemoryAvailableBytes, m.MemoryTotalBytes)))} von {ByteFormat.Format((double)m.MemoryTotalBytes)} belegt" : "");
        Fill(_disk, MetricId.DiskActive, m, m.DiskReadBytesPerSecond is not null ? $"Lesen {ByteFormat.Rate(m.DiskReadBytesPerSecond ?? 0)} · Schreiben {ByteFormat.Rate(m.DiskWriteBytesPerSecond ?? 0)}" : "");
        Fill(_network, MetricId.NetworkDown, m, "Upload " + (m.NetworkUpBytesPerSecond is { } up ? ByteFormat.Rate(up) : "n. v."), MetricId.NetworkUp);
        if (_network.IsAvailable) _network.Value = "↓ " + _network.Value;
        Fill(_temperature, MetricId.ThermalZone, m, m.CpuPackageCelsius is not null ? "CPU-Paket (Sensor)" : "ACPI-Thermalzone des Mainboards");

        Uptime = m.Uptime > TimeSpan.Zero ? FormatDuration(m.Uptime) : "–";
        if (m.OnAcPower is { } ac)
            PowerSummary = (_snapshotPlan ?? "Energiesparplan unbekannt") + (ac ? " · Netzbetrieb" : $" · Akku {m.BatteryPercent?.ToString() ?? "–"} %");
    }

    private string? _snapshotPlan;

    private static string FormatDuration(TimeSpan span)
        => span.TotalDays >= 1 ? $"{(int)span.TotalDays} T {span.Hours} Std" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} Std {span.Minutes} Min" : $"{span.Minutes} Min";

    private void OnTileOptionChanged(DashboardTileOption option)
    {
        RebuildTiles();
        _settings.Update(s => s with { DashboardHiddenWidgets = TileOptions.Where(o => !o.IsVisible).Select(o => o.Key).ToList() });
    }

    private void RebuildTiles()
    {
        Tiles.Clear();
        foreach (var option in TileOptions.Where(o => o.IsVisible)) Tiles.Add(option.Tile);
    }

    private void RefreshQuickFacts()
    {
        ActiveProfile = _profiles.ActiveProfile?.Name ?? "Kein Profil aktiv";
        _lastChange = _rollback.FindLastActive();
        CanUndoLast = _lastChange is not null;
        if (_lastChange is { } last)
        {
            LastChangeTitle = last.ActionName;
            LastChangeDetail = $"{last.Timestamp.ToLocalTime():dd.MM.yyyy HH:mm} · {last.OriginalDisplay} → {last.IntendedDisplay}";
        }
        else
        {
            LastChangeTitle = "Keine aktive Änderung";
            LastChangeDetail = "Es gibt derzeit nichts rückgängig zu machen.";
        }
    }

    private async Task RefreshStateAsync()
    {
        await Task.Run(RefreshQuickFactsData).ConfigureAwait(true);
        RefreshQuickFacts();

        if (_analysisRunning || DateTimeOffset.Now - _lastAnalysis < TimeSpan.FromSeconds(90)) return;
        _analysisRunning = true;
        IsLoading = true;
        try
        {
            var goal = _settings.Current.PrimaryGoal;
            var (snapshot, recommendations) = await Task.Run(async () =>
            {
                var captured = await _snapshots.CaptureAsync().ConfigureAwait(false);
                return (captured, _advisor.Analyze(captured, goal));
            }).ConfigureAwait(true);
            _lastAnalysis = DateTimeOffset.Now;
            ApplyAnalysis(snapshot, recommendations);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Die Dashboard-Analyse ist fehlgeschlagen.");
            HealthTitle = "Analyse nicht möglich";
            HealthDetail = "Die Systemanalyse konnte nicht abgeschlossen werden: " + ex.Message;
            HealthLevel = "warn";
        }
        finally
        {
            _analysisRunning = false;
            IsLoading = false;
        }
    }

    // Journalzugriffe laufen bewusst nicht auf dem Oberflächenthread.
    private void RefreshQuickFactsData() => _lastChange = _rollback.FindLastActive();

    private void ApplyAnalysis(SystemSnapshot snapshot, IReadOnlyList<Recommendation> recommendations)
    {
        var info = snapshot.Info;
        _snapshotPlan = snapshot.ActivePowerPlan?.Name ?? info.ActivePowerPlan;
        var memory = info.MemoryTotalBytes > 0 ? ByteFormat.Format(info.MemoryTotalBytes) + " RAM" : null;
        var gpu = info.Gpus.FirstOrDefault(g => !g.IsIntegrated)?.Name ?? info.Gpus.FirstOrDefault()?.Name;
        SystemSummary = string.Join(" · ", new[] { info.OsName + (info.OsDisplayVersion is { } v ? " " + v : ""), info.CpuName, memory, gpu }.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (Monitoring.Latest is { } latest) OnMetrics(latest);

        Recommendations.Clear();
        foreach (var recommendation in recommendations.OrderByDescending(r => r.Severity).Take(4))
            Recommendations.Add(new RecommendationViewModel(recommendation, _coordinator, _navigation));
        HasRecommendations = Recommendations.Count > 0;
        RecommendationSummary = recommendations.Count > Recommendations.Count ? $"{Recommendations.Count} von {recommendations.Count} Empfehlungen" : "";

        var critical = recommendations.Count(r => r.Severity == RecommendationSeverity.Critical);
        var warnings = recommendations.Count(r => r.Severity == RecommendationSeverity.Warning);
        var suggestions = recommendations.Count - critical - warnings;
        if (critical > 0)
        {
            HealthLevel = "critical";
            HealthTitle = critical == 1 ? "1 kritischer Befund" : $"{critical} kritische Befunde";
            HealthDetail = "Bitte prüfen Sie die Empfehlungen unten. Die Befunde beruhen auf gemessenen Werten dieses Systems.";
        }
        else if (warnings > 0)
        {
            HealthLevel = "warn";
            HealthTitle = warnings == 1 ? "1 Warnung" : $"{warnings} Warnungen";
            HealthDetail = "Es gibt Auffälligkeiten, die sich prüfen lassen. Nichts davon wird ohne Ihre Zustimmung geändert.";
        }
        else if (suggestions > 0)
        {
            HealthLevel = "info";
            HealthTitle = "Keine dringenden Auffälligkeiten";
            HealthDetail = suggestions == 1 ? "Es liegt 1 optionaler Vorschlag vor." : $"Es liegen {suggestions} optionale Vorschläge vor.";
        }
        else
        {
            HealthLevel = "ok";
            HealthTitle = "Keine Auffälligkeiten gefunden";
            HealthDetail = "Die regelbasierte Analyse hat in den gemessenen Werten nichts gefunden, das eine Änderung nahelegt.";
        }

        Notices.Clear();
        if (info.PendingReboot)
            Notices.Add("Windows wartet auf einen Neustart" + (info.PendingRebootReasons.Count > 0 ? " (" + string.Join(", ", info.PendingRebootReasons) + ")" : "") + ". Messwerte und Benchmarks sind erst danach aussagekräftig.");
        if (_runtime.Interrupted.Count > 0)
            Notices.Add($"{_runtime.Interrupted.Count} Änderung(en) wurden beim letzten Lauf unterbrochen. Im Verlauf sehen Sie den Stand und können gesicherte Zustände wiederherstellen.");
        if (snapshot.Context.IsElevated)
            Notices.Add("Die Anwendung läuft mit Administratorrechten. Empfohlen ist der Start mit Standardrechten.");
        if (info.IsVirtualMachine)
            Notices.Add("Virtuelle Maschine erkannt: Sensorwerte, Energieoptionen und Benchmarks sind hier nur eingeschränkt aussagekräftig.");
        HasNotices = Notices.Count > 0;
    }

    [RelayCommand]
    private void OptimizeNow() => _navigation.Navigate("oneclick", "analyze");

    [RelayCommand]
    private void Open(string? pageKey)
    {
        if (!string.IsNullOrEmpty(pageKey)) _navigation.Navigate(pageKey);
    }

    [RelayCommand]
    private Task UndoLastAsync() => GuardAsync(async () => await _coordinator.UndoLastAsync().ConfigureAwait(true), "Die letzte Änderung konnte nicht rückgängig gemacht werden.");

    [RelayCommand]
    private Task RefreshAsync()
    {
        _lastAnalysis = DateTimeOffset.MinValue;
        return RefreshStateAsync();
    }
}
