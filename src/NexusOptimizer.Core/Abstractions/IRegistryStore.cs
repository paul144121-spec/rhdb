using System.Globalization;
using System.Text.Json;

namespace NexusOptimizer.Core.Abstractions;

public enum RegistryRoot
{
    CurrentUser,
    LocalMachine,
}

/// <summary>Registrierungsansicht. Auf 64-Bit-Windows liegen 32-Bit-Werte unter WOW6432Node – daher immer explizit angeben.</summary>
public enum RegistryViewKind
{
    Registry64,
    Registry32,
}

public enum RegistryValueType
{
    DWord,
    QWord,
    String,
    ExpandString,
    MultiString,
    Binary,
}

/// <summary>
/// Typ und Daten eines Registrierungswerts in kanonischer Textform:
/// DWord/QWord dezimal, String/ExpandString unverändert, MultiString als JSON-Array, Binary als Hex ohne Trennzeichen.
/// </summary>
public sealed record RegistryValueData(RegistryValueType Type, string Data)
{
    public static RegistryValueData DWord(uint value) => new(RegistryValueType.DWord, value.ToString(CultureInfo.InvariantCulture));
    public static RegistryValueData QWord(ulong value) => new(RegistryValueType.QWord, value.ToString(CultureInfo.InvariantCulture));
    public static RegistryValueData Text(string value) => new(RegistryValueType.String, value);
    public static RegistryValueData Binary(byte[] value) => new(RegistryValueType.Binary, Convert.ToHexString(value));
    public static RegistryValueData Multi(IEnumerable<string> values) => new(RegistryValueType.MultiString, JsonSerializer.Serialize(values.ToArray()));

    public uint? AsDWord() => Type == RegistryValueType.DWord && uint.TryParse(Data, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    public ulong? AsQWord() => Type == RegistryValueType.QWord && ulong.TryParse(Data, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    public byte[]? AsBinary() => Type == RegistryValueType.Binary ? TryFromHex(Data) : null;
    public string[]? AsMulti()
    {
        if (Type != RegistryValueType.MultiString) return null;
        try { return JsonSerializer.Deserialize<string[]>(Data); } catch (JsonException) { return null; }
    }

    /// <summary>Prüft, ob die Textform zum deklarierten Typ passt.</summary>
    public bool IsWellFormed() => Type switch
    {
        RegistryValueType.DWord => AsDWord() is not null,
        RegistryValueType.QWord => AsQWord() is not null,
        RegistryValueType.Binary => AsBinary() is not null,
        RegistryValueType.MultiString => AsMulti() is not null,
        _ => Data is not null && Data.IndexOf('\0') < 0,
    };

    public string ToDisplay() => Type switch
    {
        RegistryValueType.DWord or RegistryValueType.QWord => Data,
        RegistryValueType.Binary => Data.Length > 32 ? Data[..32] + "…" : Data,
        _ => $"\"{Data}\"",
    };

    public static string TypeName(RegistryValueType type) => type switch
    {
        RegistryValueType.DWord => "REG_DWORD",
        RegistryValueType.QWord => "REG_QWORD",
        RegistryValueType.String => "REG_SZ",
        RegistryValueType.ExpandString => "REG_EXPAND_SZ",
        RegistryValueType.MultiString => "REG_MULTI_SZ",
        RegistryValueType.Binary => "REG_BINARY",
        _ => type.ToString(),
    };

    private static byte[]? TryFromHex(string hex)
    {
        try { return Convert.FromHexString(hex); } catch (FormatException) { return null; }
    }
}

/// <summary>Abstraktion der Windows-Registrierung, damit Transaktions- und Rollback-Logik ohne echte Registrierung testbar ist.</summary>
public interface IRegistryStore
{
    bool KeyExists(RegistryRoot root, RegistryViewKind view, string keyPath);
    /// <summary>Liefert null, wenn Schlüssel oder Wert nicht existieren.</summary>
    RegistryValueData? ReadValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName);
    /// <summary>Schreibt den Wert und legt den Schlüssel bei Bedarf an.</summary>
    void WriteValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName, RegistryValueData data);
    /// <summary>Löscht den Wert; liefert false, wenn er nicht existierte.</summary>
    bool DeleteValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName);
    /// <summary>Löscht den Schlüssel nur, wenn er weder Werte noch Unterschlüssel enthält.</summary>
    bool DeleteKeyIfEmpty(RegistryRoot root, RegistryViewKind view, string keyPath);
    IReadOnlyList<string> GetValueNames(RegistryRoot root, RegistryViewKind view, string keyPath);
    IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, RegistryViewKind view, string keyPath);
}

public static class RegistryRootExtensions
{
    public static string ToShortName(this RegistryRoot root) => root == RegistryRoot.CurrentUser ? "HKCU" : "HKLM";
}
