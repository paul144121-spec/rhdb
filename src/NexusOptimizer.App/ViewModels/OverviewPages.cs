using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Optimization;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Reporting;
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

/// <summary>Auswahlwert mit deutscher Beschriftung für Auswahllisten.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class WorkflowStepViewModel : ObservableObject
{
    public WorkflowStepViewModel(int number, string title, string description)
    {
        Number = number;
        Title = title;
        Description = description;
    }

    public int Number { get; }
    public string Title { get; }
    public string Description { get; }
    /// <summary>pending · active · done · skipped · failed</summary>
    [ObservableProperty] private string _state = "pending";
    [ObservableProperty] private string _note = "";
}

public sealed partial class ModeOptionViewModel : ObservableObject
{
    public ModeOptionViewModel(OptimizationModeInfo info) => Info = info;
    public OptimizationModeInfo Info { get; }
    public string Name => Info.Name;
    public string Summary => Info.Summary;
    public IReadOnlyList<string> Points => Info.Points;
    public string? Warning => Info.Warning;
    public RiskLevel HighestRisk => Info.HighestRisk;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Ein-Klick-Optimierung: Modus wählen → Analyse → Vorschau → Bestätigung → Ausführung mit Verifikation → Ergebnis.</summary>
public sealed partial class OneClickViewModel : PageViewModel
{
    private readonly OneClickOptimizer _optimizer;
    private readonly ActionCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly ILogger<OneClickViewModel> _logger;
    private string? _lastBatchId;

    public OneClickViewModel(OneClickOptimizer optimizer, ActionCoordinator coordinator, SettingsService settings, INavigationService navigation, ILogger<OneClickViewModel> logger)
    {
        _optimizer = optimizer;
        _coordinator = coordinator;
        _settings = settings;
        _navigation = navigation;
        _logger = logger;

        foreach (var info in OptimizationModes.All) Modes.Add(new ModeOptionViewModel(info));
        _selectedMode = Modes[0];
        _selectedMode.IsSelected = true;

        string[][] steps =
        {
            new[] { "Systemerkennung", "Hardware, Windows-Version und Stromversorgung werden gelesen." },
            new[] { "Zustandserfassung", "Aktuelle Einstellungen, Autostart, Energieplan und Messwerte werden erfasst." },
            new[] { "Analyse", "Die regelbasierte Analyse bewertet die gemessenen Werte." },
            new[] { "Kandidatenauswahl", "Passend zum gewählten Modus werden mögliche Änderungen zusammengestellt." },
            new[] { "Kompatibilitätsprüfung", "Jede Änderung wird gegen Windows-Version, Hardware und Richtlinien geprüft." },
            new[] { "Risikobewertung", "Risikostufen werden mit Ihrer Freigabe abgeglichen; Unzulässiges wird verworfen." },
            new[] { "Planerstellung", "Abhängigkeiten, Konflikte und Reihenfolge werden aufgelöst." },
            new[] { "Vorschau", "Sie sehen jede geplante Änderung mit Ist- und Zielzustand." },
            new[] { "Bestätigung", "Ohne Ihre Zustimmung wird nichts verändert." },
            new[] { "Sicherung", "Der Ausgangszustand jeder Änderung wird im Journal gesichert." },
            new[] { "Rechteanforderung", "Nur falls nötig fragt Windows sichtbar nach Administratorrechten (UAC)." },
            new[] { "Ausführung", "Die Änderungen werden Schritt für Schritt angewendet." },
            new[] { "Verifikation", "Jede Änderung wird durch erneutes Lesen überprüft." },
            new[] { "Ergebnisbericht", "Sie sehen, was tatsächlich angewendet wurde – und was nicht." },
            new[] { "Rückgängig und Rückmeldung", "Der Lauf lässt sich zurücknehmen; Ihre Entscheidung wird für künftige Vorschläge gemerkt." },
        };
        for (var i = 0; i < steps.Length; i++) Steps.Add(new WorkflowStepViewModel(i + 1, steps[i][0], steps[i][1]));
    }

    public override string Key => "oneclick";
    public override string Title => "Ein-Klick-Optimierung";
    public override string Subtitle => "Analyse, Vorschau und Ausführung in einem geführten Ablauf. Vor jeder Änderung sehen Sie, was passiert – und können alles zurücknehmen.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<ModeOptionViewModel> Modes { get; } = new();
    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = new();
    public ObservableCollection<string> ResultLines { get; } = new();

