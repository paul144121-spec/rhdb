using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Power;

public sealed record PowerPlanInfo(Guid Id, string Name, bool IsActive)
{
    public bool IsBuiltIn => WellKnownPowerPlans.All.Contains(Id);
}

public static class WellKnownPowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    /// <summary>„Ultimative Leistung“ – standardmäßig ausgeblendete Vorlage, die dupliziert werden muss.</summary>
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static readonly IReadOnlySet<Guid> All = new HashSet<Guid> { Balanced, HighPerformance, PowerSaver, UltimatePerformance };
}

/// <summary>Abstraktion der Windows-Energierichtlinien (powrprof.dll).</summary>
public interface IPowerPolicy
{
    IReadOnlyList<PowerPlanInfo> GetPlans();
    Guid GetActivePlan();
    void SetActivePlan(Guid plan);
    /// <summary>Liefert null, wenn die Einstellung auf diesem System nicht existiert.</summary>
    uint? ReadAcValue(Guid plan, Guid subgroup, Guid setting);
    uint? ReadDcValue(Guid plan, Guid subgroup, Guid setting);
    void WriteAcValue(Guid plan, Guid subgroup, Guid setting, uint value);
    void WriteDcValue(Guid plan, Guid subgroup, Guid setting, uint value);
    Guid DuplicatePlan(Guid source, string name);
    void DeletePlan(Guid plan);
    void RenamePlan(Guid plan, string name);
}

public enum PowerSettingUnit
{
    Percent,
    Seconds,
    Index,
}

