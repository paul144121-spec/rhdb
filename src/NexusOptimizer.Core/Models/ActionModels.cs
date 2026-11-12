using System.Text.Json;

namespace NexusOptimizer.Core.Models;

/// <summary>Unterstützter Bereich von Windows-Buildnummern (10240 = erstes Windows 10, 22000 = erstes Windows 11).</summary>
public sealed record WindowsVersionRange(int MinBuild = WindowsVersionRange.Windows10FirstBuild, int? MaxBuild = null)
{
    public const int Windows10FirstBuild = 10240;
    public const int Windows10_1809 = 17763;
    public const int Windows10_2004 = 19041;
    public const int Windows11FirstBuild = 22000;
    public const int Windows11_22H2 = 22621;

    public static WindowsVersionRange All { get; } = new();
    public static WindowsVersionRange Windows10Only { get; } = new(Windows10FirstBuild, Windows11FirstBuild - 1);
    public static WindowsVersionRange Windows11Only { get; } = new(Windows11FirstBuild);

    public bool Includes(int build) => build >= MinBuild && (MaxBuild is null || build <= MaxBuild);

    public string Describe()
    {
        if (MinBuild <= Windows10FirstBuild && MaxBuild is null) return "Windows 10 und Windows 11";
        if (MinBuild >= Windows11FirstBuild && MaxBuild is null)
            return MinBuild == Windows11FirstBuild ? "Windows 11" : $"Windows 11 ab Build {MinBuild}";
        if (MaxBuild is not null && MaxBuild < Windows11FirstBuild)
            return MinBuild <= Windows10FirstBuild ? "Nur Windows 10" : $"Windows 10 ab Build {MinBuild}";
        if (MaxBuild is null) return $"Windows 10 ab Build {MinBuild} und Windows 11";
        return $"Build {MinBuild} bis {MaxBuild}";
    }
}

public enum ParameterType
{
    Integer,
    Boolean,
    Text,
    Choice,
    Guid,
    FilePath,
    IpAddressList,
    DriveLetter,
    ChoiceList,
}

/// <summary>Stark typisierte Beschreibung eines Aktionsparameters. Grundlage jeder Validierung (UI, KI, Profilimport, IPC).</summary>
public sealed record ParameterSpec(
    string Name,
    ParameterType Type,
    string Description,
    bool Required = true,
    long? Min = null,
    long? Max = null,
    IReadOnlyList<string>? Choices = null,
    string? Pattern = null,
    int MaxLength = 260,
    /// <summary>Kennzeichnet Parameter, die das Ziel der Änderung bestimmen (z. B. Dienstname). Für die Konflikterkennung.</summary>
    bool IsTargetKey = false);

/// <summary>Unveränderliche Parametermenge einer Aktion. Werte sind stets Zeichenketten und werden über <see cref="ParameterSpec"/> validiert.</summary>
public sealed class ActionParameters : IEquatable<ActionParameters>
{
    private readonly SortedDictionary<string, string> _values;

    public static ActionParameters Empty { get; } = new(new Dictionary<string, string>());

    public ActionParameters(IEnumerable<KeyValuePair<string, string>> values)
    {
        _values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values) _values[pair.Key] = pair.Value;
    }

    public static ActionParameters Of(params (string Name, string Value)[] values)
        => new(values.Select(v => new KeyValuePair<string, string>(v.Name, v.Value)));

    public IReadOnlyDictionary<string, string> Values => _values;
    public int Count => _values.Count;
    public bool Has(string name) => _values.ContainsKey(name);
    public string? GetString(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public string RequireString(string name) => GetString(name) ?? throw new ArgumentException($"Parameter '{name}' fehlt.");
    public long? GetInt(string name) => _values.TryGetValue(name, out var v) && long.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    public bool? GetBool(string name) => _values.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : null;
    public Guid? GetGuid(string name) => _values.TryGetValue(name, out var v) && Guid.TryParse(v, out var g) ? g : null;

    public string ToJson() => JsonSerializer.Serialize(_values);

    public static ActionParameters FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        return new ActionParameters(dict);
    }

    public bool Equals(ActionParameters? other)
        => other is not null && _values.Count == other._values.Count
           && _values.All(p => other._values.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as ActionParameters);
    public override int GetHashCode() => _values.Aggregate(17, (h, p) => HashCode.Combine(h, StringComparer.OrdinalIgnoreCase.GetHashCode(p.Key), p.Value));
    public override string ToString() => _values.Count == 0 ? "(keine)" : string.Join(", ", _values.Select(p => $"{p.Key}={p.Value}"));
}

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Ok { get; } = new(true, Array.Empty<string>());
    public static ValidationResult Fail(params string[] errors) => new(false, errors);
    public string ErrorText => string.Join(" ", Errors);
}