    [ObservableProperty] private ModeOptionViewModel _selectedMode;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(OptimizeCommand))] private bool _isRunning;
    [ObservableProperty] private string _status = "Bereit. Wählen Sie einen Modus und starten Sie die Analyse.";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultLevel = "info";
    [ObservableProperty] private bool _canUndoRun;
    [ObservableProperty] private bool _experimentalLocked;

    partial void OnSelectedModeChanged(ModeOptionViewModel? oldValue, ModeOptionViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        ExperimentalLocked = newValue.Info.Mode == OptimizationMode.Experimental && !_settings.Current.ExperimentalUnlocked;
    }

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        ExperimentalLocked = SelectedMode.Info.Mode == OptimizationMode.Experimental && !_settings.Current.ExperimentalUnlocked;
        if (parameter is "analyze" && !IsRunning) await OptimizeAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectMode(ModeOptionViewModel? mode)
    {
        if (mode is not null && !IsRunning) SelectedMode = mode;
    }

    private void SetSteps(int from, int to, string state, string note = "")
    {
        foreach (var step in Steps.Where(s => s.Number >= from && s.Number <= to))
        {
            step.State = state;
            step.Note = note;
        }
    }

    private bool CanOptimize() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanOptimize))]
    private async Task OptimizeAsync()
    {
        var mode = SelectedMode.Info;
        IsRunning = true;
        HasResult = false;
        CanUndoRun = false;
        ResultLines.Clear();
        SetSteps(1, 15, "pending");
        ErrorMessage = null;

        try
        {
            SetSteps(1, 1, "active");
            var progress = new Progress<string>(text =>
            {
                Status = text;
                if (text.StartsWith("Hardware", StringComparison.Ordinal)) SetSteps(1, 1, "active");
                else if (text.StartsWith("Optimierungsmöglichkeiten", StringComparison.Ordinal)) { SetSteps(1, 2, "done"); SetSteps(3, 4, "active"); }
                else if (text.StartsWith("Kompatibilität", StringComparison.Ordinal)) { SetSteps(1, 4, "done"); SetSteps(5, 7, "active"); }
                else { SetSteps(1, 1, "done"); SetSteps(2, 2, "active"); }
            });

            var settings = _settings.Current;
            var analysis = await Task.Run(() => _optimizer.AnalyzeAsync(mode.Mode, settings.ToRiskPolicy(), settings.PrimaryGoal, progress)).ConfigureAwait(true);
            SetSteps(1, 7, "done");

            var plan = analysis.Plan;
            var alreadyApplied = plan.Rejected.Count(r => r.Kind == RejectionKind.AlreadyApplied);
            var rejected = plan.Rejected.Count - alreadyApplied;
            ResultLines.Add($"{plan.Steps.Count} mögliche Änderung(en) für diesen Modus gefunden.");
            if (alreadyApplied > 0) ResultLines.Add($"{alreadyApplied} Einstellung(en) liegen bereits im Zielzustand vor.");
            if (rejected > 0) ResultLines.Add($"{rejected} Kandidat(en) wurden verworfen (nicht kompatibel, nicht freigegeben oder nicht lesbar).");
            foreach (var note in analysis.Notes) ResultLines.Add(note);

            if (plan.IsEmpty)
            {
                SetSteps(8, 15, "skipped");
                ResultTitle = alreadyApplied > 0 && rejected == 0 ? "Nichts zu tun – alles bereits im Zielzustand" : "Keine ausführbare Änderung gefunden";
                ResultLevel = "info";
                Status = "Analyse abgeschlossen. Es wurde nichts verändert.";
                HasResult = true;
                return;
            }

            SetSteps(8, 9, "active");
            Status = "Vorschau wird angezeigt – bitte prüfen und bestätigen.";
            var result = await _coordinator.RunPlanAsync(plan, mode.Name, mode.Name, analysis.Notes).ConfigureAwait(true);

            if (result is null || result.Busy)
            {
                SetSteps(8, 8, "done");
                SetSteps(9, 9, "skipped", "Nicht bestätigt");
                SetSteps(10, 15, "skipped");
                ResultTitle = "Abgebrochen – es wurde nichts verändert";
                ResultLevel = "info";
                Status = "Der Vorgang wurde vor der Ausführung beendet.";
                HasResult = true;
                return;
            }

            SetSteps(8, 10, "done");
            SetSteps(11, 11, result.Steps.Any(s => s.Step.Privilege == PrivilegeLevel.Administrator) ? "done" : "skipped", result.Steps.Any(s => s.Step.Privilege == PrivilegeLevel.Administrator) ? "" : "Nicht erforderlich");
            SetSteps(12, 13, result.FailedCount == 0 ? "done" : "failed");
            SetSteps(14, 15, "done");

            _lastBatchId = result.BatchId;
            CanUndoRun = result.Steps.Any(s => s.Success && s.Step.Metadata.IsReversible);
            ResultLines.Add($"{result.SucceededCount} von {result.Steps.Count} Änderung(en) angewendet und durch erneutes Lesen bestätigt.");
            foreach (var step in result.Steps.Where(s => !s.Success)) ResultLines.Add($"Nicht angewendet: {step.Step.Preview.Title} – {step.Message}");
            if (result.Cancelled) ResultLines.Add("Der Vorgang wurde abgebrochen; bereits angewendete Änderungen bleiben bestehen.");
            if (result.Restart != RestartRequirement.None) ResultLines.Add("Hinweis: " + result.Restart.ToGerman() + ".");
            ResultLines.Add("Ob sich die Änderungen spürbar auswirken, hängt von Ihrem System ab. Eine Leistungssteigerung wird nicht zugesagt – messen können Sie sie im Benchmark-Center.");

            ResultTitle = result.FailedCount == 0 && !result.Cancelled ? "Optimierung abgeschlossen" : result.SucceededCount > 0 ? "Teilweise ausgeführt" : "Nicht ausgeführt";
            ResultLevel = result.FailedCount == 0 && !result.Cancelled ? "ok" : "warn";
            Status = "Abgeschlossen.";
            HasResult = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ein-Klick-Optimierung fehlgeschlagen.");
            foreach (var step in Steps.Where(s => s.State == "active")) step.State = "failed";
            ErrorMessage = "Die Analyse ist fehlgeschlagen. Es wurde nichts verändert: " + ex.Message;
            Status = "Fehlgeschlagen.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task UndoRunAsync()
    {
        if (_lastBatchId is null) return;
        if (await _coordinator.UndoBatchAsync(_lastBatchId, SelectedMode.Name).ConfigureAwait(true))
        {
            CanUndoRun = false;
            ResultLines.Add("Der Lauf wurde rückgängig gemacht.");
        }
    }

    [RelayCommand]
    private void Open(string? pageKey)
    {
        if (!string.IsNullOrEmpty(pageKey)) _navigation.Navigate(pageKey);
    }
}

/// <summary>KI-Optimierer: Ziele, Betriebsart, regelbasierte Empfehlungen und der eng begrenzte Automatikmodus.</summary>
public sealed partial class AiOptimizerViewModel : PageViewModel
{
    private readonly SystemSnapshotService _snapshots;
    private readonly RuleBasedAdvisor _advisor;
    private readonly ActionCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly IFeedbackStore _feedback;
    private readonly AutoPilot _autoPilot;
    private readonly DialogService _dialogs;
    private readonly ILogger<AiOptimizerViewModel> _logger;
    private SystemSnapshot? _snapshot;
    private IReadOnlyList<Recommendation> _recommendations = Array.Empty<Recommendation>();
    private bool _loadingSettings;

    public AiOptimizerViewModel(SystemSnapshotService snapshots, RuleBasedAdvisor advisor, ActionCoordinator coordinator, SettingsService settings, INavigationService navigation,
        IFeedbackStore feedback, AutoPilot autoPilot, DialogService dialogs, ILogger<AiOptimizerViewModel> logger)
    {
        _snapshots = snapshots;
        _advisor = advisor;
        _coordinator = coordinator;
        _settings = settings;
        _navigation = navigation;
        _feedback = feedback;
        _autoPilot = autoPilot;
        _dialogs = dialogs;
        _logger = logger;

        Goals = Enum.GetValues<OptimizationGoal>().Select(g => new Choice<OptimizationGoal>(g, g.ToGerman())).ToList();
        AiModes = new[]
        {
            new Choice<AiMode>(AiMode.Analysis, "Analyse – nur Befunde, keine Vorschläge zur Ausführung"),
            new Choice<AiMode>(AiMode.Recommendation, "Empfehlung – Vorschläge, die Sie einzeln prüfen"),
            new Choice<AiMode>(AiMode.Assisted, "Assistiert – Vorschläge gesammelt in einer Vorschau"),
            new Choice<AiMode>(AiMode.Auto, "Automatik – nur freigegebene, risikoarme Änderungen ohne Rückfrage"),
            new Choice<AiMode>(AiMode.Gaming, "Gaming – Analyse mit Blick auf Spieleleistung"),
            new Choice<AiMode>(AiMode.Troubleshooting, "Fehlersuche – Ursachen für Auffälligkeiten eingrenzen"),
            new Choice<AiMode>(AiMode.Learning, "Lernmodus – Ihre Entscheidungen fließen in künftige Vorschläge ein"),
        };
        _selectedGoal = Goals[0];
        _selectedAiMode = AiModes[1];
        _coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = AnalyzeAsync(); });
    }

    public override string Key => "ai";
    public override string Title => "KI-Optimierer";
    public override string Subtitle => "Empfehlungen auf Basis gemessener Werte – mit Begründung, Belegen und Risikoangabe.";
    protected override ILogger Logger => _logger;

    public IReadOnlyList<Choice<OptimizationGoal>> Goals { get; }
    public IReadOnlyList<Choice<AiMode>> AiModes { get; }
    public ObservableCollection<RecommendationViewModel> Recommendations { get; } = new();
    public ObservableCollection<InfoRow> Learning { get; } = new();
    public string EngineDescription => RuleBasedAdvisor.EngineDescription;

    [ObservableProperty] private Choice<OptimizationGoal> _selectedGoal;
    [ObservableProperty] private Choice<AiMode> _selectedAiMode;
    [ObservableProperty] private string _providerNote = "";
    [ObservableProperty] private string _summary = "Noch keine Analyse in dieser Sitzung.";
    [ObservableProperty] private bool _hasRecommendations;
    [ObservableProperty] private bool _canApplyAll;
    [ObservableProperty] private bool _isAnalysisOnly;
    [ObservableProperty] private bool _isAutoMode;
    [ObservableProperty] private bool _autoEnabled;
    [ObservableProperty] private string _autoStatus = "";
    [ObservableProperty] private string _autoLimits = "";
    [ObservableProperty] private bool _isBusy;

    partial void OnSelectedGoalChanged(Choice<OptimizationGoal> value)
    {
        if (_loadingSettings) return;
        _settings.Update(s => s with { PrimaryGoal = value.Value });
        if (IsActive) _ = AnalyzeAsync();
    }

    partial void OnSelectedAiModeChanged(Choice<AiMode> value)
    {
        IsAnalysisOnly = value.Value == AiMode.Analysis;
        IsAutoMode = value.Value == AiMode.Auto;
        if (_loadingSettings) return;
        // Der Wechsel der Betriebsart schaltet die Automatik nie stillschweigend ein – das geschieht nur über den Schalter mit Bestätigung.
        _settings.Update(s => s with { AiMode = value.Value, AiAutoEnabled = value.Value == AiMode.Auto && s.AiAutoEnabled });
        LoadSettings();
        if (IsActive) RebuildList();
    }

    partial void OnAutoEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _ = ChangeAutoAsync(value);
    }

    private async Task ChangeAutoAsync(bool enable)
    {
        if (enable)
        {
            var confirmed = await _dialogs.ConfirmAsync("Automatikmodus einschalten",
                "Im Automatikmodus wendet die Anwendung etwa alle 30 Minuten regelbasierte Empfehlungen ohne Rückfrage an – ausschließlich Änderungen, die\n\n" +
                "• höchstens die für unbeaufsichtigte Ausführung freigegebene Risikostufe haben (nie höher als „Niedrig“),\n" +
                "• vollständig umkehrbar und für die Automatik zugelassen sind,\n" +
                "• keine Sicherheitsfunktionen betreffen.\n\n" +
                "Jede Änderung steht im Verlauf und lässt sich zurücknehmen. Texte eines Sprachmodells werden nie ausgeführt.",
                "Einschalten", acknowledgement: "Ich habe die Grenzen des Automatikmodus verstanden.").ConfigureAwait(true);
            if (!confirmed)
            {
                LoadSettings();
                return;
            }
        }
        _settings.Update(s => s with { AiAutoEnabled = enable });
        LoadSettings();
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = _settings.Current;
            SelectedGoal = Goals.First(g => g.Value == settings.PrimaryGoal);
            SelectedAiMode = AiModes.First(m => m.Value == settings.AiMode);
            AutoEnabled = settings.AiAutoEnabled && settings.AiMode == AiMode.Auto;
            IsAnalysisOnly = settings.AiMode == AiMode.Analysis;
            IsAutoMode = settings.AiMode == AiMode.Auto;
            ProviderNote = settings.AiProvider switch
            {
                AiProviderKind.RuleBased => "Aktiv ist das Regelwerk ohne Sprachmodell. Alle Empfehlungen entstehen lokal aus festen, nachvollziehbaren Regeln.",
                AiProviderKind.Ollama => "Für Erklärungen im KI-Chat ist ein lokales Sprachmodell (Ollama) eingestellt. Die Empfehlungen auf dieser Seite bleiben regelbasiert.",
                _ => "Für Erklärungen im KI-Chat ist ein Cloud-Sprachmodell eingestellt. Die Empfehlungen auf dieser Seite bleiben regelbasiert und entstehen lokal.",
            };
            var policy = settings.ToRiskPolicy();
            AutoLimits = $"Unbeaufsichtigt zulässig: bis Risikostufe „{policy.MaxUnattendedRisk.ToGerman()}“" +
                         (policy.UnattendedAllowList is { Count: > 0 } list ? $", beschränkt auf {list.Count} ausdrücklich freigegebene Aktion(en)." : ", alle für die Automatik zugelassenen Aktionen.");
            AutoStatus = _autoPilot.LastRun is { } last ? $"Letzter Lauf {last.ToLocalTime():dd.MM. HH:mm}: {_autoPilot.LastResult}" : _autoPilot.LastResult;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        LoadSettings();
        RefreshLearning();
        if (_snapshot is null || DateTimeOffset.UtcNow - _snapshot.CapturedAt > TimeSpan.FromMinutes(2)) await AnalyzeAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsLoading = true;
        try
        {
            var goal = SelectedGoal.Value;
            (_snapshot, _recommendations) = await Task.Run(async () =>
            {
                var snapshot = await _snapshots.CaptureAsync().ConfigureAwait(false);
                return (snapshot, _advisor.Analyze(snapshot, goal));
            }).ConfigureAwait(true);
            RebuildList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analyse fehlgeschlagen.");
            ErrorMessage = "Die Analyse konnte nicht abgeschlossen werden: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsLoading = false;
        }
    }

    private void RebuildList()
    {
        Recommendations.Clear();
        foreach (var recommendation in _recommendations.OrderByDescending(r => r.Severity))
        {
            // Im reinen Analysemodus werden Befunde ohne ausführbare Aktionen angezeigt.
            var shown = IsAnalysisOnly ? recommendation with { Actions = Array.Empty<PlanRequest>() } : recommendation;
            Recommendations.Add(new RecommendationViewModel(shown, _coordinator, _navigation, RefreshLearning));
        }
        HasRecommendations = Recommendations.Count > 0;
        CanApplyAll = !IsAnalysisOnly && _recommendations.Any(r => r.Actions.Count > 0);
        var withActions = _recommendations.Count(r => r.Actions.Count > 0);
        Summary = _recommendations.Count == 0
            ? $"Analyse für das Ziel „{SelectedGoal.Label}“: keine Auffälligkeiten gefunden."
            : $"Analyse für das Ziel „{SelectedGoal.Label}“: {_recommendations.Count} Befund(e), davon {withActions} mit vorgeschlagener Änderung.";
    }

    private void RefreshLearning()
    {
        Learning.Clear();
        int Count(FeedbackKind kind) => _feedback.GetCounts(kind).Values.Sum();
        Learning.Add(new InfoRow("Angenommene Änderungen", Count(FeedbackKind.Accepted).ToString()));
        Learning.Add(new InfoRow("Abgelehnte Vorschläge", Count(FeedbackKind.Declined).ToString()));
        Learning.Add(new InfoRow("Als nachteilig gemessen", Count(FeedbackKind.Regressed).ToString()));
        Learning.Add(new InfoRow("Als hilfreich gemessen", Count(FeedbackKind.Helpful).ToString()));
        Learning.Add(new InfoRow("Derzeit nicht mehr vorgeschlagen", _feedback.GetDeclined().Count + " Aktion(en)"));
    }

    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        var requests = _recommendations.SelectMany(r => r.Actions).DistinctBy(r => r.Key).ToList();
        if (requests.Count == 0) return;
        await _coordinator.RunAsync(requests, ExecutionSource.AiAssisted, "Empfohlene Änderungen", "KI-Optimierer").ConfigureAwait(true);
        RefreshLearning();
    }

    [RelayCommand]
    private async Task RunAutoNowAsync()
    {
        if (!_autoPilot.IsEnabled)
        {
            AutoStatus = "Der Automatikmodus ist ausgeschaltet.";
            return;
        }
        IsBusy = true;
        try
        {
            AutoStatus = "Automatiklauf wird ausgeführt …";
            var result = await Task.Run(() => _autoPilot.RunOnceAsync()).ConfigureAwait(true);
            AutoStatus = $"Letzter Lauf {DateTimeOffset.Now:dd.MM. HH:mm}: {result}";
        }
        finally
        {
            IsBusy = false;
        }
        await AnalyzeAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetLearningAsync()
    {
        if (!await _dialogs.ConfirmAsync("Rückmeldungen löschen", "Alle gemerkten Entscheidungen (angenommen, abgelehnt, gemessen) werden gelöscht. Abgelehnte Vorschläge können danach wieder erscheinen.", "Löschen", danger: true).ConfigureAwait(true)) return;
        _feedback.Clear();
        RefreshLearning();
        await AnalyzeAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private Task ExportAsync() => GuardAsync(async () =>
    {
        var path = _dialogs.SaveFile("Textbericht (*.txt)|*.txt|JSON (*.json)|*.json|CSV (*.csv)|*.csv", $"nexus-empfehlungen-{DateTime.Now:yyyyMMdd-HHmm}.txt", "Empfehlungen exportieren");
        if (path is null) return;
        var format = Path.GetExtension(path).ToLowerInvariant() switch { ".json" => ReportFormat.Json, ".csv" => ReportFormat.Csv, _ => ReportFormat.Text };
        var content = ReportExporter.ExportRecommendations(_recommendations, _snapshot, format);
        await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8).ConfigureAwait(true);
    }, "Der Bericht konnte nicht gespeichert werden.");

    [RelayCommand]
    private void Open(string? pageKey)
    {
        if (!string.IsNullOrEmpty(pageKey)) _navigation.Navigate(pageKey);
    }
}

