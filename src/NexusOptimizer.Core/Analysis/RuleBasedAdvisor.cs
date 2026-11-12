using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Storage;

namespace NexusOptimizer.Core.Analysis;

public enum RecommendationSeverity
{
    Info,
    Suggestion,
    Warning,
    Critical,
}

public sealed record Recommendation
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    public RecommendationSeverity Severity { get; init; } = RecommendationSeverity.Suggestion;
    public ActionCategory Area { get; init; }
    /// <summary>Gemessene Werte, auf denen die Empfehlung beruht.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    /// <summary>Konkrete, ausführbare Vorschläge. Leer bei rein erläuternden Empfehlungen.</summary>
    public IReadOnlyList<PlanRequest> Actions { get; init; } = Array.Empty<PlanRequest>();
    /// <summary>Seite der Anwendung, auf der sich das Thema bearbeiten lässt.</summary>
    public string? PageKey { get; init; }
}

public enum OptimizationGoal
{
    BalancedPerformance,
    MaximumFps,
    StableFrametimes,
    LowNoise,
    LowTemperatures,
    BatteryLife,
    FastStartup,
    Productivity,
    LowBackgroundUsage,
    Stability,
}

public static class OptimizationGoalExtensions
{
    public static string ToGerman(this OptimizationGoal goal) => goal switch
    {
        OptimizationGoal.BalancedPerformance => "Ausgewogene Leistung",
        OptimizationGoal.MaximumFps => "Maximale FPS",
        OptimizationGoal.StableFrametimes => "Stabile Frametimes",
        OptimizationGoal.LowNoise => "Geringe Lautstärke",
        OptimizationGoal.LowTemperatures => "Niedrige Temperaturen",
        OptimizationGoal.BatteryLife => "Akkulaufzeit",
        OptimizationGoal.FastStartup => "Schneller Systemstart",
        OptimizationGoal.Productivity => "Hohe Produktivität",
        OptimizationGoal.LowBackgroundUsage => "Geringe Hintergrundlast",
        OptimizationGoal.Stability => "Systemstabilität",
        _ => goal.ToString(),
    };
}

/// <summary>
/// Deterministisches Regelwerk. Dies ist ausdrücklich KEIN maschinelles Lernen: Jede Empfehlung folgt aus festen,
/// nachvollziehbaren Schwellenwerten auf real gemessenen Daten und nennt diese Daten als Beleg.
/// </summary>
public sealed class RuleBasedAdvisor
{
    public const string EngineDescription = "Deterministische, regelbasierte Analyse (kein KI-Sprachmodell, kein maschinelles Lernen).";

    private static readonly System.Globalization.CultureInfo De = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

    public IReadOnlyList<Recommendation> Analyze(SystemSnapshot s, OptimizationGoal goal = OptimizationGoal.BalancedPerformance)
    {
        var list = new List<Recommendation>();
        Storage(s, list);
        Memory(s, list);
        Cpu(s, list);
        Thermal(s, list);
        Disk(s, list);
        Power(s, goal, list);
        Gpu(s, goal, list);
        Startup(s, goal, list);
        Maintenance(s, list);
        Tweaks(s, goal, list);
        return list.OrderByDescending(r => r.Severity).ToList();
    }

    private static string P(double? value) => value is null ? "n. v." : value.Value.ToString("0", De) + " %";

    private static bool CanPropose(SystemSnapshot s, string actionId)
        => !s.DeclinedActions.Contains(actionId) && s.ActionStates.TryGetValue(actionId, out var applied) && applied == false;