/// <summary>Allowlist-Eintrag einer unterstützten Energieeinstellung mit zulässigem Wertebereich.</summary>
public sealed record PowerSettingDefinition
{
    public required string Alias { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Guid Subgroup { get; init; }
    public required Guid Setting { get; init; }
    public required uint Min { get; init; }
    public required uint Max { get; init; }
    public PowerSettingUnit Unit { get; init; } = PowerSettingUnit.Index;
    public IReadOnlyDictionary<uint, string>? Choices { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Low;
    public string Warning { get; init; } = "";

    public string Format(uint? value)
    {
        if (value is null) return "Nicht verfügbar";
        if (Choices is not null && Choices.TryGetValue(value.Value, out var label)) return label;
        return Unit switch
        {
            PowerSettingUnit.Percent => $"{value} %",
            PowerSettingUnit.Seconds => value == 0 ? "Nie" : value % 60 == 0 ? $"{value / 60} Min." : $"{value} Sek.",
            _ => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}

public static class PowerSettingCatalog
{
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid SubDisk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    public static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");

    public const string ProcessorMin = "processor-min";
    public const string ProcessorMax = "processor-max";
    public const string BoostMode = "boost-mode";
    public const string CoreParkingMin = "core-parking-min";
    public const string EnergyPreference = "energy-preference";
    public const string DisplayTimeout = "display-timeout";
    public const string SleepTimeout = "sleep-timeout";
    public const string DiskTimeout = "disk-timeout";
    public const string UsbSuspend = "usb-selective-suspend";
    public const string PciAspm = "pci-express-aspm";

    private const string HeatWarning = "Höhere Werte bedeuten mehr Stromverbrauch, Abwärme und Lüftergeräusch sowie kürzere Akkulaufzeit.";

    public static IReadOnlyList<PowerSettingDefinition> All { get; } = new[]
    {
        new PowerSettingDefinition
        {
            Alias = ProcessorMin, Name = "Minimaler Leistungszustand des Prozessors",
            Description = "Untergrenze, bis zu der Windows die Prozessorleistung im Leerlauf absenken darf.",
            Subgroup = SubProcessor, Setting = new("893dee8e-2bef-41e0-89c6-b55d0929964c"), Min = 0, Max = 100, Unit = PowerSettingUnit.Percent,
            Risk = RiskLevel.Low, Warning = HeatWarning,
        },
        new PowerSettingDefinition
        {
            Alias = ProcessorMax, Name = "Maximaler Leistungszustand des Prozessors",
            Description = "Obergrenze der Prozessorleistung. Werte unter 100 % begrenzen Takt und Turbo und senken damit Temperatur und Lautstärke.",
            Subgroup = SubProcessor, Setting = new("bc5038f7-23e0-4960-96da-33abaf5935ec"), Min = 50, Max = 100, Unit = PowerSettingUnit.Percent,
            Risk = RiskLevel.Low, Warning = "Werte unter 100 % verringern die Spitzenleistung spürbar.",
        },
        new PowerSettingDefinition
        {
            Alias = BoostMode, Name = "Leistungssteigerungsmodus des Prozessors (Turbo)",
            Description = "Legt fest, wie bereitwillig der Prozessor über seinen Nenntakt hinaus beschleunigt.",
            Subgroup = SubProcessor, Setting = new("be337238-0d82-4146-a960-4f3749d470c7"), Min = 0, Max = 4,
            Choices = new Dictionary<uint, string> { [0] = "Deaktiviert", [1] = "Aktiviert", [2] = "Aggressiv", [3] = "Effizient aktiviert", [4] = "Effizient aggressiv" },
            Risk = RiskLevel.Moderate, Warning = HeatWarning + " „Deaktiviert“ senkt Temperaturen deutlich, kostet aber Spitzenleistung.",
        },
        new PowerSettingDefinition
        {
            Alias = CoreParkingMin, Name = "Core Parking: Mindestanzahl aktiver Kerne",
            Description = "Anteil der Prozessorkerne, die Windows nicht in den Parkzustand versetzen darf. 100 % deaktiviert das Parken von Kernen.",
            Subgroup = SubProcessor, Setting = new("0cc5b647-c1df-4637-891a-dec35c318583"), Min = 0, Max = 100, Unit = PowerSettingUnit.Percent,
            Risk = RiskLevel.Moderate, Warning = HeatWarning + " Auf Hybrid-Prozessoren (P-/E-Kerne) kann eine Änderung die Kernzuteilung von Windows stören.",
        },
        new PowerSettingDefinition
        {
            Alias = EnergyPreference, Name = "Energie-/Leistungspräferenz des Prozessors (EPP)",
            Description = "Hinweis an den Prozessor, ob er eher Leistung (0 %) oder Energieeffizienz (100 %) bevorzugen soll.",
            Subgroup = SubProcessor, Setting = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), Min = 0, Max = 100, Unit = PowerSettingUnit.Percent,
            Risk = RiskLevel.Low, Warning = "Niedrige Werte erhöhen Stromverbrauch und Abwärme.",
        },
        new PowerSettingDefinition
        {
            Alias = DisplayTimeout, Name = "Bildschirm ausschalten nach",
            Description = "Leerlaufzeit, nach der der Bildschirm ausgeschaltet wird (0 = nie).",
            Subgroup = SubVideo, Setting = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), Min = 0, Max = 18000, Unit = PowerSettingUnit.Seconds, Risk = RiskLevel.VeryLow,
        },
        new PowerSettingDefinition
        {
            Alias = SleepTimeout, Name = "Energiesparmodus nach",
            Description = "Leerlaufzeit, nach der der Computer in den Energiesparmodus wechselt (0 = nie).",
            Subgroup = SubSleep, Setting = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), Min = 0, Max = 36000, Unit = PowerSettingUnit.Seconds, Risk = RiskLevel.VeryLow,
            Warning = "„Nie“ erhöht den Stromverbrauch bei unbeaufsichtigtem Gerät.",
        },
        new PowerSettingDefinition
        {
            Alias = DiskTimeout, Name = "Festplatte ausschalten nach",
            Description = "Leerlaufzeit, nach der Festplatten heruntergefahren werden (0 = nie). Für SSDs praktisch ohne Bedeutung.",
            Subgroup = SubDisk, Setting = new("6738e2c4-e8a5-4a42-b16a-e040e769756e"), Min = 0, Max = 36000, Unit = PowerSettingUnit.Seconds, Risk = RiskLevel.VeryLow,
        },
        new PowerSettingDefinition
        {
            Alias = UsbSuspend, Name = "Selektives USB-Energiesparen",
            Description = "Erlaubt Windows, einzelne USB-Anschlüsse im Leerlauf abzuschalten.",
            Subgroup = SubUsb, Setting = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), Min = 0, Max = 1,
            Choices = new Dictionary<uint, string> { [0] = "Deaktiviert", [1] = "Aktiviert" },
            Risk = RiskLevel.Low, Warning = "Deaktiviert erhöht den Leerlaufverbrauch, behebt aber Aussetzer mancher USB-Geräte.",
        },
        new PowerSettingDefinition
        {
            Alias = PciAspm, Name = "PCI Express: Verbindungszustand-Energieverwaltung",
            Description = "Stromsparmechanismus (ASPM) für PCIe-Verbindungen, z. B. Grafikkarte und NVMe-SSD.",
            Subgroup = SubPciExpress, Setting = new("ee12f906-d277-404b-b6da-e5fa1a576df5"), Min = 0, Max = 2,
            Choices = new Dictionary<uint, string> { [0] = "Aus", [1] = "Mittlere Energieeinsparungen", [2] = "Maximale Energieeinsparungen" },
            Risk = RiskLevel.Moderate, Warning = "„Aus“ erhöht den Leerlaufverbrauch und die Temperatur von PCIe-Geräten.",
        },
    };

    public static PowerSettingDefinition? Find(string alias) => All.FirstOrDefault(s => string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase));
}