public sealed partial class AlertRuleViewModel : ObservableObject
{
    private readonly AlertRule _rule;
    private readonly Action _changed;

    public AlertRuleViewModel(AlertRule rule, Action changed)
    {
        _rule = rule;
        _changed = changed;
        _enabled = rule.Enabled;
        _threshold = rule.Threshold;
        _sustainSeconds = rule.SustainSeconds;
    }

    public string Name => _rule.Name;
    public string MetricName => MetricCatalog.Get(_rule.Metric).Name;
    public string ComparisonText => _rule.Comparison == AlertComparison.Above ? "über" : "unter";
    public string UnitText => MetricCatalog.Get(_rule.Metric).Unit switch
    {
        MetricUnit.Percent => "%", MetricUnit.Celsius => "°C", MetricUnit.Milliseconds => "ms", MetricUnit.Megahertz => "MHz", MetricUnit.Bytes => "Bytes", MetricUnit.BytesPerSecond => "Bytes/s", _ => "",
    };
    public bool IsCustom => !_rule.Id.StartsWith("default-", StringComparison.Ordinal);

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _threshold;
    [ObservableProperty] private int _sustainSeconds;

    partial void OnEnabledChanged(bool value) => _changed();
    partial void OnThresholdChanged(double value) => _changed();
    partial void OnSustainSecondsChanged(int value) => _changed();

