using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using NexusOptimizer.Core.Gaming;

namespace NexusOptimizer.Windows.Gaming;

/// <summary>
/// Erkennt installierte Spiele anhand der Installationsmetadaten der Plattformen (Bibliotheksmanifeste und Registrierung).
/// Es werden keine Kontodaten, Anmelde-Token oder geschützten Anwendungsdaten gelesen.
/// </summary>
public sealed class WindowsGameLibraryScanner : IGameLibraryScanner
{
    private readonly ILogger _logger;

    public WindowsGameLibraryScanner(ILogger<WindowsGameLibraryScanner>? logger = null) => _logger = logger ?? NullLogger<WindowsGameLibraryScanner>.Instance;

    public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<GameInfo>>(() =>
        {
            var games = new List<GameInfo>();
            foreach (var scan in new Func<IEnumerable<GameInfo>>[] { ScanSteam, ScanEpic, ScanGog, ScanUbisoft, ScanEa, ScanBattleNet, ScanWindowsGameStore })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { games.AddRange(scan()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException)
                {
                    _logger.LogDebug(ex, "Spielbibliothek konnte nicht gelesen werden.");
                }
            }
            return games.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, cancellationToken);

    private static string? Read(RegistryHive hive, RegistryView view, string path, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static IEnumerable<(string Name, RegistryKey Key)> SubKeys(RegistryHive hive, RegistryView view, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(path);
        if (key is null) yield break;
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub is not null) yield return (name, sub);
        }
    }

    /// <summary>Sucht im Installationsordner nach wahrscheinlichen Spiel-Programmdateien (größte .exe-Dateien, ohne Hilfsprogramme).</summary>
    private static IReadOnlyList<string> FindExecutables(string? directory, int max = 3)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return Array.Empty<string>();
        string[] ignore = { "unins", "setup", "install", "crash", "report", "redist", "vcredist", "dxsetup", "launcher", "updater", "helper", "anticheat", "easyanticheat", "battleye", "ue4prereq", "dotnet", "cefprocess", "webhelper" };
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => !ignore.Any(i => f.Name.Contains(i, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.Length).Take(max).Select(f => f.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private IEnumerable<GameInfo> ScanSteam()
    {
        var steamPath = Read(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath")
                        ?? Read(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (string.IsNullOrEmpty(steamPath)) yield break;
        steamPath = steamPath.Replace('/', '\\');

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamPath };
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            try { foreach (var folder in GameManifestParsers.ParseSteamLibraryFolders(File.ReadAllText(libraryFile))) libraries.Add(folder.Replace('/', '\\')); }
            catch (FormatException ex) { _logger.LogDebug(ex, "libraryfolders.vdf ist nicht lesbar."); }
        }

        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                GameInfo? game = null;
                try
                {
                    // Manifestdateien sind klein; eine Größenbegrenzung schützt vor fehlerhaften Dateien.
                    if (new FileInfo(manifest).Length > 1024 * 1024) continue;
                    game = GameManifestParsers.ParseSteamAppManifest(File.ReadAllText(manifest), steamApps);
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { }
                if (game is not null) yield return game with { Executables = FindExecutables(game.InstallDirectory) };
            }
        }
    }

    private IEnumerable<GameInfo> ScanEpic()
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) yield break;
        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            GameInfo? game = null;
            try
            {
                if (new FileInfo(file).Length > 1024 * 1024) continue;
                game = GameManifestParsers.ParseEpicManifest(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (game is null) continue;
            yield return game.Executables.Count > 0 && game.Executables.All(File.Exists) ? game : game with { Executables = FindExecutables(game.InstallDirectory) };
        }
    }

