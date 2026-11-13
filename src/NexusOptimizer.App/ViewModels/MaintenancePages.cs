using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Benchmark;
using NexusOptimizer.Core.Diagnostics;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Gaming;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Orchestration;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Tools;

namespace NexusOptimizer.App.ViewModels;

// ───────────────────────── Bereinigung ─────────────────────────

public sealed partial class CleanupCategoryViewModel : ObservableObject
{
    public CleanupCategoryViewModel(CleanupCategory category)
    {
        Category = category;
        _isSelected = category.SelectedByDefault;
    }

    public CleanupCategory Category { get; }
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string Safeguards => Category.Safeguards;
    public bool RequiresAdmin => Category.Privilege == PrivilegeLevel.Administrator;
    public string BlockingText => Category.BlockingProcesses.Count > 0 ? "Wird übersprungen, solange laufen: " + string.Join(", ", Category.BlockingProcesses) : "";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _sizeText = "Noch nicht ermittelt";
    [ObservableProperty] private long _bytes;
    [ObservableProperty] private string? _note;

    public void Apply(CleanupScanResult scan)
    {
        Bytes = scan.Bytes;
        SizeText = scan.IsBlocked ? "Nicht ermittelbar" : $"{ByteFormat.Format(scan.Bytes)} in {scan.Files:N0} Dateien";
        Note = scan.BlockedReason ?? (scan.Inaccessible > 0 ? $"{scan.Inaccessible} Elemente sind nicht lesbar und werden nicht angefasst." : null);
    }
}

/// <summary>Windows-Bereinigung: Vorabprüfung zeigt je Kategorie, was gelöscht würde; gelöscht wird erst nach Vorschau und Bestätigung.</summary>
public sealed partial class CleanupViewModel : PageViewModel
{
    private readonly CleanupEngine _engine;
    private readonly ActionCoordinator _coordinator;
    private readonly ILogger<CleanupViewModel> _logger;
    private bool _subscribed;

    public CleanupViewModel(CleanupEngine engine, ActionCoordinator coordinator, ILogger<CleanupViewModel> logger)
    {
        _engine = engine;
        _coordinator = coordinator;
        _logger = logger;
        foreach (var category in CleanupCategories.All) Categories.Add(new CleanupCategoryViewModel(category));
    }

    public override string Key => "cleanup";
    public override string Title => "Windows-Bereinigung";
    public override string Subtitle => "Temporäre Dateien und Caches. Bereinigt wird nur in fest hinterlegten Ordnern; persönliche Dokumente, Downloads und Desktop sind nie betroffen.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<CleanupCategoryViewModel> Categories { get; } = new();
    [ObservableProperty] private string _totalText = "Noch keine Prüfung durchgeführt.";
    [ObservableProperty] private bool _isScanning;

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = ScanAsync(); });
        }
        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        IsLoading = true;
        try
        {
            var results = await Task.Run(() => Categories.Select(c => (c, scan: SafeScan(c.Category.Id))).ToList()).ConfigureAwait(true);
            foreach (var (category, scan) in results) category.Apply(scan);
            var selected = Categories.Where(c => c.IsSelected).Sum(c => c.Bytes);
            TotalText = $"Insgesamt belegt: {ByteFormat.Format(Categories.Sum(c => c.Bytes))} · aktuell ausgewählt: {ByteFormat.Format(selected)}";
        }
        finally
        {
            IsScanning = false;
            IsLoading = false;
        }
    }

    private CleanupScanResult SafeScan(string id)
    {
        try { return _engine.Scan(id); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prüfung der Kategorie {Id} fehlgeschlagen.", id);
            return new CleanupScanResult(id, 0, 0, Array.Empty<string>(), 0, "Prüfung fehlgeschlagen: " + ex.Message);
        }
    }

    [RelayCommand]
    private void UpdateTotal() => TotalText = $"Insgesamt belegt: {ByteFormat.Format(Categories.Sum(c => c.Bytes))} · aktuell ausgewählt: {ByteFormat.Format(Categories.Where(c => c.IsSelected).Sum(c => c.Bytes))}";

    [RelayCommand]
    private async Task CleanAsync()
    {
        var requests = new List<PlanRequest>();
        var user = Categories.Where(c => c.IsSelected && !c.RequiresAdmin).Select(c => c.Category.Id).ToList();
        var system = Categories.Where(c => c.IsSelected && c.RequiresAdmin).Select(c => c.Category.Id).ToList();
        if (user.Count > 0) requests.Add(new PlanRequest(CleanupAction.UserActionId, ActionParameters.Of(("categories", string.Join(",", user)))));
        if (system.Count > 0) requests.Add(new PlanRequest(CleanupAction.SystemActionId, ActionParameters.Of(("categories", string.Join(",", system)))));
        if (requests.Count == 0)
        {
            TotalText = "Bitte mindestens eine Kategorie auswählen.";
            return;
        }
        await _coordinator.RunAsync(requests, ExecutionSource.User, "Windows-Bereinigung", notes: new[] { "Gelöschte Dateien können nicht wiederhergestellt werden." }).ConfigureAwait(true);
        await ScanAsync().ConfigureAwait(true);
    }
}