    public AlertRule ToRule() => _rule with
    {
        Enabled = Enabled,
        Threshold = double.IsFinite(Threshold) ? Threshold : _rule.Threshold,
        SustainSeconds = Math.Clamp(SustainSeconds, 0, 3600),
    };
}

/// <summary>Systemmonitor: Live-Diagramme, gespeicherte Historie mit Zoom, Warnregeln und Export.</summary>
public sealed partial class MonitorViewModel : LivePageViewModel
{
    private static readonly (MetricId Id, string Accent, double Max, MetricId? Secondary)[] TileDefinitions =
    {
        (MetricId.CpuTotal, "Brush.Accent", 100, null), (MetricId.CpuFrequency, "Brush.Accent", double.NaN, null), (MetricId.CpuPerformanceLimit, "Brush.Amber", 100, null),
        (MetricId.ThermalZone, "Brush.Amber", 110, null), (MetricId.GpuUsage, "Brush.Cyan", 100, null), (MetricId.GpuMemoryPercent, "Brush.Cyan", 100, null),
        (MetricId.GpuTemperature, "Brush.Amber", 110, null), (MetricId.MemoryUsedPercent, "Brush.Blue", 100, null), (MetricId.CommitPercent, "Brush.Blue", 100, null),
        (MetricId.DiskActive, "Brush.Green", 100, null), (MetricId.DiskRead, "Brush.Green", double.NaN, MetricId.DiskWrite), (MetricId.DiskLatency, "Brush.Green", double.NaN, null),
        (MetricId.NetworkDown, "Brush.Cyan", double.NaN, MetricId.NetworkUp), (MetricId.Latency, "Brush.Cyan", double.NaN, null), (MetricId.BatteryPercent, "Brush.Green", 100, null),
    };

