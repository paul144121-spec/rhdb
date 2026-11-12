using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Storage;

/// <summary>Ein konkreter, fest definierter Bereinigungsort.</summary>
/// <param name="Root">Absoluter Ordnerpfad. Wird nie selbst gelöscht.</param>
/// <param name="Pattern">Dateimuster (z. B. „thumbcache_*.db“).</param>
/// <param name="Recursive">Unterordner einbeziehen.</param>
/// <param name="MinAge">Nur Dateien, die seit mindestens dieser Zeit nicht mehr geändert wurden.</param>
public sealed record CleanupLocation(string Root, string Pattern = "*", bool Recursive = true, TimeSpan? MinAge = null);

public sealed record CleanupCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public PrivilegeLevel Privilege { get; init; } = PrivilegeLevel.User;
    public bool SelectedByDefault { get; init; }
    public string Safeguards { get; init; } = "";
    /// <summary>Prozesse, die nicht laufen dürfen, damit die Kategorie bereinigt wird (z. B. der zugehörige Browser).</summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();
}

public static class CleanupCategories
{
    public const string UserTemp = "user-temp";
    public const string ThumbnailCache = "thumbnail-cache";
    public const string UserCrashDumps = "user-crash-dumps";
    public const string UserErrorReports = "user-error-reports";
    public const string ShaderCache = "directx-shader-cache";
    public const string EdgeCache = "browser-cache-edge";
    public const string ChromeCache = "browser-cache-chrome";
    public const string FirefoxCache = "browser-cache-firefox";
    public const string RecycleBin = "recycle-bin";
    public const string WindowsTemp = "windows-temp";
    public const string SystemCrashDumps = "system-crash-dumps";
    public const string SystemErrorReports = "system-error-reports";
    public const string OldWindowsLogs = "old-windows-logs";

    private const string BrowserSafeguard = "Es werden ausschließlich Cache-Ordner geleert. Profile, Lesezeichen, Verlauf, Cookies, Erweiterungen und gespeicherte Zugangsdaten bleiben unberührt. Bei laufendem Browser wird die Kategorie übersprungen.";