// ───────────────────────── Reparatur ─────────────────────────

public sealed partial class RepairToolViewModel : ObservableObject
{
    private readonly SystemToolDefinition _definition;
    private readonly SystemToolAction _action;
    private readonly ActionCoordinator _coordinator;

    public RepairToolViewModel(SystemToolDefinition definition, SystemToolAction action, ActionCoordinator coordinator, string? drive)
    {
        _definition = definition;
        _action = action;
        _coordinator = coordinator;
        Drive = drive;
    }

    public string Name => _definition.Name;
    public string Description => _definition.Description;
    public string ExpectedEffect => _definition.ExpectedEffect;
    public string SideEffects => _definition.SideEffects;
    public string Duration => _definition.DurationHint;
    public RiskLevel Risk => _definition.Risk;
    public bool IsReadOnly => _definition.ReadOnly;
    public string? Drive { get; }
    public bool NeedsDrive => _definition.TakesDrive;
    public string? RestartText => _definition.Restart == RestartRequirement.None ? null : _definition.Restart.ToGerman();

    [ObservableProperty] private string _lastRun = "Noch nicht in dieser Sitzung ausgeführt.";
    [ObservableProperty] private string _lastOutput = "";
    [ObservableProperty] private bool _hasOutput;

    public void RefreshLastRun()
    {
        var report = _action.GetLastRun(Parameters());
        if (report is null) return;
        LastRun = $"{report.FinishedAt.ToLocalTime():dd.MM.yyyy HH:mm}: " + (report.TimedOut ? "Zeitüberschreitung" : report.Success ? "erfolgreich" : $"fehlgeschlagen (Rückgabecode {report.ExitCode})") + $" · Dauer {report.Duration:hh\\:mm\\:ss}";
        LastOutput = report.Output.Length > 6000 ? report.Output[^6000..] : report.Output;
        HasOutput = LastOutput.Length > 0;
    }

    private ActionParameters Parameters() => NeedsDrive && Drive is not null ? ActionParameters.Of(("drive", Drive)) : ActionParameters.Empty;

    [RelayCommand]
    private async Task RunAsync()
    {
        await _coordinator.RunAsync(new[] { new PlanRequest(_definition.Id, Parameters()) }, ExecutionSource.User, _definition.Name).ConfigureAwait(true);
        RefreshLastRun();
    }
}

/// <summary>Windows-Reparatur: SFC, DISM, CHKDSK-Prüfung und Wiederherstellungspunkte. Die Werkzeuge laufen mit ihren echten Ausgaben; nichts wird simuliert.</summary>
public sealed partial class RepairViewModel : PageViewModel
{
    private static readonly string[] ToolIds =
    {
        SystemToolCatalog.DismCheckHealth, SystemToolCatalog.DismScanHealth, SystemToolCatalog.SfcScan, SystemToolCatalog.DismRestoreHealth, SystemToolCatalog.ChkdskScan, SystemToolCatalog.DismComponentCleanup,
    };

    private readonly ActionCoordinator _coordinator;
    private readonly IHardwareInfoProvider _hardware;
    private readonly ILogger<RepairViewModel> _logger;

    public RepairViewModel(ActionCatalog catalog, ActionCoordinator coordinator, IHardwareInfoProvider hardware, ILogger<RepairViewModel> logger)
    {
        _coordinator = coordinator;
        _hardware = hardware;
        _logger = logger;
        _catalog = catalog;
    }

    private readonly ActionCatalog _catalog;

    public override string Key => "repair";
    public override string Title => "Windows-Reparatur";
    public override string Subtitle => "Integrierte Windows-Werkzeuge in sinnvoller Reihenfolge: erst prüfen, dann nur bei Befund reparieren.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<RepairToolViewModel> Tools { get; } = new();
    public ObservableCollection<string> Notices { get; } = new();
    [ObservableProperty] private string _restorePointText = "";

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        var info = await Task.Run(() => _hardware.GetSystemInfoAsync()).ConfigureAwait(true);
        var drive = info.SystemVolume?.Letter ?? "C:";
        Notices.Clear();
        if (info.PendingReboot) Notices.Add("Windows wartet auf einen Neustart. Starten Sie zuerst neu – Reparaturwerkzeuge liefern sonst unzuverlässige Ergebnisse.");
        if (info.IsVirtualMachine) Notices.Add("Virtuelle Maschine erkannt: Reparaturläufe sind möglich, Laufwerksprüfungen betreffen den virtuellen Datenträger.");

