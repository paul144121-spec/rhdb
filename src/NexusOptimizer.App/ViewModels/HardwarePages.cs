using System.Collections.ObjectModel;
using System.IO;
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
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Tools;

namespace NexusOptimizer.App.ViewModels;

/// <summary>Gemeinsame Abhängigkeiten der Hardware-Seiten.</summary>
public sealed record HardwareServices(MonitoringService Monitoring, IHardwareInfoProvider Hardware, ActionCatalog Catalog, ActionCoordinator Coordinator, ITransactionJournal Journal,
    SystemSnapshotService Snapshots, IProcessMonitor Processes, INavigationService Navigation, DialogService Dialogs);

/// <summary>Basis der Hardware-Seiten: lädt die Hardwareerkennung, hält Tweak-Karten aktuell und reagiert auf Systemänderungen.</summary>
public abstract partial class HardwarePageViewModel : LivePageViewModel
{
    private bool _subscribed;

    protected HardwarePageViewModel(HardwareServices services) : base(services.Monitoring) => Services = services;

    protected HardwareServices Services { get; }
    protected SystemInfo? Info { get; private set; }
    public ObservableCollection<InfoSection> Sections { get; } = new();
    public ObservableCollection<TweakItemViewModel> Tweaks { get; } = new();
    public bool HasTweaks => Tweaks.Count > 0;

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        await base.OnNavigatedToAsync(parameter).ConfigureAwait(true);
        if (!_subscribed)
        {
            _subscribed = true;
            Services.Coordinator.SystemChanged += (_, _) => Ui.Post(() => { if (IsActive) _ = RefreshStateAsync(); });
        }