    public static IReadOnlyList<CleanupCategory> All { get; } = new[]
    {
        new CleanupCategory { Id = UserTemp, Name = "Temporäre Dateien des Benutzers", SelectedByDefault = true,
            Description = "Inhalte des Benutzer-Temp-Ordners (%TEMP%), die älter als 24 Stunden sind.",
            Safeguards = "Dateien der letzten 24 Stunden und gesperrte Dateien bleiben erhalten, damit laufende Installationen nicht gestört werden." },
        new CleanupCategory { Id = ThumbnailCache, Name = "Miniaturansichten-Cache",
            Description = "Zwischengespeicherte Vorschaubilder des Explorers. Windows erstellt sie bei Bedarf neu.",
            Safeguards = "Vom Explorer aktuell verwendete Cache-Dateien sind gesperrt und werden übersprungen." },
        new CleanupCategory { Id = UserCrashDumps, Name = "Absturzabbilder von Anwendungen", SelectedByDefault = true,
            Description = "Speicherabbilder abgestürzter Programme im Benutzerprofil (CrashDumps).",
            Safeguards = "Nur für die Fehleranalyse relevant. Wenn Sie einen Absturz noch untersuchen möchten, wählen Sie diese Kategorie ab." },
        new CleanupCategory { Id = UserErrorReports, Name = "Fehlerberichte des Benutzers", SelectedByDefault = true,
            Description = "Archivierte und wartende Windows-Fehlerberichte des Benutzerkontos.", Safeguards = "Betrifft nur Berichte, keine Anwendungsdaten." },
        new CleanupCategory { Id = ShaderCache, Name = "DirectX-Shader-Cache",
            Description = "Von Grafiktreibern zwischengespeicherte Shader. Wird von Spielen bei Bedarf neu aufgebaut.",
            Safeguards = "Nach dem Leeren können Spiele beim ersten Start kurzzeitig ruckeln, bis der Cache neu erstellt ist." },
        new CleanupCategory { Id = EdgeCache, Name = "Microsoft Edge: Cache", Description = "Zwischengespeicherte Webinhalte von Microsoft Edge.", Safeguards = BrowserSafeguard, BlockingProcesses = new[] { "msedge" } },
        new CleanupCategory { Id = ChromeCache, Name = "Google Chrome: Cache", Description = "Zwischengespeicherte Webinhalte von Google Chrome.", Safeguards = BrowserSafeguard, BlockingProcesses = new[] { "chrome" } },
        new CleanupCategory { Id = FirefoxCache, Name = "Mozilla Firefox: Cache", Description = "Zwischengespeicherte Webinhalte von Mozilla Firefox.", Safeguards = BrowserSafeguard, BlockingProcesses = new[] { "firefox" } },
        new CleanupCategory { Id = RecycleBin, Name = "Papierkorb",
            Description = "Leert den Papierkorb aller Laufwerke für das aktuelle Benutzerkonto.",
            Safeguards = "Die Dateien sind danach endgültig gelöscht. Prüfen Sie den Papierkorb vorher." },
        new CleanupCategory { Id = WindowsTemp, Name = "Temporäre Dateien von Windows", Privilege = PrivilegeLevel.Administrator, SelectedByDefault = true,
            Description = "Inhalte von C:\\Windows\\Temp, die älter als 24 Stunden sind.",
            Safeguards = "Dateien der letzten 24 Stunden und gesperrte Dateien bleiben erhalten." },
        new CleanupCategory { Id = SystemCrashDumps, Name = "Systemabsturzabbilder", Privilege = PrivilegeLevel.Administrator,
            Description = "Speicherabbilder von Bluescreens und Treiberfehlern (Ordner Minidump und LiveKernelReports). Die Datei MEMORY.DMP wird nicht angetastet.",
            Safeguards = "Wenn Sie einen Bluescreen noch analysieren (lassen) möchten, wählen Sie diese Kategorie ab." },
        new CleanupCategory { Id = SystemErrorReports, Name = "Fehlerberichte des Systems", Privilege = PrivilegeLevel.Administrator, SelectedByDefault = true,
            Description = "Systemweit archivierte und wartende Windows-Fehlerberichte.", Safeguards = "Betrifft nur Berichte, keine Anwendungsdaten." },
        new CleanupCategory { Id = OldWindowsLogs, Name = "Alte Wartungsprotokolle (CBS)", Privilege = PrivilegeLevel.Administrator,
            Description = "Archivierte CBS-Protokolle (CbsPersist_*), die älter als 30 Tage sind.",
            Safeguards = "Das aktuelle CBS-Protokoll bleibt erhalten." },
    };

    public static CleanupCategory? Find(string id) => All.FirstOrDefault(c => c.Id == id);
    public static IEnumerable<CleanupCategory> For(PrivilegeLevel privilege) => All.Where(c => c.Privilege == privilege);
}

/// <summary>Löst Kategorien in konkrete Pfade des aktuellen Systems auf (Windows-spezifisch).</summary>
public interface ICleanupLocationProvider
{
    IReadOnlyList<CleanupLocation> GetLocations(string categoryId);
    /// <summary>True, wenn einer der blockierenden Prozesse läuft.</summary>
    bool IsProcessRunning(string processName);
}

public interface IRecycleBin
{
    (long Bytes, long Items)? Query();
    bool Empty();
}

public sealed record CleanupScanResult(string CategoryId, long Bytes, int Files, IReadOnlyList<string> Locations, int Inaccessible, string? BlockedReason = null)
{
    public bool IsBlocked => BlockedReason is not null;
}

public sealed record CleanupRunResult(string CategoryId, long FreedBytes, int DeletedFiles, int SkippedFiles, IReadOnlyList<string> Errors, string? BlockedReason = null);