        if (Tools.Count == 0)
        {
            foreach (var id in ToolIds)
            {
                if (_catalog.Find(id) is not SystemToolAction action) continue;
                var definition = SystemToolCatalog.All.First(d => d.Id == id);
                Tools.Add(new RepairToolViewModel(definition, action, _coordinator, drive));
            }
        }
        foreach (var tool in Tools) tool.RefreshLastRun();
        RestorePointText = "Ein Wiederherstellungspunkt sichert Systemdateien und Registrierung (nicht Ihre persönlichen Dateien). Windows legt höchstens einen Punkt pro 24 Stunden an.";
    }

    [RelayCommand]
    private Task CreateRestorePointAsync()
        => _coordinator.RunAsync(new[] { new PlanRequest(RestorePointAction.ActionId, ActionParameters.Of(("description", "NEXUS Optimizer AI – manuell"))) }, ExecutionSource.User, "Wiederherstellungspunkt erstellen");

    [RelayCommand]
    private void OpenRecoverySettings() => PageHelpers.OpenUri("ms-settings:recovery");
}

// ───────────────────────── Diagnose ─────────────────────────

public sealed record FindingViewModel(DiagnosticFinding Finding)
{
    public string Area => Finding.Area;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public FindingSeverity Severity => Finding.Severity;
    public string SeverityText => Finding.Severity switch { FindingSeverity.Ok => "In Ordnung", FindingSeverity.Info => "Hinweis", FindingSeverity.Warning => "Warnung", FindingSeverity.Critical => "Kritisch", _ => "Nicht verfügbar" };
    public bool HasPage => Finding.PageKey is not null;
}

public sealed record LogEntryRow(string Time, string Source, string Level, string Message);