        if (Info is null)
        {
            IsLoading = true;
            Info = await Task.Run(() => Services.Hardware.GetSystemInfoAsync()).ConfigureAwait(true);
        }
        Sections.Clear();
        foreach (var section in BuildSections(Info)) Sections.Add(section);
        await RefreshStateAsync().ConfigureAwait(true);
    }

    protected abstract IEnumerable<InfoSection> BuildSections(SystemInfo info);

    /// <summary>Liest änderbare Zustände neu (nach dem Laden und nach jeder Systemänderung).</summary>
    protected virtual async Task RefreshStateAsync()
    {
        if (Tweaks.Count == 0) return;
        var context = await Services.Snapshots.GetContextAsync().ConfigureAwait(true);
        await PageHelpers.RefreshTweaksAsync(Tweaks, context).ConfigureAwait(true);
    }

    /// <summary>Fügt parameterlose, nicht experimentelle Aktionen als Tweak-Karten hinzu.</summary>
    protected void AddTweaks(Func<ActionMetadata, bool> filter)
    {
        foreach (var action in Services.Catalog.All.Where(a => a.Metadata.Parameters.Count == 0 && !a.Metadata.IsExperimental && !a.Metadata.IsDiagnosticOnly && filter(a.Metadata)).OrderBy(a => a.Metadata.Risk).ThenBy(a => a.Metadata.Name))
            Tweaks.Add(new TweakItemViewModel(action, Services.Coordinator, Services.Journal));
        OnPropertyChanged(nameof(HasTweaks));
    }

    [RelayCommand]
    private async Task RescanHardwareAsync()
    {
        IsLoading = true;
        try
        {
            Info = await Task.Run(() => Services.Hardware.GetSystemInfoAsync(true)).ConfigureAwait(true);
            Sections.Clear();
            foreach (var section in BuildSections(Info)) Sections.Add(section);
            await RefreshStateAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Die Hardwareerkennung ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Open(string? pageKey)
    {
        if (!string.IsNullOrEmpty(pageKey)) Services.Navigation.Navigate(pageKey);
    }

    protected static string Yes(bool? value) => value switch { true => "Ja", false => "Nein", _ => "Nicht verfügbar" };
}

public sealed partial class CoreLoadViewModel : ObservableObject
{
    public CoreLoadViewModel(int index) => Label = "Kern " + index;
    public string Label { get; }
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _text = "–";
}

/// <summary>Eine Energieeinstellung des aktiven Plans mit getrennten Werten für Netz- und Akkubetrieb.</summary>
public sealed partial class PowerSettingItemViewModel : ObservableObject
{
    private readonly PowerSettingDefinition _definition;
    private readonly IPowerPolicy _power;
    private readonly ActionCoordinator _coordinator;

    public PowerSettingItemViewModel(PowerSettingDefinition definition, IPowerPolicy power, ActionCoordinator coordinator)
    {
        _definition = definition;
        _power = power;
        _coordinator = coordinator;
        Choices = definition.Choices?.OrderBy(c => c.Key).Select(c => new Choice<uint>(c.Key, c.Value)).ToList();
    }

    public string Name => _definition.Name;
    public string Description => _definition.Description;
    public string Warning => _definition.Warning;
    public bool HasWarning => _definition.Warning.Length > 0;
    public RiskLevel Risk => _definition.Risk;
    public IReadOnlyList<Choice<uint>>? Choices { get; }
    public bool HasChoices => Choices is not null;
    public bool IsNumeric => Choices is null;
    private bool InMinutes => _definition.Unit == PowerSettingUnit.Seconds;
    public string UnitText => _definition.Unit switch { PowerSettingUnit.Percent => "%", PowerSettingUnit.Seconds => "Min. (0 = nie)", _ => "" };
    public string RangeText => InMinutes ? $"Zulässig: {_definition.Min / 60}–{_definition.Max / 60} Min." : $"Zulässig: {_definition.Min}–{_definition.Max}";

    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _currentText = "Wird gelesen …";
    [ObservableProperty] private string _acInput = "";
    [ObservableProperty] private string _dcInput = "";
    [ObservableProperty] private Choice<uint>? _selectedAc;
    [ObservableProperty] private Choice<uint>? _selectedDc;
    [ObservableProperty] private string? _inputError;

    public void Refresh()
    {
        try
        {
            var plan = _power.GetActivePlan();
            var ac = _power.ReadAcValue(plan, _definition.Subgroup, _definition.Setting);
            var dc = _power.ReadDcValue(plan, _definition.Subgroup, _definition.Setting);
            IsAvailable = ac is not null || dc is not null;
            if (!IsAvailable)
            {
                CurrentText = "Auf diesem System nicht verfügbar (die Einstellung existiert im aktiven Energieplan nicht).";
                return;
            }
            CurrentText = $"Netzbetrieb: {_definition.Format(ac)} · Akkubetrieb: {_definition.Format(dc)}";
            if (Choices is not null)
            {
                SelectedAc = Choices.FirstOrDefault(c => c.Value == ac);
                SelectedDc = Choices.FirstOrDefault(c => c.Value == dc);
            }
            else
            {
                AcInput = ac is { } a ? (InMinutes ? a / 60 : a).ToString() : "";
                DcInput = dc is { } d ? (InMinutes ? d / 60 : d).ToString() : "";
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            CurrentText = "Nicht lesbar: " + ex.Message;
        }
    }

    private bool TryParse(string text, out uint? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!uint.TryParse(text.Trim(), out var parsed)) return false;
        var raw = InMinutes ? parsed * 60 : parsed;
        if (raw < _definition.Min || raw > _definition.Max) return false;
        value = raw;
        return true;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        InputError = null;
        uint? ac, dc;
        if (Choices is not null)
        {
            ac = SelectedAc?.Value;
            dc = SelectedDc?.Value;
        }
        else if (!TryParse(AcInput, out ac) || !TryParse(DcInput, out dc))
        {
            InputError = "Bitte ganze Zahlen im zulässigen Bereich eingeben. " + RangeText;
            return;
        }

        var values = new List<(string, string)> { ("setting", _definition.Alias) };
        if (ac is { } acValue) values.Add(("ac", acValue.ToString()));
        if (dc is { } dcValue) values.Add(("dc", dcValue.ToString()));
        if (values.Count == 1)
        {
            InputError = "Bitte mindestens einen Wert angeben.";
            return;
        }
        await _coordinator.RunAsync(new[] { new PlanRequest(PowerSettingAction.ActionId, ActionParameters.Of(values.ToArray())) }, ExecutionSource.User, Name).ConfigureAwait(true);
        Refresh();
    }
}

/// <summary>Prozessor: Kennwerte, Auslastung je Kern, Takt, Leistungsgrenze und prozessorbezogene Energieeinstellungen.</summary>
public sealed partial class CpuViewModel : HardwarePageViewModel
{
    private readonly IPowerPolicy _power;
    private readonly MetricTileViewModel _usage = new("Auslastung gesamt", "Brush.Accent");
    private readonly MetricTileViewModel _frequency = new("Aktueller Takt", "Brush.Cyan", double.NaN);
    private readonly MetricTileViewModel _limit = new("Leistungsgrenze", "Brush.Amber");
    private readonly MetricTileViewModel _temperature = new("Temperatur", "Brush.Amber", 110);
    private DateTimeOffset _lastProcessRefresh = DateTimeOffset.MinValue;
    private bool _processRefreshRunning;

    public CpuViewModel(HardwareServices services, IPowerPolicy power) : base(services)
    {
        _power = power;
        Tiles = new[] { _usage, _frequency, _limit, _temperature };
        foreach (var alias in new[] { PowerSettingCatalog.ProcessorMin, PowerSettingCatalog.ProcessorMax, PowerSettingCatalog.BoostMode, PowerSettingCatalog.CoreParkingMin, PowerSettingCatalog.EnergyPreference })
            if (PowerSettingCatalog.Find(alias) is { } definition) PowerSettings.Add(new PowerSettingItemViewModel(definition, power, services.Coordinator));
        AddTweaks(m => m.Category == ActionCategory.Cpu);
    }

    public override string Key => "cpu";
    public override string Title => "Prozessor";
    public override string Subtitle => "Kennwerte, Auslastung je Kern und prozessorbezogene Energieeinstellungen des aktiven Plans.";

    public IReadOnlyList<MetricTileViewModel> Tiles { get; }
    public ObservableCollection<CoreLoadViewModel> Cores { get; } = new();
    public ObservableCollection<InfoRow> LiveRows { get; } = new();
    public ObservableCollection<InfoRow> TopProcesses { get; } = new();
    public ObservableCollection<PowerSettingItemViewModel> PowerSettings { get; } = new();
    [ObservableProperty] private bool _isThrottled;
    [ObservableProperty] private string _activePlan = "";

    protected override IEnumerable<InfoSection> BuildSections(SystemInfo info)
    {
        yield return new InfoSection("Prozessor", new[]
        {
            InfoRow.Of("Modell", info.CpuName),
            InfoRow.Of("Physische Kerne", info.CpuCores?.ToString()),
            new InfoRow("Logische Prozessoren", info.CpuLogicalProcessors.ToString()),
            InfoRow.Of("Nenntakt", info.CpuMaxClockMhz is { } mhz ? $"{mhz / 1000.0:0.00} GHz" : null),
            InfoRow.Of("Virtualisierung in der Firmware aktiviert", info.VirtualizationEnabledInFirmware),
            InfoRow.Of("Hypervisor aktiv", info.HypervisorPresent),
            new InfoRow("Virtuelle Maschine", info.IsVirtualMachine ? "Ja" : "Nein"),
        });
        yield return new InfoSection("Hauptplatine und Firmware", new[]
        {
            InfoRow.Of("Hersteller", info.MotherboardManufacturer),
            InfoRow.Of("Modell", info.MotherboardModel),
            InfoRow.Of("BIOS/UEFI-Version", info.BiosVersion),
            InfoRow.Of("BIOS/UEFI-Datum", info.BiosDate?.ToString("dd.MM.yyyy")),
        });
    }

    protected override async Task RefreshStateAsync()
    {
        await base.RefreshStateAsync().ConfigureAwait(true);
        foreach (var setting in PowerSettings) setting.Refresh();
        try { ActivePlan = "Aktiver Energieplan: " + (_power.GetPlans().FirstOrDefault(p => p.IsActive)?.Name ?? "unbekannt"); }
        catch (Exception) { ActivePlan = "Der aktive Energieplan konnte nicht gelesen werden."; }
    }

    protected override void OnMetrics(SystemMetrics m)
    {
        Fill(_usage, MetricId.CpuTotal, m, $"{m.ProcessCount?.ToString() ?? "–"} Prozesse · {m.ThreadCount?.ToString() ?? "–"} Threads");
        Fill(_frequency, MetricId.CpuFrequency, m, "Über alle Kerne gemittelt");
        Fill(_limit, MetricId.CpuPerformanceLimit, m, "100 % = keine Begrenzung durch Firmware oder Energieplan");
        Fill(_temperature, MetricId.ThermalZone, m, m.CpuPackageCelsius is not null ? "CPU-Paket (Sensor)" : "ACPI-Thermalzone – nicht zwingend der CPU-Kern");
        IsThrottled = m.IsThermallyLimited;

        var cores = m.CpuPerCorePercent;
        while (Cores.Count < cores.Count) Cores.Add(new CoreLoadViewModel(Cores.Count));
        while (Cores.Count > cores.Count) Cores.RemoveAt(Cores.Count - 1);
        for (var i = 0; i < cores.Count; i++)
        {
            Cores[i].Percent = cores[i];
            Cores[i].Text = cores[i].ToString("0") + " %";
        }

        LiveRows.Clear();
        LiveRows.Add(InfoRow.Of("Prozessor-Warteschlange", m.ProcessorQueueLength?.ToString("0.#"), "Der Leistungsindikator ist auf diesem System nicht verfügbar."));
        LiveRows.Add(InfoRow.Of("DPC-Zeit", m.DpcTimePercent is { } dpc ? dpc.ToString("0.0") + " %" : null, "Der Leistungsindikator ist auf diesem System nicht verfügbar."));
        LiveRows.Add(InfoRow.Of("Interrupt-Zeit", m.InterruptTimePercent is { } irq ? irq.ToString("0.0") + " %" : null, "Der Leistungsindikator ist auf diesem System nicht verfügbar."));
        LiveRows.Add(InfoRow.Of("Handles", m.HandleCount?.ToString("N0")));

        if (!_processRefreshRunning && DateTimeOffset.Now - _lastProcessRefresh > TimeSpan.FromSeconds(3)) _ = RefreshProcessesAsync();
    }

    private async Task RefreshProcessesAsync()
    {
        _processRefreshRunning = true;
        try
        {
            var top = await Task.Run(() => Services.Processes.GetProcesses().Where(p => p.ProcessId > 4).OrderByDescending(p => p.CpuPercent).Take(8).ToList()).ConfigureAwait(true);
            TopProcesses.Clear();
            foreach (var process in top) TopProcesses.Add(new InfoRow(process.Name, $"{process.CpuPercent:0.0} % CPU · {ByteFormat.Format(process.WorkingSetBytes)} RAM · PID {process.ProcessId}"));
        }
        catch (Exception)
        {
            // Die Prozessliste ist eine Zusatzinformation; ein Fehler darf die Live-Anzeige nicht stören.
        }
        finally
        {
            _lastProcessRefresh = DateTimeOffset.Now;
            _processRefreshRunning = false;
        }
    }
}

/// <summary>Grafik: Adapter, Treiberstand, Auslastung, Grafikspeicher und GPU-Zuordnung je Anwendung.</summary>
public sealed partial class GpuViewModel : HardwarePageViewModel
{
    private readonly MetricTileViewModel _usage = new("GPU-Auslastung", "Brush.Cyan");
    private readonly MetricTileViewModel _memory = new("Grafikspeicher", "Brush.Blue");
    private readonly MetricTileViewModel _temperature = new("GPU-Temperatur", "Brush.Amber", 110);

    public GpuViewModel(HardwareServices services) : base(services)
    {
        Tiles = new[] { _usage, _memory, _temperature };
        Preferences = new[]
        {
            new Choice<string>("default", "Windows entscheidet (Standard)"),
            new Choice<string>("power-saving", "Energiesparend (meist integrierte Grafik)"),
            new Choice<string>("high-performance", "Hohe Leistung (meist dedizierte Grafikkarte)"),
        };
        _selectedPreference = Preferences[2];
        AddTweaks(m => m.Category is ActionCategory.Gpu or ActionCategory.Gaming);
    }

    public override string Key => "gpu";
    public override string Title => "Grafik";
    public override string Subtitle => "Grafikadapter, Treiberstand und Live-Werte. Übertaktung, Spannungs- oder Lüftersteuerung bietet diese Anwendung bewusst nicht an.";

    public IReadOnlyList<MetricTileViewModel> Tiles { get; }
    public ObservableCollection<InfoRow> LiveRows { get; } = new();
    public ObservableCollection<InfoRow> DriverLinks { get; } = new();
    public IReadOnlyList<Choice<string>> Preferences { get; }
    [ObservableProperty] private string _appPath = "";
    [ObservableProperty] private Choice<string> _selectedPreference;
    [ObservableProperty] private string? _preferenceError;

    protected override IEnumerable<InfoSection> BuildSections(SystemInfo info)
    {
        DriverLinks.Clear();
        if (info.Gpus.Count == 0)
        {
            yield return new InfoSection("Grafikadapter", new[] { new InfoRow("Erkennung", "Nicht verfügbar", "Windows hat keinen Grafikadapter gemeldet.") });
            yield break;
        }
        foreach (var gpu in info.Gpus)
        {
            if (gpu.DriverUrl is { } url && DriverLinks.All(l => l.Value != url)) DriverLinks.Add(new InfoRow("Offizielle Treiberseite: " + gpu.Vendor, url));
            yield return new InfoSection(gpu.Name, new[]
            {
                InfoRow.Of("Hersteller", gpu.Vendor),
                new InfoRow("Typ", gpu.IsIntegrated ? "Integrierte Grafik" : "Dedizierte Grafikkarte"),
                InfoRow.Of("Dedizierter Grafikspeicher", gpu.DedicatedMemoryBytes is > 0 ? ByteFormat.Format(gpu.DedicatedMemoryBytes.Value) : null),
                InfoRow.Of("Treiberversion", gpu.DriverVersion),
                InfoRow.Of("Treiberdatum", gpu.DriverDate?.ToString("dd.MM.yyyy")),
                InfoRow.Of("Auflösung", gpu.Resolution, "Dieser Adapter steuert derzeit keinen Bildschirm an."),
                InfoRow.Of("Bildwiederholrate", gpu.RefreshRateHz is { } hz ? hz + " Hz" : null, "Dieser Adapter steuert derzeit keinen Bildschirm an."),
            });
        }
    }

    protected override void OnMetrics(SystemMetrics m)
    {
        Fill(_usage, MetricId.GpuUsage, m, "Höchste Auslastung aller GPU-Engines");
        Fill(_memory, MetricId.GpuMemoryPercent, m, m.GpuDedicatedUsedBytes is { } used ? ByteFormat.Format(used) + " belegt" : "");
        if (!_memory.IsAvailable && m.GpuDedicatedUsedBytes is { } usedOnly)
        {
            // Belegung ist messbar, die Gesamtgröße aber unbekannt – dann wird nur der absolute Wert gezeigt.
            _memory.IsAvailable = true;
            _memory.Value = ByteFormat.Format(usedOnly);
            _memory.Detail = "Belegt (Gesamtgröße nicht ermittelbar)";
        }
        Fill(_temperature, MetricId.GpuTemperature, m, "Sensor der Grafikkarte");

        const string nvidiaOnly = "Nur für NVIDIA-Grafikkarten über die Treiberschnittstelle verfügbar.";
        LiveRows.Clear();
        LiveRows.Add(InfoRow.Of("Gemeinsam genutzter Speicher", m.GpuSharedUsedBytes is { } shared ? ByteFormat.Format(shared) : null, "Der Leistungsindikator ist auf diesem System nicht verfügbar."));
        LiveRows.Add(InfoRow.Of("Leistungsaufnahme", m.GpuPowerWatts is { } watts ? watts.ToString("0") + " W" : null, nvidiaOnly));
        LiveRows.Add(InfoRow.Of("GPU-Takt", m.GpuClockMhz is { } clock ? clock.ToString("0") + " MHz" : null, nvidiaOnly));
    }

    [RelayCommand]
    private void BrowseApp()
    {
        if (Services.Dialogs.OpenFile("Programme (*.exe)|*.exe", "Anwendung auswählen") is { } path) AppPath = path;
    }

    [RelayCommand]
    private async Task ApplyPreferenceAsync()
    {
        PreferenceError = null;
        var path = AppPath.Trim().Trim('"');
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            PreferenceError = "Bitte den vollständigen Pfad einer vorhandenen Programmdatei (.exe) angeben.";
            return;
        }
        await Services.Coordinator.RunAsync(new[] { new PlanRequest(GpuPreferenceAction.ActionId, ActionParameters.Of(("path", path), ("preference", SelectedPreference.Value))) },
            ExecutionSource.User, "GPU-Zuordnung für " + Path.GetFileName(path)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLink(string? url) => PageHelpers.OpenUri(url);
}

/// <summary>Arbeitsspeicher: Module, Belegung, zugesicherter Speicher und Auslagerungsdatei.</summary>
public sealed partial class RamViewModel : HardwarePageViewModel
{
    private readonly MetricTileViewModel _used = new("Belegung", "Brush.Blue");
    private readonly MetricTileViewModel _commit = new("Zugesicherter Speicher", "Brush.Accent");
    private readonly MetricTileViewModel _available = new("Verfügbar", "Brush.Green", double.NaN);
    private DateTimeOffset _lastProcessRefresh = DateTimeOffset.MinValue;
    private bool _processRefreshRunning;

    public RamViewModel(HardwareServices services) : base(services)
    {
        Tiles = new[] { _used, _commit, _available };
        AddTweaks(m => m.Category == ActionCategory.Memory);
    }

    public override string Key => "ram";
    public override string Title => "Arbeitsspeicher";
    public override string Subtitle => "Module, Belegung und Auslagerung. „Speicher freigeben“ auf Knopfdruck gibt es hier bewusst nicht – das verschiebt Daten nur in die Auslagerungsdatei und macht das System langsamer.";

    public IReadOnlyList<MetricTileViewModel> Tiles { get; }
    public ObservableCollection<InfoRow> LiveRows { get; } = new();
    public ObservableCollection<InfoRow> TopProcesses { get; } = new();

    protected override IEnumerable<InfoSection> BuildSections(SystemInfo info)
    {
        var rows = new List<InfoRow>
        {
            InfoRow.Of("Installiert", info.MemoryTotalBytes > 0 ? ByteFormat.Format(info.MemoryTotalBytes) : null),
            InfoRow.Of("Geschwindigkeit (langsamstes Modul)", info.MemorySpeedMhz is { } speed ? speed + " MHz" : null, "Die Modulgeschwindigkeit wird von der Firmware nicht gemeldet (typisch für virtuelle Maschinen)."),
            new InfoRow("Erkannte Module", info.MemoryModules.Count > 0 ? info.MemoryModules.Count.ToString() : "Nicht verfügbar", info.MemoryModules.Count > 0 ? null : "Die Firmware meldet keine Modulinformationen."),
        };
        yield return new InfoSection("Übersicht", rows);

        foreach (var module in info.MemoryModules)
        {
            yield return new InfoSection("Steckplatz " + module.Slot, new[]
            {
                new InfoRow("Kapazität", ByteFormat.Format(module.CapacityBytes)),
                InfoRow.Of("Geschwindigkeit", module.SpeedMhz is { } mhz ? mhz + " MHz" : null),
                InfoRow.Of("Hersteller", module.Manufacturer),
                InfoRow.Of("Teilenummer", module.PartNumber),
            });
        }

        if (info.PageFile is { } pageFile)
        {
            yield return new InfoSection("Auslagerungsdatei", new[]
            {
                new InfoRow("Verwaltung", pageFile.AutomaticallyManaged ? "Automatisch durch Windows (empfohlen)" : "Manuell festgelegt"),
                InfoRow.Of("Dateien", pageFile.Files.Count > 0 ? string.Join(", ", pageFile.Files) : null),
                InfoRow.Of("Zugewiesen", pageFile.AllocatedBytes is { } allocated ? ByteFormat.Format(allocated) : null),
                InfoRow.Of("Aktuell genutzt", pageFile.CurrentUsageBytes is { } usage ? ByteFormat.Format(usage) : null),
                InfoRow.Of("Spitzenwert", pageFile.PeakUsageBytes is { } peak ? ByteFormat.Format(peak) : null),
            });
        }
    }

    protected override void OnMetrics(SystemMetrics m)
    {
        var used = m.MemoryTotalBytes - Math.Min(m.MemoryAvailableBytes, m.MemoryTotalBytes);
        Fill(_used, MetricId.MemoryUsedPercent, m, m.MemoryTotalBytes > 0 ? $"{ByteFormat.Format((double)used)} von {ByteFormat.Format((double)m.MemoryTotalBytes)}" : "");
        Fill(_commit, MetricId.CommitPercent, m, m.CommitLimitBytes > 0 ? $"{ByteFormat.Format((double)m.CommitUsedBytes)} von {ByteFormat.Format((double)m.CommitLimitBytes)}" : "");
        Fill(_available, MetricId.MemoryAvailable, m, "Sofort für Programme nutzbar");

        const string noCounter = "Der Leistungsindikator ist auf diesem System nicht verfügbar.";
        LiveRows.Clear();
        LiveRows.Add(InfoRow.Of("Standby-Cache", m.StandbyCacheBytes is { } standby ? ByteFormat.Format(standby) : null, noCounter));
        LiveRows.Add(InfoRow.Of("Geänderte Seiten", m.ModifiedPageBytes is { } modified ? ByteFormat.Format(modified) : null, noCounter));
        LiveRows.Add(InfoRow.Of("Systemcache", m.CachedBytes is { } cached ? ByteFormat.Format(cached) : null, noCounter));
        LiveRows.Add(new InfoRow("Hinweis", "Der Standby-Cache ist kein verschwendeter Speicher: Windows gibt ihn sofort frei, sobald Programme ihn brauchen."));

        if (!_processRefreshRunning && DateTimeOffset.Now - _lastProcessRefresh > TimeSpan.FromSeconds(4)) _ = RefreshProcessesAsync();
    }

    private async Task RefreshProcessesAsync()
    {
        _processRefreshRunning = true;
        try
        {
            var top = await Task.Run(() => Services.Processes.GetProcesses().OrderByDescending(p => p.WorkingSetBytes).Take(8).ToList()).ConfigureAwait(true);
            TopProcesses.Clear();
            foreach (var process in top) TopProcesses.Add(new InfoRow(process.Name, $"{ByteFormat.Format(process.WorkingSetBytes)} Arbeitssatz · {ByteFormat.Format(process.PrivateBytes)} privat · PID {process.ProcessId}"));
        }
        catch (Exception)
        {
            // Zusatzinformation – Fehler werden nicht eskaliert.
        }
        finally
        {
            _lastProcessRefresh = DateTimeOffset.Now;
            _processRefreshRunning = false;
        }
    }
}

public sealed partial class VolumeViewModel : ObservableObject
{
    private readonly ActionCoordinator _coordinator;

    public VolumeViewModel(VolumeInfo volume, ActionCoordinator coordinator)
    {
        Volume = volume;
        _coordinator = coordinator;
    }

    public VolumeInfo Volume { get; }
    public string Title => Volume.Letter + (string.IsNullOrWhiteSpace(Volume.Label) ? "" : "  " + Volume.Label) + (Volume.IsSystem ? "  (System)" : "");
    public double UsedPercent => 100 - Volume.FreePercent;
    public string UsageText => $"{ByteFormat.Format(Volume.FreeBytes)} frei von {ByteFormat.Format(Volume.TotalBytes)} ({Volume.FreePercent:0.0} % frei)";
    public string MediaText => Volume.MediaType switch
    {
        DriveMediaType.Nvme => "NVMe-SSD", DriveMediaType.Ssd => "SSD", DriveMediaType.Hdd => "Festplatte (HDD)", DriveMediaType.Removable => "Wechseldatenträger", DriveMediaType.Virtual => "Virtueller Datenträger", _ => "Medientyp unbekannt",
    } + (Volume.FileSystem is { } fs ? " · " + fs : "") + (Volume.TrimSupported is { } trim ? " · TRIM " + (trim ? "aktiv" : "nicht aktiv") : "");
    public bool IsLow => Volume.FreePercent < 12;
    public bool IsSolidState => Volume.MediaType is DriveMediaType.Ssd or DriveMediaType.Nvme;

    private Task RunAsync(string actionId, string title)
        => _coordinator.RunAsync(new[] { new PlanRequest(actionId, ActionParameters.Of(("drive", Volume.Letter))) }, ExecutionSource.User, $"{title} ({Volume.Letter})");

    [RelayCommand] private Task OptimizeAsync() => RunAsync(SystemToolCatalog.DriveOptimize, "Laufwerk optimieren");
    [RelayCommand] private Task RetrimAsync() => RunAsync(SystemToolCatalog.DriveRetrim, "TRIM erneut senden");
    [RelayCommand] private Task CheckAsync() => RunAsync(SystemToolCatalog.ChkdskScan, "Dateisystem prüfen");
}

public sealed record LargeFileRow(string Path, string Size, string Modified);
public sealed record DuplicateRow(string Summary, IReadOnlyList<string> Paths);

/// <summary>Datenträger: Belegung, Zustand, medienabhängige Wartung sowie Suche nach großen Dateien und Duplikaten.</summary>
public sealed partial class StorageViewModel : HardwarePageViewModel
{
    private readonly MetricTileViewModel _active = new("Aktivität", "Brush.Green");
    private readonly MetricTileViewModel _throughput = new("Lesen / Schreiben", "Brush.Cyan", double.NaN);
    private readonly MetricTileViewModel _latency = new("Antwortzeit", "Brush.Amber", double.NaN);
    private CancellationTokenSource? _scanCancellation;

    public StorageViewModel(HardwareServices services) : base(services)
    {
        Tiles = new[] { _active, _throughput, _latency };
        _scanFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddTweaks(m => m.Category == ActionCategory.Storage);
    }

    public override string Key => "storage";
    public override string Title => "Datenträger";
    public override string Subtitle => "Belegung, Zustand und medienabhängige Wartung. SSDs werden nie defragmentiert – Windows sendet dort nur TRIM.";

    public IReadOnlyList<MetricTileViewModel> Tiles { get; }
    public ObservableCollection<VolumeViewModel> Volumes { get; } = new();
    public ObservableCollection<LargeFileRow> LargeFiles { get; } = new();
    public ObservableCollection<DuplicateRow> Duplicates { get; } = new();
    [ObservableProperty] private string _scanFolder;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _scanStatus = "Die Suche liest nur. Gelöscht wird hier nichts – Sie entscheiden im Explorer.";

    protected override IEnumerable<InfoSection> BuildSections(SystemInfo info)
    {
        Volumes.Clear();
        foreach (var volume in info.Volumes) Volumes.Add(new VolumeViewModel(volume, Services.Coordinator));

        const string smartHint = "Dieser Wert wird vom Laufwerk bzw. Treiber nicht gemeldet (häufig bei USB-Gehäusen, RAID oder virtuellen Datenträgern).";
        foreach (var drive in info.Drives)
        {
            yield return new InfoSection(string.IsNullOrWhiteSpace(drive.Model) ? "Laufwerk " + drive.DeviceNumber : drive.Model, new[]
            {
                new InfoRow("Typ", drive.MediaType switch { DriveMediaType.Nvme => "NVMe-SSD", DriveMediaType.Ssd => "SSD", DriveMediaType.Hdd => "Festplatte (HDD)", DriveMediaType.Removable => "Wechseldatenträger", DriveMediaType.Virtual => "Virtuell", _ => "Unbekannt" } + (drive.BusType is { } bus ? " · " + bus : "")),
                new InfoRow("Kapazität", ByteFormat.Format(drive.SizeBytes)),
                new InfoRow("Zustand laut Windows", drive.Health switch { HealthState.Healthy => "In Ordnung", HealthState.Warning => "Warnung – Datensicherung empfohlen", HealthState.Unhealthy => "Fehlerhaft – Daten umgehend sichern", _ => "Nicht verfügbar" }, drive.Health == HealthState.Unknown ? smartHint : null),
                InfoRow.Of("Temperatur", drive.TemperatureCelsius is { } t ? t + " °C" : null, smartHint),
                InfoRow.Of("Abnutzung", drive.WearPercent is { } wear ? wear + " % verbraucht" : null, smartHint),
                InfoRow.Of("Betriebsstunden", drive.PowerOnHours?.ToString("N0"), smartHint),
                InfoRow.Of("Lesefehler", drive.ReadErrors?.ToString("N0"), smartHint),
                InfoRow.Of("Firmware", drive.FirmwareVersion),
            });
        }
        if (info.Drives.Count == 0)
            yield return new InfoSection("Physische Laufwerke", new[] { new InfoRow("Erkennung", "Nicht verfügbar", "Windows hat über die Speicherverwaltung keine physischen Laufwerke gemeldet.") });
    }

    protected override void OnMetrics(SystemMetrics m)
    {
        Fill(_active, MetricId.DiskActive, m, m.DiskQueueLength is { } queue ? $"Warteschlange {queue:0.0}" : "");
        Fill(_throughput, MetricId.DiskRead, m, "Schreiben " + (m.DiskWriteBytesPerSecond is { } write ? ByteFormat.Rate(write) : "n. v."), MetricId.DiskWrite);
        Fill(_latency, MetricId.DiskLatency, m, "Mittlere Antwortzeit je Zugriff");
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        if (Services.Dialogs.PickFolder("Ordner für die Suche auswählen") is { } folder) ScanFolder = folder;
    }

    private bool PrepareScan(out string folder)
    {
        folder = ScanFolder.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            ScanStatus = "Bitte einen vorhandenen Ordner mit vollständigem Pfad angeben.";
            return false;
        }
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        return true;
    }

    [RelayCommand]
    private async Task FindLargeFilesAsync()
    {
        if (!PrepareScan(out var folder)) return;
        var token = _scanCancellation!.Token;
        try
        {
            var progress = new Progress<ScanProgress>(p => ScanStatus = $"{p.FilesSeen:N0} Dateien geprüft …");
            var files = await Task.Run(() => FileAnalysis.FindLargestFiles(folder, 60, 50L * 1024 * 1024, progress, token), token).ConfigureAwait(true);
            LargeFiles.Clear();
            foreach (var file in files) LargeFiles.Add(new LargeFileRow(file.Path, ByteFormat.Format(file.Bytes), file.LastWriteUtc.ToLocalTime().ToString("dd.MM.yyyy")));
            ScanStatus = files.Count == 0 ? "Keine Dateien über 50 MB gefunden." : $"{files.Count} Dateien über 50 MB, zusammen {ByteFormat.Format(files.Sum(f => f.Bytes))}.";
        }
        catch (OperationCanceledException) { ScanStatus = "Suche abgebrochen."; }
        catch (Exception ex) { ScanStatus = "Die Suche ist fehlgeschlagen: " + ex.Message; }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        if (!PrepareScan(out var folder)) return;
        var token = _scanCancellation!.Token;
        try
        {
            var progress = new Progress<ScanProgress>(p => ScanStatus = $"{p.FilesSeen:N0} Dateien geprüft …");
            var groups = await Task.Run(() => FileAnalysis.FindDuplicates(folder, 1024 * 1024, progress, token), token).ConfigureAwait(true);
            Duplicates.Clear();
            foreach (var group in groups.OrderByDescending(g => g.WastedBytes).Take(80))
                Duplicates.Add(new DuplicateRow($"{group.Paths.Count} identische Dateien je {ByteFormat.Format(group.FileSize)} – {ByteFormat.Format(group.WastedBytes)} doppelt belegt", group.Paths));
            ScanStatus = groups.Count == 0 ? "Keine inhaltsgleichen Dateien über 1 MB gefunden." : $"{groups.Count} Gruppen inhaltsgleicher Dateien (SHA-256), zusammen {ByteFormat.Format(groups.Sum(g => g.WastedBytes))} doppelt belegt.";
        }
        catch (OperationCanceledException) { ScanStatus = "Suche abgebrochen."; }
        catch (Exception ex) { ScanStatus = "Die Suche ist fehlgeschlagen: " + ex.Message; }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void CancelScan() => _scanCancellation?.Cancel();

    [RelayCommand]
    private void ShowInExplorer(string? path) => PageHelpers.ShowInExplorer(path);

    protected override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _scanCancellation?.Cancel();
    }
}

/// <summary>Netzwerk: Adapter, Durchsatz, DNS-Konfiguration, Reparaturwerkzeuge und Verbindungsdiagnose.</summary>
public sealed partial class NetworkViewModel : HardwarePageViewModel
{
    private readonly INetworkConfigurator _network;
    private readonly MetricTileViewModel _throughput = new("Download / Upload", "Brush.Cyan", double.NaN);
    private readonly MetricTileViewModel _latency = new("Latenz", "Brush.Amber", double.NaN);
    private CancellationTokenSource? _diagnosticCancellation;

    public NetworkViewModel(HardwareServices services, INetworkConfigurator network) : base(services)
    {
        _network = network;
        Tiles = new[] { _throughput, _latency };
        DnsPresets = new[]
        {
            new Choice<string>("", "Automatisch beziehen (DHCP/Router)"),
            new Choice<string>("1.1.1.1,1.0.0.1", "Cloudflare (1.1.1.1, 1.0.0.1)"),
            new Choice<string>("8.8.8.8,8.8.4.4", "Google (8.8.8.8, 8.8.4.4)"),
            new Choice<string>("9.9.9.9,149.112.112.112", "Quad9 (9.9.9.9, 149.112.112.112)"),
            new Choice<string>("custom", "Eigene Server …"),
        };
        _selectedDnsPreset = DnsPresets[0];
        AddTweaks(m => m.Category == ActionCategory.Network);
    }

    public override string Key => "network";
    public override string Title => "Netzwerk";
    public override string Subtitle => "Adapter, Durchsatz, DNS und Diagnose. „Internet-Beschleuniger“-Tweaks ohne belegten Nutzen bietet diese Anwendung nicht an.";

    public IReadOnlyList<MetricTileViewModel> Tiles { get; }
    public ObservableCollection<Choice<string>> DnsAdapters { get; } = new();
    public IReadOnlyList<Choice<string>> DnsPresets { get; }
    public ObservableCollection<string> DiagnosticLines { get; } = new();
    [ObservableProperty] private Choice<string>? _selectedDnsAdapter;
    [ObservableProperty] private Choice<string> _selectedDnsPreset;
    [ObservableProperty] private string _customDns = "";
    [ObservableProperty] private string _dnsCurrent = "";
    [ObservableProperty] private string? _dnsError;
    [ObservableProperty] private string _diagnosticTarget = "one.one.one.one";
    [ObservableProperty] private bool _isDiagnosing;

    public bool IsCustomDns => SelectedDnsPreset.Value == "custom";
    partial void OnSelectedDnsPresetChanged(Choice<string> value) => OnPropertyChanged(nameof(IsCustomDns));
    partial void OnSelectedDnsAdapterChanged(Choice<string>? value) => UpdateDnsCurrent();

    private IReadOnlyList<AdapterDnsInfo> _dnsInfos = Array.Empty<AdapterDnsInfo>();

    protected override IEnumerable<InfoSection> BuildSections(SystemInfo info)
    {
        foreach (var adapter in info.NetworkAdapters.OrderByDescending(a => a.IsUp))
        {
            yield return new InfoSection(adapter.Name + (adapter.IsUp ? "" : "  (nicht verbunden)"), new[]
            {
                InfoRow.Of("Gerät", adapter.Description),
                new InfoRow("Typ", adapter.Type + (adapter.IsWireless ? " (drahtlos)" : "")),
                InfoRow.Of("Verbindungsgeschwindigkeit", adapter.SpeedBitsPerSecond is > 0 ? FormatSpeed(adapter.SpeedBitsPerSecond.Value) : null, "Der Adapter ist nicht verbunden oder meldet keine Geschwindigkeit."),
                InfoRow.Of("IPv4-Adressen", adapter.IPv4.Count > 0 ? string.Join(", ", adapter.IPv4) : null, "Keine IPv4-Adresse zugewiesen."),
                InfoRow.Of("IPv6-Adressen", adapter.IPv6.Count > 0 ? string.Join(", ", adapter.IPv6) : null, "Keine IPv6-Adresse zugewiesen."),
                InfoRow.Of("Gateway", adapter.Gateways.Count > 0 ? string.Join(", ", adapter.Gateways) : null, "Kein Gateway eingetragen."),
                InfoRow.Of("DNS-Server", adapter.DnsServers.Count > 0 ? string.Join(", ", adapter.DnsServers) : null, "Keine DNS-Server eingetragen."),
                InfoRow.Of("DHCP", adapter.DhcpEnabled, "Aktiv", "Statische Konfiguration"),
            });
        }
        if (info.NetworkAdapters.Count == 0)
            yield return new InfoSection("Netzwerkadapter", new[] { new InfoRow("Erkennung", "Nicht verfügbar", "Windows hat keine Netzwerkadapter gemeldet.") });
    }

    private static string FormatSpeed(long bitsPerSecond)
        => bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1_000_000_000.0:0.#} Gbit/s" : $"{bitsPerSecond / 1_000_000.0:0.#} Mbit/s";

    protected override async Task RefreshStateAsync()
    {
        await base.RefreshStateAsync().ConfigureAwait(true);
        try
        {
            _dnsInfos = await Task.Run(() => _network.GetAdapters()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _dnsInfos = Array.Empty<AdapterDnsInfo>();
            DnsCurrent = "Die DNS-Konfiguration konnte nicht gelesen werden: " + ex.Message;
        }
        var previous = SelectedDnsAdapter?.Value;
        DnsAdapters.Clear();
        foreach (var adapter in _dnsInfos) DnsAdapters.Add(new Choice<string>(adapter.AdapterId, adapter.Name));
        SelectedDnsAdapter = DnsAdapters.FirstOrDefault(a => a.Value == previous) ?? DnsAdapters.FirstOrDefault();
        UpdateDnsCurrent();
    }

    private void UpdateDnsCurrent()
    {
        var info = _dnsInfos.FirstOrDefault(a => a.AdapterId == SelectedDnsAdapter?.Value);
        if (info is null)
        {
            if (_dnsInfos.Count == 0 && string.IsNullOrEmpty(DnsCurrent)) DnsCurrent = "Es wurde kein konfigurierbarer Netzwerkadapter gefunden.";
            return;
        }
        DnsCurrent = "Aktuell: " + (info.IsAutomatic ? "automatisch" + (info.Servers.Count > 0 ? " (" + string.Join(", ", info.Servers) + ")" : "") : string.Join(", ", info.Servers));
    }

    protected override void OnMetrics(SystemMetrics m)
    {
        Fill(_throughput, MetricId.NetworkDown, m, "Upload " + (m.NetworkUpBytesPerSecond is { } up ? ByteFormat.Rate(up) : "n. v."), MetricId.NetworkUp);
        if (_throughput.IsAvailable) _throughput.Value = "↓ " + _throughput.Value;
        Fill(_latency, MetricId.Latency, m, "Mittelwert der eingestellten Gegenstellen");
        if (!_latency.IsAvailable) _latency.Detail = "Die Latenzmessung ist ausgeschaltet. Sie lässt sich in den Einstellungen mit eigenen Gegenstellen aktivieren.";
    }

    [RelayCommand]
    private async Task ApplyDnsAsync()
    {
        DnsError = null;
        if (SelectedDnsAdapter is null)
        {
            DnsError = "Bitte zuerst einen Adapter auswählen.";
            return;
        }
        var servers = IsCustomDns ? string.Join(",", CustomDns.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : SelectedDnsPreset.Value;
        if (IsCustomDns && servers.Length == 0)
        {
            DnsError = "Bitte mindestens eine IPv4-Adresse angeben.";
            return;
        }
        var parameters = servers.Length == 0
            ? ActionParameters.Of(("adapter", SelectedDnsAdapter.Value))
            : ActionParameters.Of(("adapter", SelectedDnsAdapter.Value), ("servers", servers));
        await Services.Coordinator.RunAsync(new[] { new PlanRequest(DnsSetAction.ActionId, parameters) }, ExecutionSource.User, "DNS-Server für " + SelectedDnsAdapter.Label).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task FlushDnsAsync() => Services.Coordinator.RunAsync(new[] { new PlanRequest(SystemToolCatalog.DnsFlush) }, ExecutionSource.User, "DNS-Cache leeren");

    [RelayCommand]
    private Task ResetWinsockAsync() => Services.Coordinator.RunAsync(new[] { new PlanRequest(SystemToolCatalog.WinsockReset) }, ExecutionSource.User, "Winsock-Katalog zurücksetzen");

    private bool BeginDiagnostic(out string target)
    {
        target = DiagnosticTarget.Trim();
        if (!NetworkDiagnostics.IsValidTarget(target))
        {
            DiagnosticLines.Clear();
            DiagnosticLines.Add("Bitte einen gültigen Hostnamen oder eine IP-Adresse angeben.");
            return false;
        }
        _diagnosticCancellation?.Cancel();
        _diagnosticCancellation = new CancellationTokenSource();
        DiagnosticLines.Clear();
        IsDiagnosing = true;
        return true;
    }

    [RelayCommand]
    private async Task PingAsync()
    {
        if (!BeginDiagnostic(out var target)) return;
        try
        {
            DiagnosticLines.Add($"Ping an {target} (10 Pakete) …");
            var progress = new Progress<long?>(ms => DiagnosticLines.Add(ms is { } value ? $"Antwort nach {value} ms" : "Zeitüberschreitung"));
            var summary = await NetworkDiagnostics.PingAsync(target, 10, 2000, progress, _diagnosticCancellation!.Token).ConfigureAwait(true);
            DiagnosticLines.Add(summary.Received == 0
                ? $"Keine Antwort ({summary.Sent} gesendet). Manche Gegenstellen beantworten ICMP grundsätzlich nicht."
                : $"Ergebnis: {summary.Received}/{summary.Sent} Antworten, Verlust {summary.LossPercent:0.#} %, Minimum {summary.MinimumMs:0} ms, Mittel {summary.AverageMs:0.0} ms, Maximum {summary.MaximumMs:0} ms, Jitter {summary.JitterMs:0.0} ms.");
        }
        catch (OperationCanceledException) { DiagnosticLines.Add("Abgebrochen."); }
        catch (Exception ex) { DiagnosticLines.Add("Fehlgeschlagen: " + ex.Message); }
        finally { IsDiagnosing = false; }
    }

    [RelayCommand]
    private async Task TracerouteAsync()
    {
        if (!BeginDiagnostic(out var target)) return;
        try
        {
            DiagnosticLines.Add($"Routenverfolgung zu {target} (höchstens 30 Stationen) …");
            var progress = new Progress<TracerouteHop>(hop => DiagnosticLines.Add($"{hop.Number,2}  {(hop.TimedOut ? "*" : hop.RoundtripMs + " ms"),8}  {hop.Address ?? "keine Antwort"}"));
            var hops = await NetworkDiagnostics.TracerouteAsync(target, 30, 2000, progress, _diagnosticCancellation!.Token).ConfigureAwait(true);
            DiagnosticLines.Add($"Abgeschlossen nach {hops.Count} Station(en).");
        }
        catch (OperationCanceledException) { DiagnosticLines.Add("Abgebrochen."); }
        catch (Exception ex) { DiagnosticLines.Add("Fehlgeschlagen: " + ex.Message); }
        finally { IsDiagnosing = false; }
    }

    [RelayCommand]
    private async Task MeasureDnsAsync()
    {
        if (!BeginDiagnostic(out var target)) return;
        try
        {
            var timing = await NetworkDiagnostics.MeasureDnsAsync(target, _diagnosticCancellation!.Token).ConfigureAwait(true);
            DiagnosticLines.Add(timing.Error is { } error
                ? $"Namensauflösung für {target} fehlgeschlagen: {error}"
                : $"Namensauflösung für {target}: {timing.Milliseconds:0.0} ms → {string.Join(", ", timing.Addresses)}");
            DiagnosticLines.Add("Hinweis: Wiederholte Messungen sind durch den DNS-Cache schneller. Für einen Vergleich vorher den DNS-Cache leeren.");
        }
        catch (OperationCanceledException) { DiagnosticLines.Add("Abgebrochen."); }
        catch (Exception ex) { DiagnosticLines.Add("Fehlgeschlagen: " + ex.Message); }
        finally { IsDiagnosing = false; }
    }

    [RelayCommand]
    private void CancelDiagnostic() => _diagnosticCancellation?.Cancel();

    protected override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _diagnosticCancellation?.Cancel();
    }
}

public sealed partial class PowerPlanViewModel : ObservableObject
{
    private readonly ActionCoordinator _coordinator;

    public PowerPlanViewModel(PowerPlanInfo plan, ActionCoordinator coordinator)
    {
        Plan = plan;
        _coordinator = coordinator;
    }

    public PowerPlanInfo Plan { get; }
    public string Name => Plan.Name;
    public bool IsActive => Plan.IsActive;
    public bool CanActivate => !Plan.IsActive;
    public string Kind => Plan.IsBuiltIn ? "Windows-Standardplan" : "Benutzerdefinierter oder Herstellerplan";
    public string Hint => Plan.Id == WellKnownPowerPlans.Balanced ? "Empfohlen für die meisten Systeme – regelt Leistung nach Bedarf."
        : Plan.Id == WellKnownPowerPlans.HighPerformance ? "Hält den Prozessor leistungsbereit: mehr Stromverbrauch, Abwärme und Lüftergeräusch."
        : Plan.Id == WellKnownPowerPlans.PowerSaver ? "Spart Energie auf Kosten der Leistung."
        : Plan.Id == WellKnownPowerPlans.UltimatePerformance ? "Für Workstations gedacht: höchster Verbrauch, kaum Energiesparfunktionen."
        : "";

    [RelayCommand]
    private Task ActivateAsync()
        => _coordinator.RunAsync(new[] { new PlanRequest(PowerPlanActivateAction.ActionId, ActionParameters.Of(("plan", Plan.Id.ToString()))) }, ExecutionSource.User, "Energieplan „" + Plan.Name + "“ aktivieren");
}

/// <summary>Energieverwaltung: Energiepläne, Akkuzustand und unterstützte Energieeinstellungen.</summary>
public sealed partial class PowerViewModel : HardwarePageViewModel
{
    private readonly IPowerPolicy _power;

    public PowerViewModel(HardwareServices services, IPowerPolicy power) : base(services)
    {
        _power = power;
        foreach (var definition in PowerSettingCatalog.All) Settings.Add(new PowerSettingItemViewModel(definition, power, services.Coordinator));
        AddTweaks(m => m.Category == ActionCategory.Power);
    }

    public override string Key => "power";
    public override string Title => "Energieverwaltung";
    public override string Subtitle => "Energiepläne und Einstellungen des aktiven Plans. Jede Änderung wird gesichert und lässt sich zurücknehmen.";

    public ObservableCollection<PowerPlanViewModel> Plans { get; } = new();
    public ObservableCollection<PowerSettingItemViewModel> Settings { get; } = new();
    [ObservableProperty] private string _supplyText = "Stromversorgung wird ermittelt …";
    [ObservableProperty] private string _plansNote = "";

    protected override IEnumerable<InfoSection> BuildSections(SystemInfo info)
    {
        if (info.Battery is { } battery)
        {
            yield return new InfoSection("Akku", new[]
            {
                InfoRow.Of("Ladestand", battery.ChargePercent is { } charge ? charge + " %" : null),
                InfoRow.Of("Stromversorgung", battery.OnAcPower, "Netzbetrieb", "Akkubetrieb"),
                InfoRow.Of("Zustand (volle Ladung / Auslegung)", battery.HealthPercent is { } health ? health.ToString("0") + " %" : null, "Der Akku meldet keine Kapazitätswerte."),
                InfoRow.Of("Auslegungskapazität", battery.DesignCapacityMilliwattHours is { } design ? (design / 1000.0).ToString("0.0") + " Wh" : null, "Der Akku meldet keine Kapazitätswerte."),
                InfoRow.Of("Kapazität bei voller Ladung", battery.FullChargeCapacityMilliwattHours is { } full ? (full / 1000.0).ToString("0.0") + " Wh" : null, "Der Akku meldet keine Kapazitätswerte."),
                InfoRow.Of("Ladezyklen", battery.CycleCount?.ToString(), "Der Akku meldet keine Zykluszahl."),
                InfoRow.Of("Chemie", battery.Chemistry),
            });
        }
        else
        {
            yield return new InfoSection("Akku", new[] { new InfoRow("Akku", "Nicht vorhanden", "Dieses System hat keinen Akku oder meldet ihn nicht.") });
        }
    }

    protected override async Task RefreshStateAsync()
    {
        await base.RefreshStateAsync().ConfigureAwait(true);
        try
        {
            var plans = await Task.Run(() => _power.GetPlans()).ConfigureAwait(true);
            Plans.Clear();
            foreach (var plan in plans.OrderByDescending(p => p.IsActive).ThenBy(p => p.Name)) Plans.Add(new PowerPlanViewModel(plan, Services.Coordinator));
            PlansNote = plans.Count <= 1
                ? "Dieses System stellt nur einen Energieplan bereit. Das ist auf Geräten mit Modern Standby normal – dort steuert der Leistungsregler in den Windows-Einstellungen das Verhalten."
                : "";
        }
        catch (Exception ex)
        {
            Plans.Clear();
            PlansNote = "Die Energiepläne konnten nicht gelesen werden: " + ex.Message;
        }
        foreach (var setting in Settings) setting.Refresh();
    }

    protected override void OnMetrics(SystemMetrics m)
    {
        SupplyText = m.OnAcPower switch
        {
            true => "Netzbetrieb" + (m.BatteryPercent is { } percent ? $" · Akku {percent} %" : ""),
            false => $"Akkubetrieb · {m.BatteryPercent?.ToString() ?? "–"} %" + (m.BatteryRateMilliwatts is { } rate && rate != 0 ? $" · Entladung {Math.Abs(rate) / 1000:0.0} W" : ""),
            _ => "Die Stromversorgung wird von Windows nicht gemeldet.",
        };
    }

    [RelayCommand]
    private void OpenWindowsPowerSettings() => PageHelpers.OpenUri("ms-settings:powersleep");
}