    private readonly IMetricHistoryStore _history;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ILogger<MonitorViewModel> _logger;
    private readonly List<(MetricId Id, MetricId? Secondary, MetricTileViewModel Tile)> _tiles = new();
    private bool _loadingRules;

    public MonitorViewModel(MonitoringService monitoring, IMetricHistoryStore history, SettingsService settings, DialogService dialogs, ILogger<MonitorViewModel> logger) : base(monitoring)
    {
        _history = history;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;

        foreach (var (id, accent, max, secondary) in TileDefinitions)
        {
            var definition = MetricCatalog.Get(id);
            var tile = new MetricTileViewModel(secondary is null ? definition.Name : definition.Name + " / " + MetricCatalog.Get(secondary.Value).Name, accent, max);
            _tiles.Add((id, secondary, tile));
            Tiles.Add(tile);
        }

        Metrics = MetricCatalog.All.Where(m => m.Id is not (MetricId.ProcessCount or MetricId.ThreadCount)).Select(m => new Choice<MetricId>(m.Id, m.Name)).ToList();
        Ranges = new[]
        {
            new Choice<TimeSpan>(TimeSpan.FromHours(1), "Letzte Stunde"), new Choice<TimeSpan>(TimeSpan.FromHours(6), "Letzte 6 Stunden"),
            new Choice<TimeSpan>(TimeSpan.FromHours(24), "Letzte 24 Stunden"), new Choice<TimeSpan>(TimeSpan.FromDays(7), "Letzte 7 Tage"), new Choice<TimeSpan>(TimeSpan.FromDays(30), "Letzte 30 Tage"),
        };
        _selectedMetric = Metrics[0];
        _selectedRange = Ranges[0];
    }