/// <summary>Systemdiagnose: Befunde aus Hardware- und Systemzustand, Ereignisprotokoll, Gerätestatus und Abstürzen – jeweils mit nachvollziehbarer Herkunft.</summary>
public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private readonly SystemSnapshotService _snapshots;
    private readonly ISystemDiagnostics _diagnostics;
    private readonly INavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly ILogger<DiagnosticsViewModel> _logger;
    private List<FindingDiagnostic> _lines = new();

    private sealed record FindingDiagnostic(string Text);

    public DiagnosticsViewModel(SystemSnapshotService snapshots, ISystemDiagnostics diagnostics, INavigationService navigation, DialogService dialogs, ILogger<DiagnosticsViewModel> logger)
    {
        _snapshots = snapshots;
        _diagnostics = diagnostics;
        _navigation = navigation;
        _dialogs = dialogs;
        _logger = logger;
    }

    public override string Key => "diagnostics";
    public override string Title => "Systemdiagnose";
    public override string Subtitle => "Befunde aus gemessenen und gelesenen Werten. Wo Windows keine Auskunft gibt, steht „nicht verfügbar“ – es wird nichts vermutet.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<FindingViewModel> Findings { get; } = new();
    public ObservableCollection<LogEntryRow> Events { get; } = new();
    public ObservableCollection<InfoRow> Crashes { get; } = new();
    public ObservableCollection<InfoRow> Devices { get; } = new();
    [ObservableProperty] private string _reliability = "";
    [ObservableProperty] private string _eventsNote = "";
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private string _summary = "Die Diagnose liest nur Daten und verändert nichts.";

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!HasRun) await RunAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        IsLoading = true;
        try
        {
            Summary = "Diagnose läuft …";
            var snapshot = await Task.Run(() => _snapshots.CaptureAsync()).ConfigureAwait(true);
            Findings.Clear();
            foreach (var finding in BuildFindings(snapshot).OrderByDescending(f => f.Severity == FindingSeverity.Unavailable ? 0 : (int)f.Severity)) Findings.Add(new FindingViewModel(finding));

            var window = TimeSpan.FromDays(7);
            var eventsTask = Safe(() => _diagnostics.GetRecentErrorsAsync(window, 40), Array.Empty<EventLogEntryInfo>());
            var devicesTask = Safe(() => _diagnostics.GetDeviceProblemsAsync(), Array.Empty<DeviceProblem>());
            var crashTask = Safe(() => _diagnostics.GetApplicationCrashesAsync(window), Array.Empty<CrashInfo>());
            var reliabilityTask = Safe(() => _diagnostics.GetReliabilityAsync(), new ReliabilityInfo(null, null, "Nicht verfügbar."));
            await Task.WhenAll(eventsTask, devicesTask, crashTask, reliabilityTask).ConfigureAwait(true);

            Events.Clear();
            foreach (var entry in eventsTask.Result) Events.Add(new LogEntryRow(entry.Time.ToString("dd.MM. HH:mm"), $"{entry.Log}/{entry.Source} ({entry.EventId})", entry.Level, entry.Message));
            EventsNote = eventsTask.Result.Count == 0 ? "In den letzten 7 Tagen wurden keine Fehler im System- oder Anwendungsprotokoll gefunden – oder das Protokoll ist nicht lesbar." : $"{eventsTask.Result.Count} Fehler in den letzten 7 Tagen (neueste zuerst, höchstens 40).";

            Devices.Clear();
            foreach (var device in devicesTask.Result) Devices.Add(new InfoRow(device.Name, $"Fehlercode {device.ErrorCode}: {device.Explanation}"));
            if (devicesTask.Result.Count == 0) Devices.Add(new InfoRow("Geräte", "Keine Geräte mit gemeldetem Problem"));

            Crashes.Clear();
            foreach (var crash in crashTask.Result.Take(12)) Crashes.Add(new InfoRow($"{crash.Time:dd.MM. HH:mm} · {crash.Application}", (crash.IsHang ? "Hänger" : "Absturz") + (crash.FaultingModule is { } module ? " in " + module : "") + (crash.ExceptionCode is { } code ? " (" + code + ")" : "")));
            if (crashTask.Result.Count == 0) Crashes.Add(new InfoRow("Anwendungen", "Keine Abstürze in den letzten 7 Tagen gemeldet"));

            var reliability = reliabilityTask.Result;
            Reliability = reliability.StabilityIndex is { } index ? $"Zuverlässigkeitsindex von Windows: {index:0.0} von 10" + (reliability.MeasuredAt is { } at ? $" (Stand {at:dd.MM.yyyy})" : "") + ". " + reliability.Note : reliability.Note;

            HasRun = true;
            var problems = Findings.Count(f => f.Severity is FindingSeverity.Warning or FindingSeverity.Critical);
            Summary = problems == 0 ? "Keine Warnungen. Alle gelesenen Werte liegen im unauffälligen Bereich." : $"{problems} Befund(e) mit Warnung oder kritischem Zustand.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnose fehlgeschlagen.");
            ErrorMessage = "Die Diagnose konnte nicht abgeschlossen werden: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static async Task<T> Safe<T>(Func<Task<T>> read, T fallback)
    {
        try { return await Task.Run(read).ConfigureAwait(false); }
        catch (Exception) { return fallback; }
    }

    private static IEnumerable<DiagnosticFinding> BuildFindings(SystemSnapshot s)
    {
        var info = s.Info;
        foreach (var volume in info.Volumes.Where(v => v.TotalBytes > 0))
        {
            var severity = volume.FreePercent < 5 ? FindingSeverity.Critical : volume.FreePercent < 12 ? FindingSeverity.Warning : FindingSeverity.Ok;
            yield return new DiagnosticFinding("Speicherplatz", $"Laufwerk {volume.Letter}: {volume.FreePercent:0.0} % frei", $"{ByteFormat.Format(volume.FreeBytes)} von {ByteFormat.Format(volume.TotalBytes)} frei.", severity, severity == FindingSeverity.Ok ? null : "cleanup");
        }
        foreach (var drive in info.Drives)
        {
            if (drive.Health is HealthState.Warning or HealthState.Unhealthy)
                yield return new DiagnosticFinding("Datenträger", $"{drive.Model}: Zustand „{(drive.Health == HealthState.Unhealthy ? "fehlerhaft" : "Warnung")}“", "Windows meldet für dieses Laufwerk einen Problemzustand. Sichern Sie Ihre Daten umgehend.", drive.Health == HealthState.Unhealthy ? FindingSeverity.Critical : FindingSeverity.Warning, "storage");
            else if (drive.WearPercent is >= 80)
                yield return new DiagnosticFinding("Datenträger", $"{drive.Model}: {drive.WearPercent} % der Lebensdauer verbraucht", "Planen Sie einen Austausch und sichern Sie Ihre Daten.", FindingSeverity.Warning, "storage");
        }
        if (s.Memory.Peak is >= 92) yield return new DiagnosticFinding("Arbeitsspeicher", $"Speicherbelegung erreichte {s.Memory.Peak:0} %", "Bei dauerhaft hoher Belegung lagert Windows aus und das System wird langsamer. Mehr RAM oder weniger Hintergrundprogramme helfen.", FindingSeverity.Warning, "ram");
        else if (s.Memory.Samples > 0) yield return new DiagnosticFinding("Arbeitsspeicher", $"Belegung im Mittel {s.Memory.Average:0} %", "Keine Auffälligkeit in den gemessenen Werten.", FindingSeverity.Ok, "ram");
        if (s.Cpu.Average is >= 85) yield return new DiagnosticFinding("Prozessor", $"Dauerhaft hohe Auslastung ({s.Cpu.Average:0} % im Mittel)", "Prüfen Sie, welche Prozesse die Last verursachen.", FindingSeverity.Warning, "processes");
        if (s.ThermalLimitObserved) yield return new DiagnosticFinding("Temperatur", "Prozessorleistung wird begrenzt", "Die Leistung wurde thermisch oder durch Firmware/Energieplan reduziert. Prüfen Sie Kühlung, Luftwege und Staub.", FindingSeverity.Warning, "cpu");
        if (s.Temperature.Samples == 0) yield return new DiagnosticFinding("Temperatur", "Keine Temperatursensoren lesbar", "Windows stellt auf diesem System keine Thermalzone bereit. Es werden keine Temperaturen geschätzt.", FindingSeverity.Unavailable);
        if (info.PendingReboot) yield return new DiagnosticFinding("Windows", "Neustart ausstehend", string.Join(", ", info.PendingRebootReasons), FindingSeverity.Warning);
        if (s.Uptime > TimeSpan.FromDays(14)) yield return new DiagnosticFinding("Windows", $"Seit {(int)s.Uptime.TotalDays} Tagen nicht neu gestartet", "Updates werden teils erst nach einem Neustart wirksam.", FindingSeverity.Info);
        if (info.SecureBootEnabled == false) yield return new DiagnosticFinding("Sicherheit", "Sicherer Start ist nicht aktiv", "Windows 11 setzt ihn voraus. Die Aktivierung erfolgt in der Firmware (UEFI) und wird hier nicht verändert.", FindingSeverity.Info);
        if (info.IsActivated == false) yield return new DiagnosticFinding("Windows", "Windows ist nicht aktiviert", "Dies hat keinen Einfluss auf diese Anwendung.", FindingSeverity.Info);
        if (info.Battery?.HealthPercent is < 60) yield return new DiagnosticFinding("Akku", $"Akkukapazität nur noch {info.Battery.HealthPercent:0} % des Neuzustands", "Die verringerte Laufzeit ist Verschleiß, kein Softwareproblem.", FindingSeverity.Warning, "power");
        foreach (var adapter in info.NetworkAdapters.Where(a => a.IsUp && a.SpeedBitsPerSecond is > 0 and < 100_000_000 && !a.IsWireless))
            yield return new DiagnosticFinding("Netzwerk", $"{adapter.Name}: nur {adapter.SpeedBitsPerSecond / 1_000_000} Mbit/s", "Ein Kabeladapter läuft mit weniger als 100 Mbit/s – prüfen Sie Kabel und Anschluss.", FindingSeverity.Warning, "network");
        if (s.StartupEntries.Count(e => e.Enabled) > 15) yield return new DiagnosticFinding("Autostart", $"{s.StartupEntries.Count(e => e.Enabled)} aktivierte Autostart-Einträge", "Viele Autostart-Programme verlängern Anmeldung und Speicherbedarf.", FindingSeverity.Info, "startup");
    }

    [RelayCommand]
    private void Open(FindingViewModel? finding)
    {
        if (finding?.Finding.PageKey is { } key) _navigation.Navigate(key);
    }

    [RelayCommand]
    private Task ExportAsync() => GuardAsync(async () =>
    {
        var path = _dialogs.SaveFile("Textbericht (*.txt)|*.txt", $"nexus-diagnose-{DateTime.Now:yyyyMMdd-HHmm}.txt", "Diagnosebericht speichern");
        if (path is null) return;
        var lines = new List<string> { "NEXUS Optimizer AI – Diagnosebericht " + DateTime.Now.ToString("dd.MM.yyyy HH:mm"), "" };
        lines.AddRange(Findings.Select(f => $"[{f.SeverityText}] {f.Area}: {f.Title} – {f.Detail}"));
        lines.Add(""); lines.Add("Ereignisprotokoll:");
        lines.AddRange(Events.Select(e => $"{e.Time} {e.Source} {e.Message}"));
        await File.WriteAllLinesAsync(path, lines, System.Text.Encoding.UTF8).ConfigureAwait(true);
    }, "Der Bericht konnte nicht gespeichert werden.");
}