    private IEnumerable<GameInfo> ScanGog()
    {
        foreach (var (id, key) in SubKeys(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\GOG.com\Games"))
        {
            var name = key.GetValue("gameName") as string;
            var path = key.GetValue("path") as string;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
            if (key.GetValue("dependsOn") is string { Length: > 0 }) continue; // Zusatzinhalt
            var exe = key.GetValue("exe") as string;
            yield return new GameInfo
            {
                Id = GameInfo.MakeId(GamePlatform.Gog, id), Name = name, Platform = GamePlatform.Gog, PlatformAppId = id, InstallDirectory = path,
                Executables = !string.IsNullOrEmpty(exe) && File.Exists(exe) ? new[] { exe } : FindExecutables(path),
            };
        }
    }

    private IEnumerable<GameInfo> ScanUbisoft()
    {
        foreach (var (id, key) in SubKeys(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher\Installs"))
        {
            var path = (key.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            yield return new GameInfo
            {
                Id = GameInfo.MakeId(GamePlatform.Ubisoft, id), Name = Path.GetFileName(path), Platform = GamePlatform.Ubisoft, PlatformAppId = id, InstallDirectory = path,
                Executables = FindExecutables(path),
            };
        }
    }

    private IEnumerable<GameInfo> ScanEa()
    {
        foreach (var root in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Origin Games" })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                foreach (var (id, key) in SubKeys(RegistryHive.LocalMachine, view, root))
                {
                    var path = ((key.GetValue("Install Dir") ?? key.GetValue("InstallDir")) as string)?.TrimEnd('\\');
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                    var name = key.GetValue("DisplayName") as string ?? id;
                    yield return new GameInfo
                    {
                        Id = GameInfo.MakeId(GamePlatform.EaApp, id), Name = name, Platform = GamePlatform.EaApp, PlatformAppId = id, InstallDirectory = path,
                        Executables = FindExecutables(path),
                    };
                }
            }
        }
    }

    /// <summary>Battle.net-Spiele erscheinen in der Liste installierter Programme mit Blizzard als Herausgeber.</summary>
    private IEnumerable<GameInfo> ScanBattleNet()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            foreach (var (id, key) in SubKeys(RegistryHive.LocalMachine, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                var publisher = key.GetValue("Publisher") as string ?? "";
                if (!publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) continue;
                var name = key.GetValue("DisplayName") as string;
                var path = (key.GetValue("InstallLocation") as string)?.TrimEnd('\\');
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                if (name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) continue; // der Launcher selbst
                yield return new GameInfo
                {
                    Id = GameInfo.MakeId(GamePlatform.BattleNet, id), Name = name, Platform = GamePlatform.BattleNet, PlatformAppId = id, InstallDirectory = path,
                    Executables = FindExecutables(path),
                };
            }
        }
    }

    /// <summary>Spiele, die Windows selbst als Spiel erkannt hat (Game-Bar-Konfiguration) – deckt u. a. Xbox-/Store-Titel und manuell markierte Programme ab.</summary>
    private IEnumerable<GameInfo> ScanWindowsGameStore()
    {
        foreach (var (id, key) in SubKeys(RegistryHive.CurrentUser, RegistryView.Registry64, @"System\GameConfigStore\Children"))
        {
            var exe = key.GetValue("MatchedExeFullPath") as string;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
            string name;
            try
            {
                var version = FileVersionInfo.GetVersionInfo(exe);
                name = !string.IsNullOrWhiteSpace(version.ProductName) ? version.ProductName.Trim() : !string.IsNullOrWhiteSpace(version.FileDescription) ? version.FileDescription.Trim() : Path.GetFileNameWithoutExtension(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { name = Path.GetFileNameWithoutExtension(exe); }
            yield return new GameInfo
            {
                Id = GameInfo.MakeId(GamePlatform.Xbox, exe), Name = name, Platform = GamePlatform.Xbox, PlatformAppId = id, InstallDirectory = Path.GetDirectoryName(exe),
                Executables = new[] { exe },
            };
        }
    }
}

/// <summary>
/// Optionale Frametime-Aufzeichnung über das dokumentierte Intel-Werkzeug PresentMon (Konsolenanwendung, CSV-Ausgabe).
/// Der Pfad zur PresentMon-Programmdatei wird vom Benutzer in den Einstellungen festgelegt; ohne ihn bleiben FPS-Werte „nicht verfügbar“.
/// </summary>
public sealed class PresentMonFrameCapture : IFrameCaptureProvider
{
    private readonly Func<string?> _executablePath;
    private Process? _process;
    private string? _outputFile;

    public PresentMonFrameCapture(Func<string?> executablePath) => _executablePath = executablePath;

    public bool IsAvailable => _executablePath() is { Length: > 0 } path && File.Exists(path) && Path.GetFileName(path).StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase);

    public string UnavailableReason => _executablePath() is not { Length: > 0 }
        ? "Für FPS- und Frametime-Messungen ist PresentMon erforderlich. Der Pfad kann in den Einstellungen hinterlegt werden."
        : "Die angegebene PresentMon-Programmdatei wurde nicht gefunden.";

    public Task StartAsync(int processId, string processName, CancellationToken cancellationToken)
    {
        if (!IsAvailable) return Task.CompletedTask;
        _outputFile = Path.Combine(Path.GetTempPath(), $"nexus-presentmon-{Guid.NewGuid():N}.csv");
        var start = new ProcessStartInfo
        {
            FileName = _executablePath()!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "--process_id", processId.ToString(CultureInfo.InvariantCulture), "--output_file", _outputFile, "--terminate_on_proc_exit", "--stop_existing_session", "--no_console_stats" })
            start.ArgumentList.Add(argument);
        try
        {
            _process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _process = null;
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<double>> StopAsync()
    {
        var process = _process;
        var file = _outputFile;
        _process = null;
        _outputFile = null;
        if (process is null || file is null) return Array.Empty<double>();

        try
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } }
                }
            }
            return File.Exists(file) ? ParseCsv(File.ReadLines(file, Encoding.UTF8)) : Array.Empty<double>();
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Liest die Frametime-Spalte („MsBetweenPresents“ bzw. „FrameTime“ in neueren Versionen) aus der PresentMon-CSV.</summary>
    internal static IReadOnlyList<double> ParseCsv(IEnumerable<string> lines)
    {
        var values = new List<double>();
        var column = -1;
        foreach (var line in lines)
        {
            var cells = line.Split(',');
            if (column < 0)
            {
                column = Array.FindIndex(cells, c => c.Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase) || c.Trim().Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
                if (column < 0) return values; // unbekanntes Format → lieber keine Werte als falsche
                continue;
            }
            if (cells.Length > column && double.TryParse(cells[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0) values.Add(value);
        }
        return values;
    }
}
