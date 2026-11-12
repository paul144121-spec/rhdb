using System.Diagnostics;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Data;
using NexusOptimizer.Windows;
using NexusOptimizer.Windows.Diagnostics;
using NexusOptimizer.Windows.Gaming;
using NexusOptimizer.Windows.Hardware;
using NexusOptimizer.Windows.Monitoring;
using NexusOptimizer.Windows.SystemAccess;

// Portable Diagnoseprüfung: führt die lesenden Windows-Zugriffe der Anwendung einzeln aus und meldet je Bereich,
// ob sie auf diesem System funktionieren. Verändert nichts außer einem Wegwerf-Schlüssel unter HKCU und einer temporären Datenbank.
var failures = 0;
var total = 0;

async Task Check(string name, Func<Task<string>> body)
{
    total++;
    var stopwatch = Stopwatch.StartNew();
    try
    {
        var detail = await body();
        Console.WriteLine($"[ OK ] {name} ({stopwatch.ElapsedMilliseconds} ms): {detail}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"[FEHLER] {name} ({stopwatch.ElapsedMilliseconds} ms): {ex.GetType().Name}: {ex.Message}");
    }
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"NEXUS Optimizer AI – Diagnoseprüfung auf {Environment.OSVersion}, {Environment.ProcessorCount} logische Prozessoren");

var platform = new WindowsPlatformServices();

await Check("Messwerte (PDH, Speicher, Energie)", async () =>
{
    using var collector = new WindowsMetricsCollector();
    collector.Collect();
    await Task.Delay(1100);
    var m = collector.Collect();
    if (m.MemoryTotalBytes == 0) throw new InvalidOperationException("Kein Arbeitsspeicher gemeldet.");
    return $"CPU {m.CpuTotalPercent?.ToString("0.0") ?? "n. v."} %, Kerne {m.CpuPerCorePercent.Count}, RAM {ByteFormat.Format((long)m.MemoryTotalBytes)} ({m.MemoryUsedPercent:0} % belegt), " +
           $"GPU {m.GpuUsagePercent?.ToString("0") ?? "n. v."} %, Datenträger {m.DiskActivePercent?.ToString("0") ?? "n. v."} %, Netz ↓ {(m.NetworkDownBytesPerSecond is { } d ? ByteFormat.Rate(d) : "n. v.")}, " +
           $"Prozesse {m.ProcessCount}, Akku {m.BatteryPercent?.ToString() ?? "keiner"}, nicht verfügbar: {collector.UnavailableReasons.Count} Messgrößen";
});

await Check("Hardwareerkennung (WMI, Registrierung)", async () =>
{
    var info = await new WindowsHardwareInfoProvider(platform.Power).GetSystemInfoAsync();
    return $"{info.OsName} Build {info.OsBuild}; CPU „{info.CpuName}“; RAM {ByteFormat.Format(info.MemoryTotalBytes)}; GPUs {info.Gpus.Count}; Datenträger {info.Drives.Count}; Volumes {info.Volumes.Count}; " +
           $"Adapter {info.NetworkAdapters.Count}; Startmodus {info.BootMode ?? "n. v."}; Secure Boot {info.SecureBootEnabled?.ToString() ?? "n. v."}; TPM {info.TpmPresent?.ToString() ?? "n. v."}; Hinweise {info.Notes.Count}";
});

await Check("Energiepläne (powrprof)", () =>
{
    var plans = platform.Power.GetPlans();
    return Task.FromResult($"{plans.Count} Pläne, aktiv: {plans.FirstOrDefault(p => p.IsActive)?.Name ?? "?"}");
});

await Check("Dienste (WMI, SCM)", () => Task.FromResult($"{platform.Services.GetServices().Count} Dienste"));
await Check("Autostart (Registrierung, Ordner)", () => Task.FromResult($"{platform.Startup.GetEntries().Count} Einträge"));

await Check("Prozesse (Toolhelp, Leistungsdaten)", async () =>
{
    platform.Processes.GetProcesses();
    await Task.Delay(300);
    var processes = platform.Processes.GetProcesses();
    var self = processes.FirstOrDefault(p => p.ProcessId == Environment.ProcessId) ?? throw new InvalidOperationException("Eigener Prozess nicht gefunden.");
    return $"{processes.Count} Prozesse; dieser Prozess: {ByteFormat.Format(self.WorkingSetBytes)}, Pfad {(self.ExecutablePath is null ? "n. v." : "ok")}";
});

await Check("Bereinigung (nur Analyse)", () =>
{
    long bytes = 0;
    var files = 0;
    foreach (var category in CleanupCategories.For(PrivilegeLevel.User))
    {
        var scan = platform.Cleanup.Scan(category.Id);
        bytes += scan.Bytes;
        files += scan.Files;
    }
    return Task.FromResult($"{ByteFormat.Format(bytes)} in {files} Dateien bereinigbar (nichts gelöscht)");
});

await Check("Registrierungs-Tweaks (Zustand lesen)", async () =>
{
    var applied = 0;
    foreach (var definition in RegistryTweakCatalog.All)
    {
        var state = await new RegistryTweakAction(definition, platform.Registry).DetectCurrentStateAsync(ActionParameters.Empty);
        if (state.IsApplied == true) applied++;
    }
    return $"{RegistryTweakCatalog.All.Count} Tweaks gelesen, {applied} bereits im Zielzustand";
});

await Check("Engine: Anwenden, Verifizieren, Journal, Rollback (Wegwerf-Schlüssel)", async () =>
{
    var key = @"Software\NexusOptimizerAI.Probe\" + Guid.NewGuid().ToString("N");
    var database = Path.Combine(Path.GetTempPath(), "nexus-probe-" + Guid.NewGuid().ToString("N"), "nexus.db");
    try
    {
        var definition = new RegistryTweakDefinition
        {
            Id = "probe.disposable", Name = "Probe", Description = "Probe", Category = ActionCategory.Interface, Root = RegistryRoot.CurrentUser, KeyPath = key, ValueName = "Wert",
            OptimizedValue = RegistryValueData.Text("100"), WindowsDefault = RegistryValueData.Text("400"), OptimizedLabel = "100", DefaultLabel = "400", ExpectedBehavior = "Probe",
        };
        var catalog = new NexusOptimizer.Core.Actions.ActionCatalog(new[] { new RegistryTweakAction(definition, platform.Registry) });
        var journal = new SqliteTransactionJournal(new NexusDatabase(database));
        var executor = new OptimizationExecutor(new LocalActionRunner(catalog), journal, new ExecutionGate());
        var plan = await new OptimizationPlanner(catalog).BuildAsync(new[] { new PlanRequest(definition.ActionId) }, new RiskPolicy(), new SystemContext { WindowsBuild = Environment.OSVersion.Version.Build }, ExecutionSource.User);
        var batch = await executor.ExecuteAsync(plan);
        if (!batch.AllSucceeded) throw new InvalidOperationException("Anwenden fehlgeschlagen: " + batch.Steps.FirstOrDefault()?.Message);
        var written = platform.Registry.ReadValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, key, "Wert")?.Data;
        var outcome = await new RollbackService(executor, journal).RollbackLastAsync();
        var afterRollback = platform.Registry.ReadValue(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, key, "Wert");
        if (outcome is not { Success: true } || afterRollback is not null || platform.Registry.KeyExists(RegistryRoot.CurrentUser, RegistryViewKind.Registry64, key))
            throw new InvalidOperationException("Rollback hat den Ursprungszustand nicht wiederhergestellt.");
        return $"geschrieben „{written}“, verifiziert, Journal {journal.GetRecent().Single().Status}, Wert und Schlüssel wieder entfernt";
    }
    finally
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\NexusOptimizerAI.Probe", false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(database)!, true); } catch (IOException) { }
    }
});