// ───────────────────────── Benchmark ─────────────────────────

public sealed partial class BenchmarkOptionViewModel : ObservableObject
{
    public BenchmarkOptionViewModel(BenchmarkDescription description) => Description = description;
    public BenchmarkDescription Description { get; }
    public BenchmarkKind Kind => Description.Kind;
    public string Name => Description.Name;
    public string Methodology => Description.Methodology;
    public string Limitations => Description.Limitations;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _lastResult = "";
}

public sealed record BenchmarkHistoryRow(BenchmarkResult Result)
{
    public string Time => Result.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string Name => BenchmarkRunner.Describe(Result.Kind).Name;
    public string Label => Result.Label ?? "";
    public string Median => $"{Statistics.Median(Result.Samples.ToList()):0.##} {Result.Unit}";
    public string Spread => $"± {Statistics.CoefficientOfVariation(Result.Samples.ToList()) * 100:0.#} % ({Result.Samples.Count} Läufe)";
}

/// <summary>Benchmark-Center: wiederholbare Messungen mit Streuung und ehrlichem Vorher-/Nachher-Vergleich. Es gibt keinen Gesamt-Score.</summary>
public sealed partial class BenchmarkViewModel : PageViewModel
{
    private readonly BenchmarkRunner _runner;
    private readonly IBenchmarkStore _store;
    private readonly DialogService _dialogs;
    private readonly ILogger<BenchmarkViewModel> _logger;
    private CancellationTokenSource? _cancellation;

