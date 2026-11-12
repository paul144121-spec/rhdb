using System.Text;
using System.Text.Json;

namespace NexusOptimizer.Core.Gaming;

public enum GamePlatform
{
    Steam,
    EpicGames,
    Gog,
    Ubisoft,
    EaApp,
    BattleNet,
    Xbox,
    Manual,
}

public sealed record GameInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public GamePlatform Platform { get; init; }
    public string? InstallDirectory { get; init; }
    /// <summary>Bekannte ausführbare Dateien des Spiels (vollständige Pfade). Kann leer sein, wenn die Plattform sie nicht nennt.</summary>
    public IReadOnlyList<string> Executables { get; init; } = Array.Empty<string>();
    public string? PlatformAppId { get; init; }

    public static string MakeId(GamePlatform platform, string key) => $"{platform.ToString().ToLowerInvariant()}:{key.ToLowerInvariant()}";
    public string PlatformName => Platform switch
    {
        GamePlatform.EpicGames => "Epic Games",
        GamePlatform.Gog => "GOG",
        GamePlatform.Ubisoft => "Ubisoft Connect",
        GamePlatform.EaApp => "EA app",
        GamePlatform.BattleNet => "Battle.net",
        GamePlatform.Xbox => "Xbox / Windows",
        GamePlatform.Manual => "Manuell hinzugefügt",
        _ => Platform.ToString(),
    };
}

/// <summary>Parser für das „KeyValues“-Textformat von Valve (libraryfolders.vdf, appmanifest_*.acf).</summary>
public sealed class VdfNode
{
    public string? Value { get; init; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VdfNode? this[string key] => Children.GetValueOrDefault(key);
    public string? GetString(string key) => Children.GetValueOrDefault(key)?.Value;

    public static VdfNode Parse(string text)
    {
        var position = 0;
        var root = new VdfNode();
        ParseObject(text, ref position, root, depth: 0, topLevel: true);
        return root;
    }

    private static void ParseObject(string text, ref int position, VdfNode node, int depth, bool topLevel)
    {
        if (depth > 32) throw new FormatException("VDF-Datei ist zu tief verschachtelt.");
        while (true)
        {
            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length)
            {
                if (!topLevel) throw new FormatException("Unerwartetes Ende der VDF-Datei.");
                return;
            }
            if (text[position] == '}')
            {
                position++;
                if (topLevel) throw new FormatException("Unerwartete schließende Klammer in der VDF-Datei.");
                return;
            }

            var key = ReadToken(text, ref position);
            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length) throw new FormatException("Unerwartetes Ende der VDF-Datei nach einem Schlüssel.");

            if (text[position] == '{')
            {
                position++;
                var child = new VdfNode();
                ParseObject(text, ref position, child, depth + 1, topLevel: false);
                node.Children[key] = child;
            }
            else
            {
                node.Children[key] = new VdfNode { Value = ReadToken(text, ref position) };
            }
        }
    }

    private static void SkipWhitespaceAndComments(string text, ref int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position])) position++;
            else if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
            }
            else break;
        }
    }

    private static string ReadToken(string text, ref int position)
    {
        var builder = new StringBuilder();
        if (text[position] == '"')
        {
            position++;
            while (position < text.Length && text[position] != '"')
            {
                if (text[position] == '\\' && position + 1 < text.Length)
                {
                    position++;
                    builder.Append(text[position] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', var c => c });
                }
                else builder.Append(text[position]);
                position++;
            }
            if (position >= text.Length) throw new FormatException("Nicht abgeschlossene Zeichenkette in der VDF-Datei.");
            position++;
        }
        else
        {
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"')) builder.Append(text[position++]);
            if (builder.Length == 0) throw new FormatException("Unerwartetes Zeichen in der VDF-Datei.");
        }
        return builder.ToString();
    }
}

public static class GameManifestParsers
{
    /// <summary>Liest die Bibliotheksordner aus Steams libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> ParseSteamLibraryFolders(string vdfText)
    {
        var root = VdfNode.Parse(vdfText);
        var folders = root["libraryfolders"] ?? root["LibraryFolders"];
        if (folders is null) return Array.Empty<string>();

        var result = new List<string>();
        foreach (var (key, node) in folders.Children)
        {
            if (!int.TryParse(key, out _)) continue;
            // Neues Format: Unterobjekt mit "path"; altes Format: direkter Zeichenkettenwert.
            var path = node.GetString("path") ?? node.Value;
            if (!string.IsNullOrWhiteSpace(path)) result.Add(path);
        }
        return result;
    }

    /// <summary>Liest ein Steam-App-Manifest (appmanifest_&lt;id&gt;.acf).</summary>
    public static GameInfo? ParseSteamAppManifest(string acfText, string steamAppsDirectory)
    {
        var state = VdfNode.Parse(acfText)["AppState"];
        var appId = state?.GetString("appid");
        var name = state?.GetString("name");
        var installDir = state?.GetString("installdir");
        if (state is null || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) return null;
        if (installDir.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || installDir is "." or "..") return null;
        if (IsSteamTool(name)) return null;

        return new GameInfo
        {
            Id = GameInfo.MakeId(GamePlatform.Steam, appId),
            Name = name,
            Platform = GamePlatform.Steam,
            PlatformAppId = appId,
            InstallDirectory = Path.Combine(steamAppsDirectory, "common", installDir),
        };
    }

    private static bool IsSteamTool(string name)
        => name.StartsWith("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
           || name.Equals("SteamVR", StringComparison.OrdinalIgnoreCase);

    /// <summary>Liest ein Manifest des Epic Games Launcher (*.item, JSON).</summary>
    public static GameInfo? ParseEpicManifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Get(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            var displayName = Get("DisplayName");
            var location = Get("InstallLocation");
            var appName = Get("AppName");
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(appName)) return null;
            // Zusatzinhalte und Engine-Installationen sind keine eigenständigen Spiele.
            if (root.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) return null;
            if (appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) return null;

            var executables = new List<string>();
            var launch = Get("LaunchExecutable");
            if (!string.IsNullOrWhiteSpace(launch) && !launch.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(launch))
                executables.Add(Path.Combine(location, launch.Replace('/', Path.DirectorySeparatorChar)));

            return new GameInfo
            {
                Id = GameInfo.MakeId(GamePlatform.EpicGames, appName),
                Name = displayName,
                Platform = GamePlatform.EpicGames,
                PlatformAppId = appName,
                InstallDirectory = location,
                Executables = executables,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Durchsucht die Spielebibliotheken. Greift nur auf Installationsmetadaten zu – nie auf Kontodaten oder Zugangstoken.</summary>
public interface IGameLibraryScanner
{
    Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken = default);
}
