using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Storage;

namespace NexusOptimizer.App.ViewModels;

/// <summary>Vollständige Übersicht über alle erkannte Hardware an einem Ort. Nicht erkennbare Angaben stehen als „nicht verfügbar“ da – nichts wird ergänzt oder geschätzt.</summary>
public sealed partial class HardwareOverviewViewModel : HardwarePageViewModel
{
    public HardwareOverviewViewModel(HardwareServices services) : base(services) { }

    public override string Key => "hardware";
    public override string Title => "Hardware-Übersicht";
    public override string Subtitle => "Alle erkannten Komponenten dieses Computers auf einen Blick: System, Prozessor, Mainboard, Speicher, Grafik, Laufwerke, Netzwerk, Akku und Sicherheit.";

    protected override void OnMetrics(SystemMetrics metrics) { }

    protected override IEnumerable<InfoSection> BuildSections(SystemInfo i)
    {
        yield return new InfoSection("System", new[]
        {
            InfoRow.Of("Computername", i.ComputerName),
            InfoRow.Of("Hersteller", i.SystemManufacturer),
            InfoRow.Of("Modell", i.SystemModel),
            InfoRow.Of("Windows", i.OsName + (i.OsDisplayVersion is { } v ? " " + v : "")),
            InfoRow.Of("Edition", i.OsEdition),
            new InfoRow("Build", i.OsBuild + (i.OsRevision is { } r ? "." + r : "")),
            InfoRow.Of("Aktiviert", i.IsActivated),
            InfoRow.Of("Startmodus", i.BootMode),
            new InfoRow("Virtuelle Maschine", i.IsVirtualMachine ? "Ja" : "Nein"),
        });
        yield return new InfoSection("Prozessor", new[]
        {
            InfoRow.Of("Modell", i.CpuName),
            InfoRow.Of("Physische Kerne", i.CpuCores?.ToString()),
            new InfoRow("Logische Prozessoren", i.CpuLogicalProcessors.ToString()),
            InfoRow.Of("Nenntakt", i.CpuMaxClockMhz is { } m ? $"{m / 1000.0:0.00} GHz" : null),
            InfoRow.Of("Virtualisierung in der Firmware", i.VirtualizationEnabledInFirmware),
            InfoRow.Of("Hypervisor aktiv", i.HypervisorPresent),
        });
        yield return new InfoSection("Mainboard und Firmware", new[]
        {
            InfoRow.Of("Hersteller", i.MotherboardManufacturer),
            InfoRow.Of("Modell", i.MotherboardModel),
            InfoRow.Of("BIOS/UEFI-Version", i.BiosVersion),
            InfoRow.Of("BIOS/UEFI-Datum", i.BiosDate?.ToString("dd.MM.yyyy")),
            InfoRow.Of("Sicherer Start", i.SecureBootEnabled, "Aktiv", "Nicht aktiv"),
            InfoRow.Of("TPM", i.TpmPresent is null ? null : i.TpmPresent == true ? "Vorhanden" + (i.TpmVersion is { } t ? " (" + t + ")" : "") : "Nicht vorhanden"),
        });
        yield return new InfoSection("Arbeitsspeicher", new[]
        {
            InfoRow.Of("Gesamt", i.MemoryTotalBytes > 0 ? ByteFormat.Format(i.MemoryTotalBytes) : null),
            InfoRow.Of("Geschwindigkeit", i.MemorySpeedMhz is { } s ? s + " MHz" : null, "Die Firmware meldet keine Modulgeschwindigkeit."),
            new InfoRow("Module", i.MemoryModules.Count > 0 ? string.Join("; ", i.MemoryModules.Select(x => $"{x.Slot}: {ByteFormat.Format(x.CapacityBytes)}" + (x.SpeedMhz is { } z ? $" @ {z} MHz" : "") + (string.IsNullOrWhiteSpace(x.Manufacturer) ? "" : " (" + x.Manufacturer + ")"))) : "Nicht verfügbar", i.MemoryModules.Count > 0 ? null : "Die Firmware meldet keine Modulinformationen."),
        });
        foreach (var g in i.Gpus)
            yield return new InfoSection("Grafik: " + g.Name, new[]
            {
                InfoRow.Of("Hersteller", g.Vendor),
                new InfoRow("Typ", g.IsIntegrated ? "Integriert" : "Dediziert"),
                InfoRow.Of("Grafikspeicher", g.DedicatedMemoryBytes is > 0 ? ByteFormat.Format(g.DedicatedMemoryBytes.Value) : null),
                InfoRow.Of("Treiber", g.DriverVersion + (g.DriverDate is { } d ? " (" + d.ToString("dd.MM.yyyy") + ")" : "")),
                InfoRow.Of("Anzeige", g.Resolution is null ? null : g.Resolution + (g.RefreshRateHz is { } hz ? $" @ {hz} Hz" : "")),
            });
        if (i.Gpus.Count == 0) yield return new InfoSection("Grafik", new[] { new InfoRow("Erkennung", "Nicht verfügbar", "Windows hat keinen Grafikadapter gemeldet.") });
        foreach (var d in i.Drives)
            yield return new InfoSection("Laufwerk: " + (string.IsNullOrWhiteSpace(d.Model) ? "Nr. " + d.DeviceNumber : d.Model), new[]
            {
                new InfoRow("Typ", d.MediaType + (d.BusType is { } b ? " · " + b : "")),
                new InfoRow("Kapazität", ByteFormat.Format(d.SizeBytes)),
                new InfoRow("Zustand", d.Health switch { HealthState.Healthy => "In Ordnung", HealthState.Warning => "Warnung", HealthState.Unhealthy => "Fehlerhaft", _ => "Nicht verfügbar" }),
                InfoRow.Of("Firmware", d.FirmwareVersion),
                InfoRow.Of("Temperatur", d.TemperatureCelsius is { } temp ? temp + " °C" : null),
            });
        if (i.Volumes.Count > 0)
            yield return new InfoSection("Partitionen", i.Volumes.Select(v => new InfoRow(v.Letter + (string.IsNullOrWhiteSpace(v.Label) ? "" : " " + v.Label), $"{ByteFormat.Format(v.FreeBytes)} frei von {ByteFormat.Format(v.TotalBytes)} · {v.FileSystem ?? "?"}")).ToList());
        foreach (var n in i.NetworkAdapters)
            yield return new InfoSection("Netzwerk: " + n.Name, new[]
            {
                InfoRow.Of("Gerät", n.Description),
                new InfoRow("Status", (n.IsUp ? "Verbunden" : "Getrennt") + (n.IsWireless ? " · WLAN" : "")),
                InfoRow.Of("Geschwindigkeit", n.SpeedBitsPerSecond is > 0 ? $"{n.SpeedBitsPerSecond / 1_000_000.0:0.#} Mbit/s" : null),
                InfoRow.Of("IPv4", n.IPv4.Count > 0 ? string.Join(", ", n.IPv4) : null),
                InfoRow.Of("MAC-Adresse", n.MacAddress),
            });
        yield return new InfoSection("Akku", i.Battery is { } bt
            ? new[]
            {
                InfoRow.Of("Ladestand", bt.ChargePercent is { } c ? c + " %" : null),
                InfoRow.Of("Netzbetrieb", bt.OnAcPower),
                InfoRow.Of("Zustand", bt.HealthPercent is { } h ? h.ToString("0") + " % der Auslegung" : null),
                InfoRow.Of("Ladezyklen", bt.CycleCount?.ToString()),
            }
            : new[] { new InfoRow("Akku", "Nicht vorhanden", "Dieses System hat keinen Akku oder meldet ihn nicht.") });
        if (i.SecurityFeatures.Count > 0)
            yield return new InfoSection("Sicherheitsfunktionen", i.SecurityFeatures.Select(f => f.Enabled is null ? new InfoRow(f.Name, "Nicht verfügbar", f.Detail) : new InfoRow(f.Name, f.Enabled.Value ? "Aktiv" : "Nicht aktiv")).ToList());
    }

    [RelayCommand]
    private Task ExportAsync() => GuardAsync(async () =>
    {
        var path = Services.Dialogs.SaveFile("Textbericht (*.txt)|*.txt", $"nexus-hardware-{DateTime.Now:yyyyMMdd-HHmm}.txt", "Hardware-Übersicht speichern");
        if (path is null) return;
        var text = new StringBuilder("NEXUS Optimizer AI – Hardware-Übersicht " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + Environment.NewLine);
        foreach (var section in Sections)
        {
            text.AppendLine().AppendLine("[" + section.Title + "]");
            foreach (var row in section.Rows) text.AppendLine($"{row.Label}: {row.Value}");
        }
        await File.WriteAllTextAsync(path, text.ToString(), Encoding.UTF8).ConfigureAwait(true);
    }, "Der Bericht konnte nicht gespeichert werden.");
}