/// <summary>
/// Bereinigungs-Engine mit strikten Pfadschutzregeln: gelöscht wird nur unterhalb fest definierter Orte,
/// Verknüpfungspunkte (Junctions/Symlinks) werden nie verfolgt, und zu allgemeine Wurzeln werden abgelehnt.
/// </summary>
public sealed class CleanupEngine
{
    private static readonly HashSet<string> ForbiddenRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)", "ProgramData", "Users", "Documents", "Dokumente", "Desktop",
        "Downloads", "Pictures", "Bilder", "Music", "Musik", "Videos", "AppData", "Local", "LocalLow", "Roaming", "OneDrive", "User Data", "Profiles",
    };

    private readonly ICleanupLocationProvider _locations;
    private readonly IRecycleBin? _recycleBin;
    private readonly TimeProvider _time;

    public CleanupEngine(ICleanupLocationProvider locations, IRecycleBin? recycleBin = null, TimeProvider? time = null)
    {
        _locations = locations;
        _recycleBin = recycleBin;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Prüft, ob eine Wurzel eng genug gefasst ist, um darin löschen zu dürfen.</summary>
    public static bool IsAcceptableRoot(string root, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) { reason = "Der Pfad ist nicht absolut."; return false; }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var segments = full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        // Unter Windows zählt das Laufwerk („C:“) als erstes Segment.
        var depth = segments.Length - (segments.Length > 0 && segments[0].EndsWith(':') ? 1 : 0);
        if (depth < 2) { reason = "Der Pfad liegt zu nah am Laufwerksstamm."; return false; }
        if (ForbiddenRootNames.Contains(segments[^1])) { reason = $"Der Ordner „{segments[^1]}“ darf nicht als Ganzes bereinigt werden."; return false; }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && string.Equals(full, Path.TrimEndingDirectorySeparator(profile), StringComparison.OrdinalIgnoreCase)) { reason = "Das Benutzerprofil darf nicht bereinigt werden."; return false; }
        return true;
    }

    private string? GetBlockedReason(CleanupCategory category)
    {
        var running = category.BlockingProcesses.FirstOrDefault(_locations.IsProcessRunning);
        return running is null ? null : $"„{running}“ wird gerade ausgeführt. Schließen Sie das Programm, um diese Kategorie zu bereinigen.";
    }

    public CleanupScanResult Scan(string categoryId, CancellationToken cancellationToken = default)
    {
        var category = CleanupCategories.Find(categoryId) ?? throw new ArgumentException($"Unbekannte Bereinigungskategorie '{categoryId}'.");

        if (categoryId == CleanupCategories.RecycleBin)
        {
            var info = _recycleBin?.Query();
            return info is null
                ? new CleanupScanResult(categoryId, 0, 0, new[] { "Papierkorb" }, 0, "Der Papierkorb konnte nicht abgefragt werden.")
                : new CleanupScanResult(categoryId, info.Value.Bytes, (int)Math.Min(int.MaxValue, info.Value.Items), new[] { "Papierkorb aller Laufwerke" }, 0);
        }

        long bytes = 0;
        int files = 0, inaccessible = 0;
        var shown = new List<string>();
        foreach (var location in _locations.GetLocations(categoryId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAcceptableRoot(location.Root, out _) || !Directory.Exists(location.Root)) continue;
            shown.Add(location.Pattern == "*" ? location.Root : Path.Combine(location.Root, location.Pattern));
            foreach (var file in EnumerateCandidates(location, ref inaccessible, cancellationToken))
            {
                try { bytes += file.Length; files++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { inaccessible++; }
            }
        }
        return new CleanupScanResult(categoryId, bytes, files, shown, inaccessible, GetBlockedReason(category));
    }

    public CleanupRunResult Run(string categoryId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var category = CleanupCategories.Find(categoryId) ?? throw new ArgumentException($"Unbekannte Bereinigungskategorie '{categoryId}'.");
        var blocked = GetBlockedReason(category);
        if (blocked is not null) return new CleanupRunResult(categoryId, 0, 0, 0, Array.Empty<string>(), blocked);

        if (categoryId == CleanupCategories.RecycleBin)
        {
            var before = _recycleBin?.Query();
            if (_recycleBin is null || before is null) return new CleanupRunResult(categoryId, 0, 0, 0, new[] { "Der Papierkorb konnte nicht abgefragt werden." });
            if (before.Value.Items == 0) return new CleanupRunResult(categoryId, 0, 0, 0, Array.Empty<string>());
            var ok = _recycleBin.Empty();
            var after = _recycleBin.Query() ?? (0, 0);
            return new CleanupRunResult(categoryId, Math.Max(0, before.Value.Bytes - after.Bytes), (int)Math.Max(0, before.Value.Items - after.Items), (int)after.Items,
                ok ? Array.Empty<string>() : new[] { "Der Papierkorb konnte nicht vollständig geleert werden." });
        }

        long freed = 0;
        int deleted = 0, skipped = 0, inaccessible = 0;
        var errors = new List<string>();
        foreach (var location in _locations.GetLocations(categoryId))
        {
            if (!IsAcceptableRoot(location.Root, out var reason))
            {
                errors.Add($"{location.Root}: {reason}");
                continue;
            }
            if (!Directory.Exists(location.Root)) continue;
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Root)) + Path.DirectorySeparatorChar;
            progress?.Report(location.Root);

            foreach (var file in EnumerateCandidates(location, ref inaccessible, cancellationToken).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Letzte Verteidigungslinie: Die Datei muss nachweislich unterhalb der Wurzel liegen.
                    if (!file.FullName.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                    var length = file.Length;
                    if (file.IsReadOnly) file.IsReadOnly = false;
                    file.Delete();
                    freed += length;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++; // gesperrt oder kein Zugriff
                }
            }

            if (location.Recursive) RemoveEmptyDirectories(new DirectoryInfo(location.Root), isRoot: true);
        }

        return new CleanupRunResult(categoryId, freed, deleted, skipped + inaccessible, errors);
    }

    private IEnumerable<FileInfo> EnumerateCandidates(CleanupLocation location, ref int inaccessible, CancellationToken cancellationToken)
    {
        var results = new List<FileInfo>();
        var cutoff = location.MinAge is { } age ? _time.GetUtcNow().UtcDateTime - age : (DateTime?)null;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(location.Root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in directory.EnumerateFiles(location.Pattern))
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is not null) continue;
                    if (cutoff is not null && file.LastWriteTimeUtc > cutoff) continue;
                    results.Add(file);
                }

                if (!location.Recursive) continue;
                foreach (var sub in directory.EnumerateDirectories())
                {
                    // Junctions und symbolische Verknüpfungen werden nie betreten.
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) || sub.LinkTarget is not null) continue;
                    pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                inaccessible++;
            }
        }
        return results;
    }

    private static void RemoveEmptyDirectories(DirectoryInfo directory, bool isRoot)
    {
        try
        {
            foreach (var sub in directory.EnumerateDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) || sub.LinkTarget is not null) continue;
                RemoveEmptyDirectories(sub, isRoot: false);
            }
            if (!isRoot && !directory.EnumerateFileSystemInfos().Any()) directory.Delete(recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht leere oder gesperrte Ordner bleiben bestehen.
        }
    }
}