await Check("Geheimnisablage (DPAPI)", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "nexus-probe-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new DpapiSecretStore(directory);
        store.Set("probe", "geheim-123");
        if (store.Get("probe") != "geheim-123") throw new InvalidOperationException("Entschlüsselung liefert einen anderen Wert.");
        return Task.FromResult("verschlüsselt gespeichert und wieder gelesen");
    }
    finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
});

await Check("Diagnose (Ereignisprotokoll, Geräte, Konfiguration)", async () =>
{
    var diagnostics = new WindowsDiagnostics();
    var errors = await diagnostics.GetRecentErrorsAsync(TimeSpan.FromDays(1), 20);
    var devices = await diagnostics.GetDeviceProblemsAsync();
    var advanced = await diagnostics.GetAdvancedDiagnosticsAsync();
    return $"{errors.Count} Fehlerereignisse (24 h), {devices.Count} Geräteprobleme, {advanced.Count} Konfigurationsangaben";
});

await Check("Spielbibliotheken", async () => $"{(await new WindowsGameLibraryScanner().ScanAsync()).Count} Spiele erkannt");
await Check("Papierkorb (nur Abfrage)", () => Task.FromResult(platform.RecycleBin.Query() is { } bin ? $"{bin.Items} Elemente, {ByteFormat.Format(bin.Bytes)}" : "nicht abfragbar"));
await Check("Netzwerkadapter (DNS-Konfiguration lesen)", () => Task.FromResult($"{platform.Network.GetAdapters().Count} Adapter"));
await Check("Aktionskatalog", () => Task.FromResult($"{WindowsActionCatalogFactory.Create(platform).All.Count} Aktionen in der Allowlist"));

Console.WriteLine($"Ergebnis: {total - failures} von {total} Prüfungen erfolgreich.");
return failures == 0 ? 0 : 1;