    private static void Storage(SystemSnapshot s, List<Recommendation> list)
    {
        foreach (var volume in s.Info.Volumes.Where(v => v.TotalBytes > 0 && v.MediaType != DriveMediaType.Removable))
        {
            var critical = volume.FreePercent < 5 || volume.FreeBytes < 5L * 1024 * 1024 * 1024;
            var low = volume.FreePercent < 12 || (volume.IsSystem && volume.FreeBytes < 20L * 1024 * 1024 * 1024);
            if (!critical && !low) continue;
            list.Add(new Recommendation
            {
                Id = "storage-low-" + volume.Letter.TrimEnd(':').ToLowerInvariant(),
                Title = $"Wenig freier Speicher auf {volume.Letter}",
                Explanation = volume.IsSystem
                    ? "Auf dem Systemlaufwerk ist wenig Platz frei. Windows benötigt freien Speicher für Updates, die Auslagerungsdatei und temporäre Dateien; bei sehr vollen SSDs sinkt zudem die Schreibleistung. Prüfen Sie die Bereinigung – sie zeigt vorab genau an, was gelöscht würde."
                    : "Auf diesem Laufwerk ist wenig Platz frei. Über die Speicheranalyse finden Sie große Dateien und Duplikate.",
                Severity = critical ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                Area = ActionCategory.Storage,
                Evidence = new[] { $"{volume.Letter} frei: {ByteFormat.Format(volume.FreeBytes)} von {ByteFormat.Format(volume.TotalBytes)} ({volume.FreePercent.ToString("0.0", De)} %)" },
                PageKey = "cleanup",
            });
        }
    }

    private static void Memory(SystemSnapshot s, List<Recommendation> list)
    {
        var highRam = s.Memory.Average is >= 85 && s.Memory.Samples >= 10;
        var highCommit = s.Commit.Average is >= 90 && s.Commit.Samples >= 10;
        if (!highRam && !highCommit) return;

        var top = s.TopMemoryProcesses.Take(3).Select(p => $"{p.Name}: {ByteFormat.Format(p.WorkingSetBytes)}").ToList();
        var evidence = new List<string> { $"RAM-Auslastung im Mittel {P(s.Memory.Average)}, Spitze {P(s.Memory.Peak)}", $"Zugesicherter Speicher im Mittel {P(s.Commit.Average)}" };
        evidence.AddRange(top);
        list.Add(new Recommendation
        {
            Id = "memory-pressure",
            Title = "Arbeitsspeicher ist knapp",
            Explanation = "Der Arbeitsspeicher war im Beobachtungszeitraum durchgehend stark belegt. Zwischengespeicherter (Standby-)Speicher ist dabei unproblematisch – Windows gibt ihn bei Bedarf sofort frei. " +
                          "Wirksam sind nur das Schließen speicherintensiver Programme, weniger Autostart-Programme oder mehr RAM. „RAM-Bereiniger“ verschieben Daten lediglich in die Auslagerungsdatei und werden hier bewusst nicht angeboten." +
                          (highCommit ? " Der zugesicherte Speicher nähert sich dem Limit; stellen Sie sicher, dass die Auslagerungsdatei von Windows verwaltet wird." : ""),
            Severity = highCommit ? RecommendationSeverity.Warning : RecommendationSeverity.Suggestion,
            Area = ActionCategory.Memory,
            Evidence = evidence,
            PageKey = "ram",
        });
    }

    private static void Cpu(SystemSnapshot s, List<Recommendation> list)
    {
        if (s.Cpu.Average is not >= 80 || s.Cpu.Samples < 15) return;
        var top = s.TopCpuProcesses.Where(p => p.CpuPercent >= 5).Take(3).Select(p => $"{p.Name}: {p.CpuPercent.ToString("0.0", De)} %").ToList();
        list.Add(new Recommendation
        {
            Id = "cpu-saturated",
            Title = "Prozessor dauerhaft stark ausgelastet",
            Explanation = "Die CPU war im Beobachtungszeitraum fast durchgehend ausgelastet. Das ist normal, solange eine rechenintensive Anwendung bewusst läuft. Andernfalls lohnt ein Blick auf die Prozesse mit dem höchsten Verbrauch.",
            Severity = RecommendationSeverity.Suggestion,
            Area = ActionCategory.Cpu,
            Evidence = new[] { $"CPU im Mittel {P(s.Cpu.Average)}, Spitze {P(s.Cpu.Peak)}" }.Concat(top).ToList(),
            PageKey = "processes",
        });
    }

