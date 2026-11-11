using Microsoft.Win32;
using NexusOptimizer.Core.Abstractions;

namespace NexusOptimizer.Windows.RegistryAccess;

/// <summary>
/// Zugriff auf die echte Windows-Registrierung über die dokumentierte Microsoft.Win32-API.
/// Die 32-/64-Bit-Ansicht wird bei jedem Zugriff explizit gewählt.
/// </summary>
public sealed class WindowsRegistryStore : IRegistryStore
{
    private static RegistryKey OpenBase(RegistryRoot root, RegistryViewKind view)
        => RegistryKey.OpenBaseKey(
            root == RegistryRoot.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view == RegistryViewKind.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64);

    public bool KeyExists(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        using var baseKey = OpenBase(root, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        return key is not null;
    }

    public RegistryValueData? ReadValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName)
    {
        using var baseKey = OpenBase(root, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        if (key is null) return null;

        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;

        return key.GetValueKind(valueName) switch
        {
            RegistryValueKind.DWord => RegistryValueData.DWord(unchecked((uint)(int)value)),
            RegistryValueKind.QWord => RegistryValueData.QWord(unchecked((ulong)(long)value)),
            RegistryValueKind.String => new RegistryValueData(RegistryValueType.String, (string)value),
            RegistryValueKind.ExpandString => new RegistryValueData(RegistryValueType.ExpandString, (string)value),
            RegistryValueKind.MultiString => RegistryValueData.Multi((string[])value),
            RegistryValueKind.Binary => RegistryValueData.Binary((byte[])value),
            // REG_NONE und unbekannte Typen werden als Binärdaten erhalten, damit ein Rollback nichts verliert.
            _ => value is byte[] bytes ? RegistryValueData.Binary(bytes) : new RegistryValueData(RegistryValueType.String, value.ToString() ?? ""),
        };
    }

    public void WriteValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName, RegistryValueData data)
    {
        if (!data.IsWellFormed()) throw new ArgumentException("Die Registrierungsdaten passen nicht zum angegebenen Typ.");

        using var baseKey = OpenBase(root, view);
        using var key = baseKey.CreateSubKey(keyPath, writable: true)
                        ?? throw new UnauthorizedAccessException($"Der Schlüssel '{keyPath}' konnte nicht geöffnet werden.");

        switch (data.Type)
        {
            case RegistryValueType.DWord:
                key.SetValue(valueName, unchecked((int)data.AsDWord()!.Value), RegistryValueKind.DWord);
                break;
            case RegistryValueType.QWord:
                key.SetValue(valueName, unchecked((long)data.AsQWord()!.Value), RegistryValueKind.QWord);
                break;
            case RegistryValueType.String:
                key.SetValue(valueName, data.Data, RegistryValueKind.String);
                break;
            case RegistryValueType.ExpandString:
                key.SetValue(valueName, data.Data, RegistryValueKind.ExpandString);
                break;
            case RegistryValueType.MultiString:
                key.SetValue(valueName, data.AsMulti()!, RegistryValueKind.MultiString);
                break;
            case RegistryValueType.Binary:
                key.SetValue(valueName, data.AsBinary()!, RegistryValueKind.Binary);
                break;
            default:
                throw new NotSupportedException($"Registrierungstyp {data.Type} wird nicht unterstützt.");
        }
    }

    public bool DeleteValue(RegistryRoot root, RegistryViewKind view, string keyPath, string valueName)
    {
        using var baseKey = OpenBase(root, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: true);
        if (key is null || key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null) return false;
        key.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    public bool DeleteKeyIfEmpty(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        using var baseKey = OpenBase(root, view);
        using (var key = baseKey.OpenSubKey(keyPath, writable: false))
        {
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return false;
        }
        baseKey.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
        return true;
    }

    public IReadOnlyList<string> GetValueNames(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        using var baseKey = OpenBase(root, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, RegistryViewKind view, string keyPath)
    {
        using var baseKey = OpenBase(root, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }
}