/// <summary>Bereinigung als journalisierte, ausdrücklich nicht umkehrbare Aktion. Je Berechtigungsstufe existiert eine eigene Aktion.</summary>
public sealed class CleanupAction : OptimizationActionBase
{
    public const string UserActionId = "cleanup.run.user";
    public const string SystemActionId = "cleanup.run.system";

    private readonly CleanupEngine _engine;
    private readonly PrivilegeLevel _privilege;
    private IReadOnlyList<CleanupRunResult> _lastResults = Array.Empty<CleanupRunResult>();

    public override ActionMetadata Metadata { get; }

    public CleanupAction(CleanupEngine engine, PrivilegeLevel privilege)
    {
        _engine = engine;
        _privilege = privilege;
        var system = privilege == PrivilegeLevel.Administrator;
        Metadata = new ActionMetadata
        {
            Id = system ? SystemActionId : UserActionId,
            Name = system ? "Systemdateien bereinigen" : "Benutzerdateien bereinigen",
            Description = "Löscht die ausgewählten Kategorien temporärer Dateien und Caches endgültig.",
            Category = ActionCategory.Cleanup,
            Risk = RiskLevel.Low,
            RequiredPrivilege = privilege,
            IsReversible = false,
            ExcludeFromAutomation = true,
            ExpectedEffect = "Der von den gewählten Kategorien belegte Speicherplatz wird freigegeben.",
            SideEffects = "Gelöschte Dateien können nicht wiederhergestellt werden. Caches werden von den jeweiligen Programmen bei Bedarf neu aufgebaut.",
            BackupStrategy = "Keine Sicherung. Die Bereinigung ist nicht umkehrbar und wird deshalb nie automatisch ausgeführt.",
            RollbackStrategy = "Nicht umkehrbar.",
            VerificationStrategy = "Die tatsächlich gelöschten Dateien und freigegebenen Bytes werden gezählt; übersprungene Dateien werden ausgewiesen.",
            Parameters = new[]
            {
                new ParameterSpec("categories", ParameterType.ChoiceList, "Kommagetrennte Bereinigungskategorien", Choices: CleanupCategories.For(privilege).Select(c => c.Id).ToArray(), MaxLength: 512),
            },
            Tags = new[] { "cleanup" },
        };
    }