/// <summary>Deklarative Metadaten einer Optimierungsaktion (Abschnitt 33 der Spezifikation).</summary>
public sealed record ActionMetadata
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ActionCategory Category { get; init; }
    public required RiskLevel Risk { get; init; }
    public PrivilegeLevel RequiredPrivilege { get; init; } = PrivilegeLevel.User;
    public RestartRequirement Restart { get; init; } = RestartRequirement.None;
    public bool IsReversible { get; init; } = true;
    /// <summary>Reine Prüfung/Wartung ohne bleibende Konfigurationsänderung (z. B. DISM CheckHealth). Benötigt keine Sicherung.</summary>
    public bool IsDiagnosticOnly { get; init; }
    /// <summary>Experimentelle Aktionen werden nie automatisch und nur nach ausdrücklicher Freischaltung ausgeführt.</summary>
    public bool IsExperimental { get; init; }
    public WindowsVersionRange SupportedVersions { get; init; } = WindowsVersionRange.All;
    public string ExpectedEffect { get; init; } = "";
    public string SideEffects { get; init; } = "";
    public string BackupStrategy { get; init; } = "";
    public string RollbackStrategy { get; init; } = "";
    public string VerificationStrategy { get; init; } = "";
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ParameterSpec> Parameters { get; init; } = Array.Empty<ParameterSpec>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    /// <summary>Technische Details für die Anzeige „auf Wunsch“ (z. B. Registrierungspfad).</summary>
    public IReadOnlyList<string> TechnicalDetails { get; init; } = Array.Empty<string>();
    /// <summary>Kann von der Aktion ausgeschlossen werden, auch wenn sie risikoarm und umkehrbar ist.</summary>
    public bool ExcludeFromAutomation { get; init; }

    /// <summary>Nur umkehrbare, nicht experimentelle Aktionen bis Risikostufe „Niedrig“ dürfen unbeaufsichtigt laufen.</summary>
    public bool AllowInAutomation => IsReversible && !IsExperimental && !ExcludeFromAutomation && Risk <= RiskLevel.Low;
}

/// <summary>Momentaufnahme der für Kompatibilitätsprüfungen relevanten Systemeigenschaften.</summary>
public sealed record SystemContext
{
    public int WindowsBuild { get; init; } = WindowsVersionRange.Windows11_22H2;
    public string WindowsEdition { get; init; } = "";
    public bool IsWindows11 => WindowsBuild >= WindowsVersionRange.Windows11FirstBuild;
    public bool IsElevated { get; init; }
    public bool HasBattery { get; init; }
    public bool OnBatteryPower { get; init; }
    public bool IsVirtualMachine { get; init; }
    public bool SystemDriveIsSsd { get; init; } = true;
    public bool HasDedicatedGpu { get; init; }
    public int GpuCount { get; init; } = 1;
    public long TotalMemoryBytes { get; init; }
    public int LogicalProcessors { get; init; } = Environment.ProcessorCount;
    public bool ThermalThrottlingObserved { get; init; }
}

public sealed record SupportResult(bool IsSupported, string Reason)
{
    public static SupportResult Supported { get; } = new(true, "Kompatibel mit diesem System.");
    public static SupportResult NotSupported(string reason) => new(false, reason);
}

/// <summary>Erkannter Ist-Zustand einer Einstellung.</summary>
public sealed record ActionState
{
    /// <summary>False, wenn der Zustand nicht gelesen werden konnte (fehlende Rechte, nicht vorhandene Hardware …).</summary>
    public bool IsAvailable { get; init; } = true;
    public string DisplayValue { get; init; } = "";
    /// <summary>True, wenn der Zielzustand der Aktion bereits vorliegt; null, wenn nicht bestimmbar.</summary>
    public bool? IsApplied { get; init; }
    public string? RawValue { get; init; }
    public string? Note { get; init; }
    public bool ManagedByPolicy { get; init; }

    public static ActionState Unavailable(string reason) => new() { IsAvailable = false, DisplayValue = "Nicht verfügbar", Note = reason };
}

public sealed record RiskAssessment(RiskLevel Level, IReadOnlyList<string> Reasons)
{
    public static RiskAssessment From(ActionMetadata metadata) => new(metadata.Risk, string.IsNullOrWhiteSpace(metadata.SideEffects) ? Array.Empty<string>() : new[] { metadata.SideEffects });
}

public sealed record ChangePreview
{
    public required string ActionId { get; init; }
    public required string Title { get; init; }
    public required string CurrentValue { get; init; }
    public required string NewValue { get; init; }
    /// <summary>False, wenn der Zielzustand bereits vorliegt und nichts geändert würde.</summary>
    public bool WillChange { get; init; } = true;
    public IReadOnlyList<string> TechnicalDetails { get; init; } = Array.Empty<string>();
    public string? Note { get; init; }
}

/// <summary>Gesicherter Ursprungszustand. <see cref="PayloadJson"/> wird ausschließlich von der Aktion selbst interpretiert und validiert.</summary>
public sealed record BackupData(string ActionId, string PayloadJson, string OriginalDisplay, string IntendedDisplay)
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record OperationResult(bool Success, string Message, string? ErrorDetails = null)
{
    public static OperationResult Ok(string message = "Erfolgreich.") => new(true, message);
    public static OperationResult Fail(string message, string? details = null) => new(false, message, details);
}

public sealed record VerificationResult(bool Verified, string Expected, string Actual, string Message)
{
    public static VerificationResult Success(string expected, string actual) => new(true, expected, actual, "Der Zielzustand wurde durch Rücklesen bestätigt.");
    public static VerificationResult Failure(string expected, string actual, string? message = null)
        => new(false, expected, actual, message ?? $"Erwartet: {expected}, tatsächlich: {actual}.");
}

public sealed record RollbackResult(bool Success, bool ConflictDetected, string Message, string? ErrorDetails = null)
{
    public static RollbackResult Ok(string message = "Ursprungszustand wiederhergestellt.") => new(true, false, message);
    public static RollbackResult Conflict(string message) => new(false, true, message);
    public static RollbackResult Fail(string message, string? details = null) => new(false, false, message, details);
}
