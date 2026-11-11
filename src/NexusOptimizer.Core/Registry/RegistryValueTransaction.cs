using System.Text.Json;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Registry;

/// <summary>Gesicherter Ursprungszustand eines einzelnen Registrierungswerts samt beabsichtigtem Zielzustand.</summary>
public sealed record RegistryBackupPayload
{
    public int Version { get; init; } = 1;
    public bool KeyExisted { get; init; }
    public bool ValueExisted { get; init; }
    public RegistryValueType? OriginalType { get; init; }
    public string? OriginalData { get; init; }
    public RegistryValueType IntendedType { get; init; }
    public string IntendedData { get; init; } = "";

    public RegistryValueData? Original => ValueExisted && OriginalType is not null && OriginalData is not null ? new RegistryValueData(OriginalType.Value, OriginalData) : null;
    public RegistryValueData Intended => new(IntendedType, IntendedData);
}

/// <summary>
/// Transaktionslogik für genau einen Registrierungswert (Abschnitt 16.2): Zustand erfassen, schreiben,
/// zurücklesen und zurückrollen. Unterscheidet beim Rollback zwischen „alten Wert wiederherstellen“
/// und „Wert löschen, der ursprünglich nicht existierte“.
/// </summary>
public static class RegistryValueTransaction
{
    private const int MaxDataLength = 64 * 1024;

    public static RegistryBackupPayload Capture(IRegistryStore store, RegistryTarget target, RegistryValueData intended)
    {
        var keyExisted = store.KeyExists(target.Root, target.View, target.KeyPath);
        var current = keyExisted ? store.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName) : null;
        return new RegistryBackupPayload
        {
            KeyExisted = keyExisted,
            ValueExisted = current is not null,
            OriginalType = current?.Type,
            OriginalData = current?.Data,
            IntendedType = intended.Type,
            IntendedData = intended.Data,
        };
    }

    public static string Serialize(RegistryBackupPayload payload) => JsonSerializer.Serialize(payload);

    /// <summary>Liest und validiert eine Sicherung. Der Speicherort stammt nie aus der Sicherung, nur die Daten.</summary>
    public static RegistryBackupPayload Parse(string json)
    {
        RegistryBackupPayload? payload;
        try { payload = JsonSerializer.Deserialize<RegistryBackupPayload>(json); }
        catch (JsonException ex) { throw new FormatException("Die Sicherungsdaten sind beschädigt.", ex); }

        if (payload is null || payload.Version != 1) throw new FormatException("Die Sicherungsdaten haben ein unbekanntes Format.");
        if (payload.IntendedData is null || payload.IntendedData.Length > MaxDataLength || !payload.Intended.IsWellFormed())
            throw new FormatException("Die Sicherungsdaten enthalten einen ungültigen Zielwert.");
        if (payload.ValueExisted)
        {
            if (payload.OriginalType is null || payload.OriginalData is null || payload.OriginalData.Length > MaxDataLength || !payload.Original!.IsWellFormed())
                throw new FormatException("Die Sicherungsdaten enthalten einen ungültigen Ursprungswert.");
        }
        return payload;
    }

    public static VerificationResult Verify(IRegistryStore store, RegistryTarget target, RegistryValueData intended)
    {
        var actual = store.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName);
        var expectedText = Describe(intended);
        if (actual is null) return VerificationResult.Failure(expectedText, "Wert nicht vorhanden");
        return ValuesEqual(actual, intended)
            ? VerificationResult.Success(expectedText, Describe(actual))
            : VerificationResult.Failure(expectedText, Describe(actual));
    }

    public static RollbackResult Rollback(IRegistryStore store, RegistryTarget target, RegistryBackupPayload payload, bool force)
    {
        var current = store.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName);
        var original = payload.Original;

        // Bereits im Ursprungszustand → nichts zu tun.
        if ((original is null && current is null) || (original is not null && current is not null && ValuesEqual(original, current)))
            return RollbackResult.Ok("Der Ursprungszustand liegt bereits vor.");

        // Konflikterkennung: Wurde der Wert seit unserer Änderung von anderer Seite verändert?
        if (!force && (current is null || !ValuesEqual(current, payload.Intended)))
        {
            return RollbackResult.Conflict(
                $"Der Wert wurde seit der Optimierung erneut geändert (aktuell: {(current is null ? "nicht vorhanden" : Describe(current))}, " +
                $"von dieser Optimierung gesetzt: {Describe(payload.Intended)}). Wiederherstellung nur nach ausdrücklicher Bestätigung.");
        }

        if (original is not null)
        {
            store.WriteValue(target.Root, target.View, target.KeyPath, target.ValueName, original);
            var check = store.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName);
            return check is not null && ValuesEqual(check, original)
                ? RollbackResult.Ok($"Vorheriger Wert {Describe(original)} wiederhergestellt.")
                : RollbackResult.Fail("Der vorherige Wert konnte nicht bestätigt wiederhergestellt werden.");
        }

        store.DeleteValue(target.Root, target.View, target.KeyPath, target.ValueName);
        if (store.ReadValue(target.Root, target.View, target.KeyPath, target.ValueName) is not null)
            return RollbackResult.Fail("Der ursprünglich nicht vorhandene Wert konnte nicht entfernt werden.");
        if (!payload.KeyExisted) store.DeleteKeyIfEmpty(target.Root, target.View, target.KeyPath);
        return RollbackResult.Ok("Der Wert existierte ursprünglich nicht und wurde wieder entfernt.");
    }

    public static bool ValuesEqual(RegistryValueData a, RegistryValueData b)
    {
        if (a.Type != b.Type)
        {
            // REG_SZ und REG_EXPAND_SZ mit gleichem Text gelten als gleichwertig für Vergleiche.
            var bothText = a.Type is RegistryValueType.String or RegistryValueType.ExpandString && b.Type is RegistryValueType.String or RegistryValueType.ExpandString;
            if (!bothText) return false;
        }
        return a.Type == RegistryValueType.Binary
            ? string.Equals(a.Data, b.Data, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Data, b.Data, StringComparison.Ordinal);
    }

    public static string Describe(RegistryValueData value) => $"{value.ToDisplay()} ({RegistryValueData.TypeName(value.Type)})";
}