    public IReadOnlyList<CleanupRunResult> LastResults => _lastResults;

    private static string[] Categories(ActionParameters p) => p.RequireString("categories").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var scans = Categories(parameters).Select(c => _engine.Scan(c, cancellationToken)).ToList();
        return Task.FromResult(new ActionState { DisplayValue = $"{ByteFormat.Format(scans.Sum(s => s.Bytes))} in {scans.Sum(s => s.Files)} Dateien", IsApplied = scans.Sum(s => s.Files) == 0 });
    }

    public override async Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var scans = await Task.Run(() => Categories(parameters).Select(c => _engine.Scan(c, cancellationToken)).ToList(), cancellationToken).ConfigureAwait(false);
        var names = scans.Select(s => $"{CleanupCategories.Find(s.CategoryId)!.Name}: {ByteFormat.Format(s.Bytes)} ({s.Files} Dateien){(s.IsBlocked ? " – wird übersprungen: " + s.BlockedReason : "")}");
        return new ChangePreview
        {
            ActionId = Metadata.Id,
            Title = Metadata.Name,
            CurrentValue = $"{ByteFormat.Format(scans.Sum(s => s.Bytes))} belegt",
            NewValue = "Dateien endgültig gelöscht",
            WillChange = scans.Any(s => s.Files > 0 && !s.IsBlocked),
            Note = "Nicht umkehrbar. Gesperrte Dateien werden übersprungen.",
            TechnicalDetails = names.Concat(scans.SelectMany(s => s.Locations).Distinct()).ToList(),
        };
    }

    public override Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        return Task.FromResult(new BackupData(Metadata.Id, "{}", "Dateien vorhanden", "Dateien gelöscht"));
    }

    public override Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureValid(parameters);
        return Task.Run(() =>
        {
            var results = new List<CleanupRunResult>();
            foreach (var category in Categories(parameters))
            {
                progress?.Report(CleanupCategories.Find(category)!.Name);
                results.Add(_engine.Run(category, progress, CancellationToken.None));
            }
            _lastResults = results;

            var errors = results.SelectMany(r => r.Errors).ToList();
            var summary = $"{ByteFormat.Format(results.Sum(r => r.FreedBytes))} freigegeben, {results.Sum(r => r.DeletedFiles)} Dateien gelöscht, {results.Sum(r => r.SkippedFiles)} übersprungen.";
            var blocked = results.Where(r => r.BlockedReason is not null).Select(r => r.BlockedReason!).ToList();
            if (blocked.Count > 0) summary += " " + string.Join(" ", blocked);
            return errors.Count == 0 ? OperationResult.Ok(summary) : OperationResult.Fail(summary, string.Join(" | ", errors));
        }, CancellationToken.None);
    }

    public override Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        var freed = _lastResults.Sum(r => r.FreedBytes);
        var skipped = _lastResults.Sum(r => r.SkippedFiles);
        var ok = _lastResults.Count > 0 && _lastResults.All(r => r.Errors.Count == 0);
        var actual = $"{ByteFormat.Format(freed)} freigegeben, {skipped} Dateien übersprungen";
        return Task.FromResult(new VerificationResult(ok, "Bereinigung ohne Fehler", actual, ok ? "Die Bereinigung wurde abgeschlossen; die Zahlen stammen aus den tatsächlich gelöschten Dateien." : "Bei der Bereinigung sind Fehler aufgetreten."));
    }

    public override Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
        => Task.FromResult(NotReversible());
}

public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long bytes) => Format((double)bytes);

    public static string Format(double bytes)
    {
        var value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        var text = unit == 0 ? value.ToString("0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) : value.ToString(value >= 100 ? "0" : "0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        return (bytes < 0 ? "-" : "") + text + " " + Units[unit];
    }

    public static string Rate(double bytesPerSecond) => Format(bytesPerSecond) + "/s";
}