    public BenchmarkViewModel(BenchmarkRunner runner, IBenchmarkStore store, DialogService dialogs, ILogger<BenchmarkViewModel> logger)
    {
        _runner = runner;
        _store = store;
        _dialogs = dialogs;
        _logger = logger;
        foreach (var description in BenchmarkRunner.Descriptions) Options.Add(new BenchmarkOptionViewModel(description));
        Options.First(o => o.Kind is BenchmarkKind.DiskSequentialWrite or BenchmarkKind.DiskSequentialRead or BenchmarkKind.DiskRandomRead).IsSelected = false;
        foreach (var option in Options.Where(o => o.Kind is BenchmarkKind.DiskSequentialWrite or BenchmarkKind.DiskSequentialRead or BenchmarkKind.DiskRandomRead)) option.IsSelected = false;
    }

    public override string Key => "benchmark";
    public override string Title => "Benchmark-Center";
    public override string Subtitle => "Messen statt behaupten: jeder Test läuft mehrfach, zeigt Median und Streuung und ist mit Messbedingungen dokumentiert.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<BenchmarkOptionViewModel> Options { get; } = new();
    public ObservableCollection<BenchmarkHistoryRow> History { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<BenchmarkHistoryRow> CompareRows { get; } = new();
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "Bereit. Schließen Sie vor dem Test andere Programme und lassen Sie das Gerät am Netzteil.";
    [ObservableProperty] private string _comparison = "Wählen Sie zwei Einträge im Verlauf, um sie zu vergleichen.";
    [ObservableProperty] private BenchmarkHistoryRow? _baselineRow;
    [ObservableProperty] private BenchmarkHistoryRow? _candidateRow;
    [ObservableProperty] private int _runs = 5;

    partial void OnBaselineRowChanged(BenchmarkHistoryRow? value) => Compare();
    partial void OnCandidateRowChanged(BenchmarkHistoryRow? value) => Compare();

    protected override async Task OnNavigatedToAsync(object? parameter) => await ReloadAsync().ConfigureAwait(true);

    private async Task ReloadAsync()
    {
        var results = await Task.Run(() => _store.GetHistory(null, 200)).ConfigureAwait(true);
        History.Clear();
        foreach (var result in results.OrderByDescending(r => r.Timestamp)) History.Add(new BenchmarkHistoryRow(result));
    }

    private void Compare()
    {
        if (BaselineRow is null || CandidateRow is null) return;
        if (BaselineRow.Result.Kind != CandidateRow.Result.Kind)
        {
            Comparison = "Nur Messungen derselben Testart lassen sich vergleichen.";
            return;
        }
        var result = MeasurementComparer.Compare(BaselineRow.Result.Samples.ToList(), CandidateRow.Result.Samples.ToList(), BaselineRow.Result.HigherIsBetter);
        Comparison = result.Explanation;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        var selected = Options.Where(o => o.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Status = "Bitte mindestens einen Test auswählen.";
            return;
        }
        if (selected.Any(o => o.Kind is BenchmarkKind.DiskSequentialWrite) &&
            !await _dialogs.ConfirmAsync("Schreibtest auf dem Datenträger", "Der Test schreibt je Lauf eine temporäre Datei von 256 MB und löscht sie danach. Auf SSDs verursacht das minimale Abnutzung.", "Test starten").ConfigureAwait(true)) return;

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        Log.Clear();
        try
        {
            var options = new BenchmarkOptions { Runs = Math.Clamp(Runs, 3, 15) };
            var progress = new Progress<string>(text => { Status = text; Log.Add(text); });
            foreach (var option in selected)
            {
                var result = await _runner.RunAsync(option.Kind, options, progress, _cancellation.Token).ConfigureAwait(true);
                var labelled = result with { Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(), Conditions = $"{Environment.ProcessorCount} logische Prozessoren, Energie: {(System.Windows.SystemParameters.PowerLineStatus)}" };
                await Task.Run(() => _store.Save(labelled)).ConfigureAwait(true);
                option.LastResult = $"{Statistics.Median(labelled.Samples.ToList()):0.##} {labelled.Unit} (Median aus {labelled.Samples.Count} Läufen, Streuung {Statistics.CoefficientOfVariation(labelled.Samples.ToList()) * 100:0.#} %)";
            }
            Status = "Fertig. Die Ergebnisse stehen im Verlauf.";
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { Status = "Abgebrochen."; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark fehlgeschlagen.");
            ErrorMessage = "Der Test ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    protected override void OnNavigatedFrom() { }
}

// ───────────────────────── Gaming ─────────────────────────

public sealed partial class GameRowViewModel : ObservableObject
{
    private readonly Action<GameRowViewModel> _save;
    private bool _loading = true;

    public GameRowViewModel(GameInfo game, GameProfile profile, Action<GameRowViewModel> save)
    {
        Game = game;
        _save = save;
        _autoActivate = profile.AutoActivate;
        _onlyOnAc = profile.OnlyOnAcPower;
        _usePowerPlan = !string.IsNullOrEmpty(profile.PowerPlan);
        _restoreOnExit = profile.RestoreOnExit;
        _recordSession = profile.RecordSession;
        _priority = profile.ProcessPriority ?? "";
        _gpuPreference = profile.GpuPreference ?? "";
        Profile = profile;
        _loading = false;
    }

    public GameInfo Game { get; }
    public GameProfile Profile { get; private set; }
    public string Name => Game.Name;
    public string Platform => Game.PlatformName;
    public string Executables => Game.Executables.Count > 0 ? string.Join(", ", Game.Executables) : "Keine Programmdatei erkannt – Profil kann nicht automatisch aktiviert werden.";
    public bool HasExecutables => Game.Executables.Count > 0;

    [ObservableProperty] private bool _autoActivate;
    [ObservableProperty] private bool _onlyOnAc;
    [ObservableProperty] private bool _usePowerPlan;
    [ObservableProperty] private bool _restoreOnExit;
    [ObservableProperty] private bool _recordSession;
    [ObservableProperty] private string _priority;
    [ObservableProperty] private string _gpuPreference;

    partial void OnAutoActivateChanged(bool value) => Changed();
    partial void OnOnlyOnAcChanged(bool value) => Changed();
    partial void OnUsePowerPlanChanged(bool value) => Changed();
    partial void OnRestoreOnExitChanged(bool value) => Changed();
    partial void OnRecordSessionChanged(bool value) => Changed();
    partial void OnPriorityChanged(string value) => Changed();
    partial void OnGpuPreferenceChanged(string value) => Changed();

    private void Changed()
    {
        if (_loading) return;
        Profile = ToProfile();
        _save(this);
    }

    public GameProfile ToProfile() => new()
    {
        GameId = Game.Id,
        GameName = Game.Name,
        ProcessNames = Game.Executables.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList(),
        ExecutablePath = Game.InstallDirectory,
        AutoActivate = AutoActivate && Game.Executables.Count > 0,
        OnlyOnAcPower = OnlyOnAc,
        PowerPlan = UsePowerPlan ? "high-performance" : null,
        ProcessPriority = string.IsNullOrEmpty(Priority) ? null : Priority,
        GpuPreference = string.IsNullOrEmpty(GpuPreference) ? null : GpuPreference,
        RestoreOnExit = RestoreOnExit,
        RecordSession = RecordSession,
    };
}

public sealed record SessionRow(GameSessionRecord Record)
{
    public string Title => $"{Record.GameName} · {Record.StartedAt.ToLocalTime():dd.MM.yyyy HH:mm}";
    public string Detail => $"Dauer {Record.Duration:hh\\:mm\\:ss} · CPU Ø {Fmt(Record.AverageCpuPercent)} (Spitze {Fmt(Record.PeakCpuPercent)}) · GPU Ø {Fmt(Record.AverageGpuPercent)} · RAM Ø {Fmt(Record.AverageMemoryPercent)}" +
                            (Record.PeakTemperatureCelsius is { } t ? $" · Temperatur max. {t:0} °C" : "");
    public string Note => (Record.ProfileApplied ? "Profil angewendet. " : "") + (Record.ProfileNote ?? "") + (Record.FrameNote is { } f ? " " + f : "");
    private static string Fmt(double? v) => v is { } x ? x.ToString("0") + " %" : "n. v.";
}

/// <summary>Gaming-Boost: erkennt installierte Spiele, verwaltet Spielprofile und zeichnet Sitzungen auf – ohne Leistungsversprechen.</summary>
public sealed partial class GamingViewModel : PageViewModel
{
    private readonly IGameLibraryScanner _scanner;
    private readonly IGameStore _store;
    private readonly GamingSessionManager _manager;
    private readonly GameProfileApplier _applier;
    private readonly IFrameCaptureProvider _frames;
    private readonly ActionCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<GamingViewModel> _logger;
    private bool _loadingSettings;

    public GamingViewModel(IGameLibraryScanner scanner, IGameStore store, GamingSessionManager manager, GameProfileApplier applier, IFrameCaptureProvider frames, ActionCoordinator coordinator,
        SettingsService settings, DialogService dialogs, INavigationService navigation, ILogger<GamingViewModel> logger)
    {
        _scanner = scanner;
        _store = store;
        _manager = manager;
        _applier = applier;
        _frames = frames;
        _coordinator = coordinator;
        _settings = settings;
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;
        Priorities = new[] { new Choice<string>("", "Unverändert"), new Choice<string>("AboveNormal", "Höher als normal"), new Choice<string>("High", "Hoch") };
        GpuPreferences = new[] { new Choice<string>("", "Unverändert"), new Choice<string>("high-performance", "Hohe Leistung (dedizierte GPU)") };
        _manager.SessionStarted += (_, _) => Ui.Post(UpdateCurrent);
        _manager.SessionEnded += (_, _) => Ui.Post(() => { UpdateCurrent(); _ = LoadSessionsAsync(); });
    }

    public override string Key => "gaming";
    public override string Title => "Gaming-Boost";
    public override string Subtitle => "Spielprofile mit Wiederherstellung beim Beenden. Ob sich Bildraten ändern, hängt vom System ab – zugesagt wird nichts, gemessen wird alles.";
    protected override ILogger Logger => _logger;

    public ObservableCollection<GameRowViewModel> Games { get; } = new();
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    public IReadOnlyList<Choice<string>> Priorities { get; }
    public IReadOnlyList<Choice<string>> GpuPreferences { get; }
    [ObservableProperty] private bool _autoMode;
    [ObservableProperty] private string _currentText = "Kein Spiel erkannt.";
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private string _frameStatus = "";
    [ObservableProperty] private bool _isScanning;

    partial void OnAutoModeChanged(bool value)
    {
        if (_loadingSettings) return;
        _ = ChangeAutoAsync(value);
    }

    private async Task ChangeAutoAsync(bool enable)
    {
        if (enable)
        {
            var ok = await _dialogs.ConfirmAsync("Automatischen Gaming-Modus einschalten",
                "Sobald ein Spiel mit aktiviertem Profil startet, werden dessen Einstellungen ohne Rückfrage angewendet – ausschließlich risikoarme, umkehrbare Änderungen. Beim Beenden des Spiels wird der vorherige Zustand wiederhergestellt (sofern im Profil gewählt). Jede Änderung steht im Verlauf.",
                "Einschalten").ConfigureAwait(true);
            if (!ok) { Load(); return; }
        }
        _settings.Update(s => s with { AutoGamingMode = enable });
        Load();
    }

    private void Load()
    {
        _loadingSettings = true;
        AutoMode = _settings.Current.AutoGamingMode;
        _loadingSettings = false;
        FrameStatus = _frames.IsAvailable ? "Frametime-Aufzeichnung über PresentMon ist verfügbar." : "FPS und Frametimes: nicht verfügbar. " + _frames.UnavailableReason;
    }

    private void UpdateCurrent()
        => CurrentText = _manager.Current is { } c ? $"Läuft: {c.Profile.GameName} (seit {c.StartedAt.ToLocalTime():HH:mm}). {c.Note}" : "Kein Spiel erkannt.";

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        Load();
        UpdateCurrent();
        await LoadGamesAsync(rescan: false).ConfigureAwait(true);
        await LoadSessionsAsync().ConfigureAwait(true);
    }

    private async Task LoadGamesAsync(bool rescan)
    {
        IsScanning = true;
        IsLoading = true;
        try
        {
            var games = await Task.Run(async () =>
            {
                var stored = _store.GetGames();
                if (rescan || stored.Count == 0)
                {
                    stored = await _scanner.ScanAsync().ConfigureAwait(false);
                    _store.SaveGames(stored);
                }
                return stored;
            }).ConfigureAwait(true);
            var profiles = _store.GetProfiles().ToDictionary(p => p.GameId);
            Games.Clear();
            foreach (var game in games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
                Games.Add(new GameRowViewModel(game, profiles.TryGetValue(game.Id, out var profile) ? profile : new GameProfile { GameId = game.Id, GameName = game.Name }, SaveProfile));
            ScanStatus = Games.Count == 0
                ? "Es wurden keine Spiele gefunden. Unterstützt werden Steam, Epic Games und Xbox/Microsoft Store; andere Launcher werden nicht erkannt."
                : $"{Games.Count} Spiele gefunden (Steam, Epic Games, Xbox).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spielesuche fehlgeschlagen.");
            ScanStatus = "Die Spielesuche ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            IsLoading = false;
        }
    }

    private void SaveProfile(GameRowViewModel row)
    {
        try { _store.SaveProfile(row.Profile); }
        catch (Exception ex) { _logger.LogWarning(ex, "Spielprofil konnte nicht gespeichert werden."); }
    }

    private async Task LoadSessionsAsync()
    {
        var sessions = await Task.Run(() => _store.GetSessions(null, 30)).ConfigureAwait(true);
        Sessions.Clear();
        foreach (var session in sessions.OrderByDescending(s => s.StartedAt)) Sessions.Add(new SessionRow(session));
    }

    [RelayCommand]
    private Task RescanAsync() => LoadGamesAsync(rescan: true);

    [RelayCommand]
    private async Task PreviewProfileAsync(GameRowViewModel? row)
    {
        if (row is null) return;
        var profile = row.ToProfile();
        var requests = _applier.BuildRequests(profile, null, out var notes);
        if (requests.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Keine Änderung", "Dieses Profil enthält keine Änderungen, die ohne laufendes Spiel angewendet werden können.", string.Join(Environment.NewLine, notes)).ConfigureAwait(true);
            return;
        }
        await _coordinator.RunAsync(requests, ExecutionSource.User, "Spielprofil: " + row.Name, "Gaming-Profil", notes).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Open(string? pageKey)
    {
        if (!string.IsNullOrEmpty(pageKey)) _navigation.Navigate(pageKey);
    }
}