    private static void Thermal(SystemSnapshot s, List<Recommendation> list)
    {
        if (!s.ThermalLimitObserved) return;
        var evidence = new List<string>();
        if (s.PerformanceLimit.Average is { } limit) evidence.Add($"CPU-Leistungsgrenze im Mittel {P(limit)} (100 % = keine Begrenzung)");
        if (s.Temperature.Peak is { } peak) evidence.Add($"Höchste gemessene Thermalzonen-Temperatur: {peak.ToString("0", De)} °C");
        list.Add(new Recommendation
        {
            Id = "thermal-limit",
            Title = "Die Prozessorleistung wird begrenzt",
            Explanation = "Windows meldet, dass die Prozessorleistung zeitweise begrenzt wurde – häufig aus thermischen Gründen oder durch ein Energielimit. Leistungsorientierte Energieprofile würden das Problem verschärfen und werden deshalb derzeit nicht empfohlen. " +
                          "Prüfen Sie Kühlung, Lüfter, Staub und Wärmeleitpaste bzw. bei Notebooks die Aufstellung und das Netzteil.",
            Severity = RecommendationSeverity.Warning,
            Area = ActionCategory.Cpu,
            Evidence = evidence,
            PageKey = "cpu",
        });
    }

    private static void Disk(SystemSnapshot s, List<Recommendation> list)
    {
        var system = s.Info.SystemVolume;
        if (system?.MediaType == DriveMediaType.Hdd)
        {
            list.Add(new Recommendation
            {
                Id = "system-on-hdd",
                Title = "Windows läuft auf einer Festplatte",
                Explanation = "Das Systemlaufwerk ist eine mechanische Festplatte. Keine Softwareeinstellung kommt an den Effekt eines Umstiegs auf eine SSD heran. Bis dahin helfen ein schlanker Autostart und die regelmäßige (von Windows geplante) Laufwerksoptimierung.",
                Severity = RecommendationSeverity.Info,
                Area = ActionCategory.Storage,
                Evidence = new[] { $"Systemlaufwerk {system.Letter}: Festplatte (HDD)" },
                PageKey = "storage",
            });
        }

        if (s.DiskLatency.Average is >= 50 && s.DiskLatency.Samples >= 10)
        {
            list.Add(new Recommendation
            {
                Id = "disk-latency",
                Title = "Datenträger reagiert langsam",
                Explanation = "Die mittlere Antwortzeit des Datenträgers war auffällig hoch. Ursachen können hohe Hintergrundlast (Updates, Virenscan, Indizierung), ein fast voller oder ein alternder Datenträger sein. Prüfen Sie den Datenträgerzustand in der Diagnose.",
                Severity = RecommendationSeverity.Warning,
                Area = ActionCategory.Storage,
                Evidence = new[] { $"Antwortzeit im Mittel {s.DiskLatency.Average.Value.ToString("0.0", De)} ms, Aktivität im Mittel {P(s.DiskActive.Average)}" },
                PageKey = "diagnostics",
            });
        }

        foreach (var drive in s.Info.Drives.Where(d => d.Health is HealthState.Warning or HealthState.Unhealthy))
        {
            list.Add(new Recommendation
            {
                Id = "drive-health-" + drive.DeviceNumber,
                Title = $"Datenträger „{drive.Model}“ meldet Probleme",
                Explanation = "Windows stuft den Zustand dieses Datenträgers nicht als fehlerfrei ein. Sichern Sie wichtige Daten umgehend und prüfen Sie den Datenträger mit dem Herstellerwerkzeug.",
                Severity = drive.Health == HealthState.Unhealthy ? RecommendationSeverity.Critical : RecommendationSeverity.Warning,
                Area = ActionCategory.Storage,
                Evidence = new[] { $"Integritätsstatus laut Windows: {(drive.Health == HealthState.Unhealthy ? "Fehlerhaft" : "Warnung")}" },
                PageKey = "storage",
            });
        }
    }