    public override string Key => "monitor";
    public override string Title => "Systemmonitor";
    public override string Subtitle => "Live-Messwerte aus den Windows-Leistungsindikatoren. Fehlende Sensoren werden als „nicht verfügbar“ gezeigt, nicht geschätzt.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<MetricTileViewModel> Tiles { get; } = new();
    public ObservableCollection<AlertRuleViewModel> AlertRules { get; } = new();
    public IReadOnlyList<Choice<MetricId>> Metrics { get; }
    public IReadOnlyList<Choice<TimeSpan>> Ranges { get; }

    [ObservableProperty] private Choice<MetricId> _selectedMetric;
    [ObservableProperty] private Choice<TimeSpan> _selectedRange;
    [ObservableProperty] private IReadOnlyList<MetricPoint> _historyPoints = Array.Empty<MetricPoint>();
    [ObservableProperty] private MetricUnit _historyUnit = MetricUnit.Percent;
    [ObservableProperty] private string _historySummary = "";
    [ObservableProperty] private string _samplingInfo = "";

    partial void OnSelectedMetricChanged(Choice<MetricId> value) => _ = LoadHistoryAsync();
    partial void OnSelectedRangeChanged(Choice<TimeSpan> value) => _ = LoadHistoryAsync();

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        await base.OnNavigatedToAsync(parameter).ConfigureAwait(true);
        LoadRules();
        var settings = _settings.Current;
        SamplingInfo = $"Abtastintervall {Monitoring.EffectiveInterval.TotalSeconds:0.#} s · Historie " + (settings.PersistHistory ? $"wird {settings.HistoryRetentionDays} Tage lokal gespeichert (15-Sekunden-Mittelwerte)" : "wird nicht gespeichert");
        await LoadHistoryAsync().ConfigureAwait(true);
    }

    protected override void OnMetrics(SystemMetrics metrics)
    {
        foreach (var (id, secondary, tile) in _tiles)
        {
            var detail = secondary is { } s ? MetricCatalog.Get(s).Name + ": " + MetricCatalog.Get(s).Format(MetricCatalog.Get(s).Read(metrics)) : "";
            Fill(tile, id, metrics, detail, secondary);
        }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (!IsActive) return;
        try
        {
            var metric = SelectedMetric.Value;
            var range = SelectedRange.Value;
            var now = DateTimeOffset.UtcNow;
            var points = await Task.Run(() => _history.Query(metric, now - range, now, 800)).ConfigureAwait(true);
            HistoryUnit = MetricCatalog.Get(metric).Unit;
            HistoryPoints = points;
            if (points.Count == 0)
            {
                HistorySummary = _settings.Current.PersistHistory
                    ? "Für diesen Zeitraum liegen noch keine gespeicherten Werte vor. Die Historie füllt sich, während die Anwendung läuft."
                    : "Die Speicherung der Historie ist in den Einstellungen ausgeschaltet.";
            }
            else
            {
                var definition = MetricCatalog.Get(metric);
                HistorySummary = $"{points.Count} Punkte · Mittel {definition.Format(points.Average(p => p.Value))} · Minimum {definition.Format(points.Min(p => p.Value))} · Maximum {definition.Format(points.Max(p => p.Value))}  ·  Mausrad: Zoom, Ziehen: Verschieben, Doppelklick: zurücksetzen";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Historie konnte nicht geladen werden.");
            HistorySummary = "Die Historie konnte nicht geladen werden: " + ex.Message;
        }
    }

    private void LoadRules()
    {
        _loadingRules = true;
        AlertRules.Clear();
        foreach (var rule in _settings.Current.AlertRules) AlertRules.Add(new AlertRuleViewModel(rule, SaveRules));
        _loadingRules = false;
    }

    private void SaveRules()
    {
        if (_loadingRules) return;
        var rules = AlertRules.Select(r => r.ToRule()).ToList();
        _settings.Update(s => s with { AlertRules = rules });
    }

    [RelayCommand]
    private void ResetRules()
    {
        _settings.Update(s => s with { AlertRules = AlertEngine.DefaultRules.ToList() });
        LoadRules();
    }

    [RelayCommand]
    private Task ExportAsync() => GuardAsync(async () =>
    {
        var path = _dialogs.SaveFile("CSV (*.csv)|*.csv|JSON (*.json)|*.json", $"nexus-messwerte-{DateTime.Now:yyyyMMdd-HHmm}.csv", "Messwerte exportieren");
        if (path is null) return;
        var format = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? ReportFormat.Json : ReportFormat.Csv;
        var now = DateTimeOffset.UtcNow;
        var range = SelectedRange.Value;
        var content = await Task.Run(() =>
        {
            var series = MetricCatalog.All.ToDictionary(m => m.Id, m => _history.Query(m.Id, now - range, now, 5000));
            return ReportExporter.ExportMetrics(series.Where(s => s.Value.Count > 0).ToDictionary(s => s.Key, s => s.Value), format);
        }).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8).ConfigureAwait(true);
    }, "Die Messwerte konnten nicht exportiert werden.");
}
