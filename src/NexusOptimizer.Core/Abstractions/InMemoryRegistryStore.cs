namespace NexusOptimizer.Core.Abstractions;

/// <summary>
/// Registrierungsersatz im Arbeitsspeicher. Wird für Unit-Tests und für Trockenläufe verwendet –
/// niemals als Ersatz für echte Systemdaten in der Oberfläche.
/// </summary>
public sealed class InMemoryRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, RegistryValueData>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Simuliert „Zugriff verweigert“ für einen Schlüssel (Schreiben und Löschen).</summary>
    public void DenyWrites(RegistryRoot root, RegistryViewKind view, string keyPath) => _denied.Add(Key(root, view, keyPath));

    /// <summary>Wenn gesetzt, werden geschriebene Werte stillschweigend verfälscht – zum Testen fehlschlagender Verifikation.</summary>
    public Func<RegistryValueData, RegistryValueData>? WriteInterceptor { get; set; }

    public int WriteCount { get; private set; }

    private static string Key(RegistryRoot root, RegistryViewKind view, string keyPath) => $"{root}|{view}|{keyPath}";

    public bool KeyExists(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        lock (_gate)
        {
            var key = Key(root, view, keyPath);
            return _keys.ContainsKey(key) || _keys.Keys.Any(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase));
        }
    }

    public void CreateKey(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        lock (_gate) _keys.TryAdd(Key(root, view, keyPath), new Dictionary<string, RegistryValueData>(StringComparer.OrdinalIgnoreCase));
    }

    public RegistryValueData? ReadValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName)
    {
        lock (_gate)
            return _keys.TryGetValue(Key(root, view, keyPath), out var values) && values.TryGetValue(valueName, out var data) ? data : null;
    }

    public void WriteValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName, RegistryValueData data)
    {
        lock (_gate)
        {
            var key = Key(root, view, keyPath);
            if (_denied.Contains(key)) throw new UnauthorizedAccessException("Zugriff verweigert (simuliert).");
            if (!_keys.TryGetValue(key, out var values)) _keys[key] = values = new Dictionary<string, RegistryValueData>(StringComparer.OrdinalIgnoreCase);
            values[valueName] = WriteInterceptor is null ? data : WriteInterceptor(data);
            WriteCount++;
        }
    }

    public bool DeleteValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName)
    {
        lock (_gate)
        {
            var key = Key(root, view, keyPath);
            if (_denied.Contains(key)) throw new UnauthorizedAccessException("Zugriff verweigert (simuliert).");
            return _keys.TryGetValue(key, out var values) && values.Remove(valueName);
        }
    }

    public bool DeleteKeyIfEmpty(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        lock (_gate)
        {
            var key = Key(root, view, keyPath);
            if (!_keys.TryGetValue(key, out var values) || values.Count > 0) return false;
            if (_keys.Keys.Any(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase))) return false;
            return _keys.Remove(key);
        }
    }

    public IReadOnlyList<string> GetValueNames(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        lock (_gate)
            return _keys.TryGetValue(Key(root, view, keyPath), out var values) ? values.Keys.ToList() : Array.Empty<string>();
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        lock (_gate)
        {
            var prefix = Key(root, view, keyPath) + "\\";
            return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