    private static void Power(SystemSnapshot s, OptimizationGoal goal, List<Recommendation> list)
    {
        var active = s.ActivePowerPlan;
        if (active is null) return;
        PowerPlanInfo? Plan(Guid id) => s.PowerPlans.FirstOrDefault(p => p.Id == id);
        var onBattery = s.Context.OnBatteryPower;
        var performancePlan = active.Id == WellKnownPowerPlans.HighPerformance || active.Id == WellKnownPowerPlans.UltimatePerformance;

        if (active.Id == WellKnownPowerPlans.PowerSaver && !onBattery && goal is not (OptimizationGoal.BatteryLife or OptimizationGoal.LowNoise or OptimizationGoal.LowTemperatures)
            && Plan(WellKnownPowerPlans.Balanced) is { } balanced && !s.DeclinedActions.Contains(PowerPlanActivateAction.ActionId))
        {
            list.Add(new Recommendation
            {
                Id = "power-saver-on-ac",
                Title = "Energiesparplan trotz Netzbetrieb aktiv",
                Explanation = "Der Plan „Energiesparmodus“ drosselt den Prozessor deutlich. Im Netzbetrieb ist „Ausbalanciert“ in der Regel die bessere Wahl: volle Leistung bei Bedarf, sparsam im Leerlauf.",
                Severity = RecommendationSeverity.Suggestion,
                Area = ActionCategory.Power,
                Evidence = new[] { $"Aktiver Plan: {active.Name}", "Stromversorgung: Netzbetrieb" },
                Actions = new[] { new PlanRequest(PowerPlanActivateAction.ActionId, ActionParameters.Of(("plan", balanced.Id.ToString())), "Im Netzbetrieb nicht unnötig drosseln") },
                PageKey = "power",
            });
        }

        if (performancePlan && (onBattery || s.ThermalLimitObserved || goal is OptimizationGoal.BatteryLife or OptimizationGoal.LowNoise or OptimizationGoal.LowTemperatures)
            && Plan(WellKnownPowerPlans.Balanced) is { } target && !s.DeclinedActions.Contains(PowerPlanActivateAction.ActionId))
        {
            var why = onBattery ? "Das Gerät läuft im Akkubetrieb." : s.ThermalLimitObserved ? "Es wurde eine Begrenzung der Prozessorleistung beobachtet." : $"Ihr Ziel ist „{goal.ToGerman()}“.";
            list.Add(new Recommendation
            {
                Id = "performance-plan-unsuitable",
                Title = "Leistungsplan passt nicht zur aktuellen Situation",
                Explanation = $"{why} Ein Hochleistungsplan hält den Prozessor dauerhaft auf hohem Takt und erhöht Verbrauch, Abwärme und Lüftergeräusch. „Ausbalanciert“ liefert bei Bedarf dieselbe Spitzenleistung.",
                Severity = RecommendationSeverity.Suggestion,
                Area = ActionCategory.Power,
                Evidence = new[] { $"Aktiver Plan: {active.Name}" },
                Actions = new[] { new PlanRequest(PowerPlanActivateAction.ActionId, ActionParameters.Of(("plan", target.Id.ToString())), why) },
                PageKey = "power",
            });
        }
    }

    private static void Gpu(SystemSnapshot s, OptimizationGoal goal, List<Recommendation> list)
    {
        if (s.Info.Gpus.Count > 1 && s.Info.Gpus.Any(g => g.IsIntegrated) && s.Info.Gpus.Any(g => !g.IsIntegrated))
        {
            list.Add(new Recommendation
            {
                Id = "gpu-hybrid",
                Title = "Integrierte und dedizierte Grafik erkannt",
                Explanation = "Windows entscheidet pro Anwendung, welche Grafikeinheit verwendet wird. Für Spiele und 3D-Anwendungen können Sie die dedizierte Grafik fest zuweisen; für Büroprogramme spart die integrierte Grafik Strom.",
                Severity = RecommendationSeverity.Info,
                Area = ActionCategory.Gpu,
                Evidence = s.Info.Gpus.Select(g => $"{g.Name} ({(g.IsIntegrated ? "integriert" : "dediziert")})").ToList(),
                PageKey = "gpu",
            });
        }

        if (s.GpuMemory.Average is >= 92 && s.GpuMemory.Samples >= 10)
        {
            list.Add(new Recommendation
            {
                Id = "vram-pressure",
                Title = "Grafikspeicher nahezu voll",
                Explanation = "Der dedizierte Grafikspeicher war fast vollständig belegt. In Spielen führt das zu Nachladerucklern. Abhilfe schaffen niedrigere Texturdetails oder eine geringere Auflösung – das ist eine Einstellung im Spiel, kein Windows-Tweak.",
                Severity = RecommendationSeverity.Suggestion,
                Area = ActionCategory.Gpu,
                Evidence = new[] { $"Grafikspeicher im Mittel {P(s.GpuMemory.Average)} belegt" },
                PageKey = "gpu",
            });
        }

        foreach (var gpu in s.Info.Gpus.Where(g => g.DriverDate is { } date && (s.CapturedAt.UtcDateTime - date).TotalDays > 540 && g.DriverUrl is not null))
        {
            list.Add(new Recommendation
            {
                Id = "gpu-driver-old-" + gpu.Vendor.ToLowerInvariant(),
                Title = $"Grafiktreiber für {gpu.Name} ist älter als 18 Monate",
                Explanation = "Neuere Treiber enthalten Fehlerbehebungen und Anpassungen für aktuelle Spiele. Laden Sie Treiber ausschließlich von der offiziellen Herstellerseite oder über Windows Update.",
                Severity = RecommendationSeverity.Info,
                Area = ActionCategory.Gpu,
                Evidence = new[] { $"Treiberversion {gpu.DriverVersion} vom {gpu.DriverDate:dd.MM.yyyy}", $"Offizielle Quelle: {gpu.DriverUrl}" },
                PageKey = "gpu",
            });
        }
    }

