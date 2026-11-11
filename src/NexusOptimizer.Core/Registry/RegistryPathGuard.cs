using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Registry;

public sealed record RegistryTarget(RegistryRoot Root, RegistryViewKind View, string KeyPath, string ValueName)
{
    public string Display => $"{Root.ToShortName()}\\{KeyPath}\\{(ValueName.Length == 0 ? "(Standard)" : ValueName)}" + (View == RegistryViewKind.Registry32 ? " [32-Bit-Ansicht]" : "");
}

/// <summary>
/// Schutzschicht für Registrierungsziele. Der Tweak-Katalog kann dadurch – auch versehentlich –
/// niemals sicherheitskritische Schlüssel (Defender, Firewall, UAC, LSA, Secure Boot, VBS, Mitigations …) verändern.
/// </summary>
public static class RegistryPathGuard
{
    private static readonly string[] ForbiddenMachinePrefixes =
    {
        "SAM",
        "SECURITY",
        "BCD00000000",
        @"SYSTEM\CurrentControlSet\Control\Lsa",
        @"SYSTEM\CurrentControlSet\Control\SecureBoot",
        @"SYSTEM\CurrentControlSet\Control\DeviceGuard",
        @"SYSTEM\CurrentControlSet\Control\CI",
        @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
        @"SYSTEM\CurrentControlSet\Control\SecurityProviders",
        @"SYSTEM\CurrentControlSet\Services\WinDefend",
        @"SYSTEM\CurrentControlSet\Services\WdNisSvc",
        @"SYSTEM\CurrentControlSet\Services\Sense",
        @"SYSTEM\CurrentControlSet\Services\SecurityHealthService",
        @"SYSTEM\CurrentControlSet\Services\wscsvc",
        @"SYSTEM\CurrentControlSet\Services\mpssvc",
        @"SYSTEM\CurrentControlSet\Services\BFE",
        @"SYSTEM\CurrentControlSet\Services\wuauserv",
        @"SYSTEM\CurrentControlSet\Services\SharedAccess",
        @"SOFTWARE\Microsoft\Windows Defender",
        @"SOFTWARE\Policies\Microsoft\Windows Defender",
        @"SOFTWARE\Policies\Microsoft\Windows Defender Security Center",
        @"SOFTWARE\Policies\Microsoft\WindowsFirewall",
        @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit",
        @"SOFTWARE\Microsoft\Cryptography",
        @"SOFTWARE\Microsoft\SystemCertificates",
    };

    private static readonly string[] ForbiddenUserPrefixes =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\Microsoft\Windows\CurrentVersion\Policies\System",
        @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"Software\Microsoft\SystemCertificates",
        @"Software\Classes",
        @"Environment",
    };

    /// <summary>Wertnamen, die unabhängig vom Schlüssel niemals geschrieben werden dürfen.</summary>
    private static readonly HashSet<string> ForbiddenValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnableSmartScreen", "SmartScreenEnabled", "EnableLUA", "ConsentPromptBehaviorAdmin", "PromptOnSecureDesktop",
        "DisableAntiSpyware", "DisableAntiVirus", "DisableRealtimeMonitoring", "DisableBehaviorMonitoring", "TamperProtection",
        "EnableVirtualizationBasedSecurity", "HypervisorEnforcedCodeIntegrity", "RequirePlatformSecurityFeatures",
        "FeatureSettingsOverride", "FeatureSettingsOverrideMask", "DisableExceptionChainValidation", "MitigationOptions",
        "MitigationAuditOptions", "EnableFirewall", "NoAutoUpdate", "RunAsPPL", "Debugger", "Userinit", "Shell", "AppInit_DLLs",
        "LoadAppInit_DLLs", "DisableWindowsUpdateAccess", "LsaCfgFlags",
    };

    public static ValidationResult Validate(RegistryTarget target)
    {
        var errors = new List<string>();
        var path = target.KeyPath;

        if (string.IsNullOrWhiteSpace(path)) errors.Add("Der Registrierungspfad ist leer.");
        else
        {
            if (path.Length > 512) errors.Add("Der Registrierungspfad ist zu lang.");
            if (path.StartsWith('\\') || path.EndsWith('\\')) errors.Add("Der Registrierungspfad darf nicht mit einem Backslash beginnen oder enden.");
            if (path.Contains('/')) errors.Add("Der Registrierungspfad darf keine Schrägstriche enthalten.");
            foreach (var segment in path.Split('\\'))
            {
                if (segment.Length == 0) { errors.Add("Der Registrierungspfad enthält leere Segmente."); break; }
                if (segment.Length > 255) { errors.Add("Ein Segment des Registrierungspfads ist zu lang."); break; }
                if (segment.Any(char.IsControl) || segment.Contains('*') || segment.Contains('?')) { errors.Add("Der Registrierungspfad enthält unzulässige Zeichen."); break; }
            }
            if (path.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) || path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) || path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase))
                errors.Add("Der Registrierungspfad darf den Stammschlüssel nicht enthalten.");
        }

        if (target.ValueName is null || target.ValueName.Length > 16383 || target.ValueName.Any(char.IsControl))
            errors.Add("Der Wertname ist ungültig.");

        if (errors.Count == 0 && IsProtected(target, out var reason)) errors.Add(reason);

        return errors.Count == 0 ? ValidationResult.Ok : new ValidationResult(false, errors);
    }

    public static bool IsProtected(RegistryTarget target, out string reason)
    {
        var prefixes = target.Root == RegistryRoot.LocalMachine ? ForbiddenMachinePrefixes : ForbiddenUserPrefixes;
        foreach (var prefix in prefixes)
        {
            if (IsSameOrChild(target.KeyPath, prefix))
            {
                reason = $"Der Schlüssel '{target.Root.ToShortName()}\\{prefix}' ist sicherheitskritisch und für Tweaks gesperrt.";
                return true;
            }
        }

        if (ForbiddenValueNames.Contains(target.ValueName))
        {
            reason = $"Der Wert '{target.ValueName}' steuert eine Sicherheitsfunktion und ist für Tweaks gesperrt.";
            return true;
        }

        reason = "";
        return false;
    }

    private static bool IsSameOrChild(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path[prefix.Length] == '\\');
}