    private static void Startup(SystemSnapshot s, OptimizationGoal goal, List<Recommendation> list)
    {
        var enabled = s.StartupEntries.Count(e => e.Enabled);
        var threshold = goal is OptimizationGoal.FastStartup or OptimizationGoal.LowBackgroundUsage ? 6 : 10;
        if (enabled < threshold) return;
        list.Add(new Recommendation
        {
            Id = "startup-many",
            Title = $"{enabled} Programme starten automatisch mit Windows",
            Explanation = "Jedes Autostart-Programm verlängert die Anmeldung und belegt dauerhaft Arbeitsspeicher. Prüfen Sie im Autostart-Manager, welche Einträge Sie wirklich benötigen. Einträge werden nur deaktiviert, nie gelöscht, und lassen sich jederzeit wieder einschalten. " +
                          "Unbekannte Einträge sind nicht automatisch schädlich – und Sicherheitssoftware sollte aktiviert bleiben.",
            Severity = RecommendationSeverity.Suggestion,
            Area = ActionCategory.Startup,
            Evidence = new[] { $"Aktive Autostarteinträge: {enabled} von {s.StartupEntries.Count}" },
            PageKey = "startup",
        });
    }

    private static void Maintenance(SystemSnapshot s, List<Recommendation> list)
    {
        if (s.Info.PendingReboot)
        {
            list.Add(new Recommendation
            {
                Id = "pending-reboot",
                Title = "Ein Neustart steht aus",
                Explanation = "Windows wartet auf einen Neustart, um Updates oder Änderungen abzuschließen. Bis dahin können Leistung und Stabilität beeinträchtigt sein. Optimierungen sollten erst nach dem Neustart beurteilt werden.",
                Severity = RecommendationSeverity.Warning,
                Area = ActionCategory.Repair,
                Evidence = s.Info.PendingRebootReasons,
            });
        }
        else if (s.Uptime.TotalDays >= 14)
        {
            list.Add(new Recommendation
            {
                Id = "long-uptime",
                Title = $"Windows läuft seit {(int)s.Uptime.TotalDays} Tagen ohne Neustart",
                Explanation = "Ein gelegentlicher Neustart schließt Updates ab und räumt hängengebliebene Ressourcen auf. Beachten Sie: Bei aktivem Schnellstart setzt „Herunterfahren“ den Kernel nicht zurück – nur „Neu starten“ tut das.",
                Severity = RecommendationSeverity.Info,
                Area = ActionCategory.Repair,
                Evidence = new[] { $"Betriebszeit: {(int)s.Uptime.TotalDays} Tage {s.Uptime.Hours} Stunden" },
            });
        }
    }

    private static void Tweaks(SystemSnapshot s, OptimizationGoal goal, List<Recommendation> list)
    {
        var gamingGoal = goal is OptimizationGoal.MaximumFps or OptimizationGoal.StableFrametimes;

        var gameMode = "reg.gaming.enable-game-mode";
        if (CanPropose(s, gameMode))
        {
            list.Add(new Recommendation
            {
                Id = "tweak-game-mode",
                Title = "Windows-Spielmodus ist ausgeschaltet",
                Explanation = "Der Spielmodus verhindert, dass Windows Update während des Spielens Treiber installiert oder zum Neustart auffordert, und bevorzugt das Spiel bei der Ressourcenzuteilung. Er ist risikoarm und jederzeit umkehrbar.",
                Severity = RecommendationSeverity.Suggestion,
                Area = ActionCategory.Gaming,
                Evidence = new[] { "Einstellung „Spielmodus“: aus" },
                Actions = new[] { new PlanRequest(gameMode, ActionParameters.Empty, "Spielmodus ist deaktiviert") },
                PageKey = "gaming",
            });
        }

        var recording = "reg.gaming.disable-background-recording";
        if (gamingGoal && CanPropose(s, recording))
        {
            list.Add(new Recommendation
            {
                Id = "tweak-background-recording",
                Title = "Hintergrundaufzeichnung der Game Bar ist möglich",
                Explanation = "Wenn Sie keine rückwirkenden Clips benötigen, vermeidet das Abschalten der Hintergrundaufzeichnung Encoder- und Datenträgerlast während des Spielens. Ob das messbar ist, hängt von Ihrem System ab.",
                Severity = RecommendationSeverity.Suggestion,
                Area = ActionCategory.Gaming,
                Evidence = new[] { "Einstellung „Hintergrundaufzeichnung“: nicht deaktiviert" },
                Actions = new[] { new PlanRequest(recording, ActionParameters.Empty, "Vermeidet Aufzeichnungslast beim Spielen") },
                PageKey = "gaming",
            });
        }

        // Schwächere Hardware: optische Effekte, die die integrierte Grafik bzw. knappen RAM belasten.
        var lowEnd = !s.Info.HasDedicatedGpu && s.Info.MemoryTotalBytes is > 0 and <= 8L * 1024 * 1024 * 1024;
        if (lowEnd)
        {
            var candidates = RegistryTweakCatalog.All.Where(t => t.Tags.Contains(RegistryTweakCatalog.TagLowEnd) && CanPropose(s, t.ActionId)).ToList();
            if (candidates.Count > 0)
            {
                list.Add(new Recommendation
                {
                    Id = "tweak-low-end-visuals",
                    Title = "Optische Effekte entlasten schwächere Hardware",
                    Explanation = "Ihr System hat höchstens 8 GB RAM und keine dedizierte Grafikkarte. Transparenz und Animationen kosten hier spürbar Grafikleistung. Das Abschalten macht die Oberfläche direkter, ändert aber das Aussehen.",
                    Severity = RecommendationSeverity.Suggestion,
                    Area = ActionCategory.VisualEffects,
                    Evidence = new[] { $"Arbeitsspeicher: {ByteFormat.Format(s.Info.MemoryTotalBytes)}", "Keine dedizierte Grafikkarte erkannt" },
                    Actions = candidates.Select(t => new PlanRequest(t.ActionId, ActionParameters.Empty, t.ExpectedBehavior)).ToList(),
                    PageKey = "registry",
                });
            }
        }

        var privacy = RegistryTweakCatalog.All.Where(t => t.Tags.Contains(RegistryTweakCatalog.TagSafe) && t.Tags.Contains(RegistryTweakCatalog.TagPrivacy) && CanPropose(s, t.ActionId)).ToList();
        if (privacy.Count >= 3)
        {
            list.Add(new Recommendation
            {
                Id = "tweak-privacy-basics",
                Title = $"{privacy.Count} risikoarme Datenschutzeinstellungen stehen auf Standard",
                Explanation = "Werbe-ID, App-Vorschläge und Tipps lassen sich ohne Funktionsverlust abschalten. Das verbessert die Leistung nicht messbar, reduziert aber Ablenkungen und Datennutzung.",
                Severity = RecommendationSeverity.Info,
                Area = ActionCategory.Privacy,
                Evidence = privacy.Take(4).Select(t => t.Name).ToList(),
                Actions = privacy.Select(t => new PlanRequest(t.ActionId, ActionParameters.Empty, t.ExpectedBehavior)).ToList(),
                PageKey = "privacy",
            });
        }
    }
}
