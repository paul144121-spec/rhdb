using System.Globalization;
using System.Text;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Optimization;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.Tools;

namespace NexusOptimizer.AI;

public enum ChatIntent
{
    Unknown,
    Greeting,
    Help,
    OptimizeGaming,
    OptimizeGeneral,
    ExplainCpu,
    SlowPrograms,
    AnalyzeMemory,
    CreateGameProfile,
    RecommendSettings,
    ListRiskyTweaks,
    UndoLast,
    SlowInternet,
    Temperatures,
    AutomationPlan,
    Storage,
    Startup,
    Power,
    Privacy,
    SystemInfo,
}

/// <summary>
/// Einfache, deterministische Erkennung deutscher Anfragen über gewichtete Schlüsselwörter.
/// Das ist ausdrücklich keine Sprach-KI: Unbekannte Formulierungen werden als „nicht verstanden“ behandelt.
/// </summary>
public static class GermanIntentParser
{
    private static readonly (ChatIntent Intent, (string Word, int Weight)[] Keywords)[] Rules =
    {
        (ChatIntent.UndoLast, new[] { ("ruckgangig", 5), ("zuruck", 2), ("wiederherstell", 4), ("letzte optimierung", 3), ("undo", 5), ("zurucknehm", 4), ("zurucksetz", 3) }),
        (ChatIntent.ListRiskyTweaks, new[] { ("riskant", 5), ("risiko", 3), ("gefahrlich", 4), ("experimentell", 4), ("expertenlabor", 5) }),
        (ChatIntent.CreateGameProfile, new[] { ("gaming-profil", 5), ("gamingprofil", 5), ("spielprofil", 5), ("profil fur dieses spiel", 6), ("profil", 2), ("spiel", 1) }),
        (ChatIntent.OptimizeGaming, new[] { ("gaming", 3), ("spiele", 3), ("zocken", 4), ("fps", 4), ("spiel", 2), ("ruckel", 3), ("frametime", 4), ("optimier", 2) }),
        (ChatIntent.AutomationPlan, new[] { ("automatisch", 3), ("automatisier", 5), ("optimierungsplan", 5), ("regel", 3), ("zeitplan", 4), ("plan", 1) }),
        (ChatIntent.ExplainCpu, new[] { ("cpu", 4), ("prozessor", 4), ("auslastung", 2), ("kern", 1) }),
        (ChatIntent.AnalyzeMemory, new[] { ("ram", 4), ("arbeitsspeicher", 5), ("speicherauslastung", 4), ("auslagerung", 4), ("memory", 3) }),
        (ChatIntent.SlowPrograms, new[] { ("welche programme", 5), ("verlangsam", 4), ("langsam", 2), ("bremst", 4), ("programme", 2), ("prozesse", 3), ("hintergrund", 3), ("trage", 3) }),
        (ChatIntent.SlowInternet, new[] { ("internet", 5), ("netzwerk", 4), ("wlan", 4), ("ping", 4), ("latenz", 3), ("dns", 4), ("download", 2), ("verbindung", 2), ("lag", 2) }),
        (ChatIntent.Temperatures, new[] { ("temperatur", 5), ("heiss", 4), ("hitze", 4), ("uberhitz", 5), ("lufter", 3), ("kuhl", 3), ("thermal", 4), ("drossel", 3) }),
        (ChatIntent.Storage, new[] { ("speicherplatz", 5), ("festplatte", 4), ("ssd", 3), ("laufwerk", 3), ("bereinig", 4), ("aufraum", 4), ("voll", 2), ("temporar", 3), ("datentrager", 4) }),
        (ChatIntent.Startup, new[] { ("autostart", 5), ("hochfahren", 4), ("systemstart", 4), ("bootet", 4), ("startet langsam", 5), ("beim start", 3) }),
        (ChatIntent.Power, new[] { ("energie", 4), ("akku", 4), ("batterie", 4), ("stromverbrauch", 4), ("energieplan", 5), ("energiespar", 4), ("leise", 2) }),
        (ChatIntent.Privacy, new[] { ("datenschutz", 5), ("privatsphare", 5), ("telemetrie", 5), ("werbung", 3), ("tracking", 4), ("diagnosedaten", 4) }),
        (ChatIntent.RecommendSettings, new[] { ("welche einstellungen", 6), ("empfehl", 4), ("empfiehl", 4), ("vorschlag", 3), ("vorschlage", 3), ("tipps", 3), ("was sollte ich", 4), ("einstellungen", 2), ("andern", 1) }),
        (ChatIntent.OptimizeGeneral, new[] { ("optimier", 3), ("schneller", 3), ("beschleunig", 4), ("verbesser", 2), ("tuning", 3), ("pc", 1) }),
        (ChatIntent.SystemInfo, new[] { ("hardware", 4), ("welche grafikkarte", 5), ("welcher prozessor", 5), ("systeminfo", 5), ("was fur ein", 2), ("mein system", 3), ("ausstattung", 3) }),
        (ChatIntent.Help, new[] { ("hilfe", 5), ("was kannst du", 6), ("funktionen", 3), ("wie funktioniert", 3), ("anleitung", 3) }),
        (ChatIntent.Greeting, new[] { ("hallo", 5), ("guten tag", 5), ("guten morgen", 5), ("guten abend", 5), ("servus", 5), ("moin", 5), ("hi", 2), ("hey", 3) }),
    };

    /// <summary>Kleinschreibung, Umlaute und ß vereinheitlicht, Satzzeichen entfernt.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c == 'ß') builder.Append("ss");
            else if (char.IsLetterOrDigit(c) || c == '-') builder.Append(c);
            else builder.Append(' ');
        }
        return " " + string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
    }

    public static ChatIntent Parse(string text)
    {
        var normalized = Normalize(text);
        // Bindestriche zusätzlich als Worttrenner werten, damit „CPU-Auslastung“ das Wort „cpu“ enthält.
        var spaced = normalized.Replace('-', ' ');
        var best = ChatIntent.Unknown;
        var bestScore = 0;
        foreach (var (intent, keywords) in Rules)
        {
            var score = 0;
            foreach (var (word, weight) in keywords)
            {
                // Kurze Wörter (≤ 3 Zeichen) nur als ganzes Wort, damit „hi“ nicht in „Hilfe“ erkannt wird.
                var matched = word.Length <= 3 ? spaced.Contains(" " + word + " ") : normalized.Contains(word) || spaced.Contains(word);
                if (matched) score += weight;
            }
            if (score > bestScore)
            {
                best = intent;
                bestScore = score;
            }
        }
        return bestScore >= 3 ? best : ChatIntent.Unknown;
    }
}

public sealed record RuleBasedAnswer(string Text, IReadOnlyList<ChatProposal> Proposals, Core.Chat.ChatMessageKind Kind);

/// <summary>
/// Erzeugt Antworten ohne Sprachmodell aus festen Bausteinen und den real gemessenen Daten der Momentaufnahme.
/// Jede Antwort ist nachvollziehbar und enthält ausschließlich Werte, die tatsächlich vorliegen.
/// </summary>
public sealed class RuleBasedResponder
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly RuleBasedAdvisor _advisor;
    private readonly ActionCatalog _catalog;
    private readonly Func<TransactionRecord?> _lastChange;

    public RuleBasedResponder(RuleBasedAdvisor advisor, ActionCatalog catalog, Func<TransactionRecord?> lastChange)
    {
        _advisor = advisor;
        _catalog = catalog;
        _lastChange = lastChange;
    }

    public const string Disclosure = "Hinweis: Es ist kein KI-Sprachmodell eingerichtet. Diese Antwort stammt aus einem festen Regelwerk, das Ihre Messwerte auswertet – nicht aus maschinellem Lernen.";

    private static string P(double? value) => value is null ? "nicht verfügbar" : value.Value.ToString("0", De) + " %";

    public RuleBasedAnswer Respond(ChatIntent intent, SystemSnapshot s, OptimizationGoal goal)
    {
        var b = new StringBuilder();
        var proposals = new List<ChatProposal>();
        var kind = Core.Chat.ChatMessageKind.Explanation;
        var info = s.Info;

        ChatProposal Page(string key, string title, string description) => new() { Kind = ProposalKind.OpenPage, Title = title, Description = description, PageKey = key };
        void AddActions(string title, string description, IEnumerable<PlanRequest> requests)
        {
            var list = requests.Where(r => _catalog.Contains(r.ActionId)).GroupBy(r => r.Key).Select(g => g.First()).ToList();
            if (list.Count == 0) return;
            proposals.Add(new ChatProposal { Kind = ProposalKind.Action, Title = title, Description = description, Requests = list });
            kind = Core.Chat.ChatMessageKind.Recommendation;
        }

        switch (intent)
        {
            case ChatIntent.Greeting:
            case ChatIntent.Help:
                b.AppendLine("Guten Tag! Ich bin der Assistent von NEXUS Optimizer AI. Ich kann zum Beispiel:");
                b.AppendLine("• die aktuelle CPU-, RAM-, Datenträger- und Netzwerkauslastung erklären,");
                b.AppendLine("• Programme benennen, die gerade viele Ressourcen belegen,");
                b.AppendLine("• risikoarme Einstellungen für Alltag oder Gaming vorschlagen – mit Vorschau und Bestätigung,");
                b.AppendLine("• die letzte Änderung rückgängig machen,");
                b.AppendLine("• zur passenden Seite der Anwendung führen.");
                b.AppendLine();
                b.AppendLine("Ich ändere nie etwas ohne Ihre ausdrückliche Bestätigung.");
                break;

            case ChatIntent.OptimizeGaming:
            {
                var candidates = OptimizationModes.BuildCandidates(OptimizationMode.Gaming, s, _advisor.Analyze(s, OptimizationGoal.MaximumFps), out var notes)
                    .Where(r => r.ActionId.StartsWith("power.", StringComparison.Ordinal) || s.ActionStates.GetValueOrDefault(r.ActionId) == false).ToList();
                b.AppendLine("Für Spiele kommen auf diesem System folgende, vollständig umkehrbare Einstellungen in Frage:");
                foreach (var request in candidates) b.AppendLine("• " + (_catalog.Find(request.ActionId)?.Metadata.Name ?? request.ActionId));
                if (candidates.Count == 0) b.AppendLine("• Keine – die spielrelevanten Einstellungen liegen bereits im Zielzustand vor.");
                foreach (var note in notes) b.AppendLine("Hinweis: " + note);
                b.AppendLine();
                b.AppendLine($"Aktuelle Lage: CPU im Mittel {P(s.Cpu.Average)}, GPU {P(s.Gpu.Average)}, RAM {P(s.Memory.Average)}.");
                if (s.Gpu.Average is >= 90 && s.Cpu.Average is < 60) b.AppendLine("Die Grafikkarte ist der begrenzende Faktor – hier helfen vor allem niedrigere Grafikdetails oder eine geringere Auflösung im Spiel.");
                else if (s.Cpu.Average is >= 85) b.AppendLine("Der Prozessor ist stark ausgelastet – prüfen Sie, welche Hintergrundprogramme während des Spielens laufen.");
                b.AppendLine("Eine höhere Bildrate kann nicht zugesagt werden; die Wirkung hängt von Spiel und Hardware ab. Leistungsorientierte Energiepläne erhöhen Verbrauch, Wärme und Lautstärke.");
                AddActions("Gaming-Einstellungen anwenden", "Vorschau anzeigen und nach Bestätigung ausführen", candidates);
                proposals.Add(Page("gaming", "Gaming Boost öffnen", "Spiele erkennen und Profile pro Spiel anlegen"));
                break;
            }

            case ChatIntent.OptimizeGeneral:
            case ChatIntent.RecommendSettings:
            {
                var recommendations = _advisor.Analyze(s, goal);
                if (recommendations.Count == 0)
                {
                    b.AppendLine("Die Analyse hat keine Auffälligkeiten ergeben. Auslastung, Speicherplatz und Energieeinstellungen liegen im unkritischen Bereich – es gibt derzeit nichts, was ich guten Gewissens ändern würde.");
                }
                else
                {
                    b.AppendLine($"Die Analyse hat {recommendations.Count} Punkt(e) ergeben:");
                    foreach (var r in recommendations.Take(6))
                    {
                        b.AppendLine();
                        b.AppendLine("• " + r.Title);
                        b.AppendLine("  " + r.Explanation);
                        foreach (var evidence in r.Evidence.Take(3)) b.AppendLine("  – " + evidence);
                    }
                    AddActions("Empfohlene Änderungen prüfen", "Nur risikoarme, umkehrbare Einstellungen – mit Vorschau", recommendations.SelectMany(r => r.Actions));
                }
                proposals.Add(Page("oneclick", "Ein-Klick-Optimierung öffnen", "Analyse mit vollständigem Optimierungsplan"));
                break;
            }

            case ChatIntent.ExplainCpu:
                b.AppendLine($"CPU-Auslastung: aktuell {P(s.Latest?.CpuTotalPercent)}, im Mittel {P(s.Cpu.Average)}, Spitze {P(s.Cpu.Peak)} ({s.Cpu.Samples} Messpunkte).");
                if (s.TopCpuProcesses.Any(p => p.CpuPercent >= 1))
                {
                    b.AppendLine("Die größten Verbraucher im Moment:");
                    foreach (var p in s.TopCpuProcesses.Where(p => p.CpuPercent >= 1).Take(5)) b.AppendLine($"• {p.Name}: {p.CpuPercent.ToString("0.0", De)} %");
                }
                else b.AppendLine("Kein einzelner Prozess beansprucht derzeit nennenswert CPU-Zeit.");
                if (s.ThermalLimitObserved) b.AppendLine("Auffällig: Windows meldet, dass die Prozessorleistung zeitweise begrenzt wurde – das deutet auf ein Wärme- oder Energielimit hin.");
                if (s.Cpu.Average is < 50) b.AppendLine("Insgesamt ist die Auslastung unauffällig. Kurze Spitzen beim Starten von Programmen oder bei Updates sind normal.");
                else if (s.Cpu.Average is >= 80) b.AppendLine("Die Auslastung ist dauerhaft hoch. Wenn kein rechenintensives Programm bewusst läuft, lohnt ein Blick in die Prozessliste.");
                proposals.Add(Page("processes", "Prozess-Manager öffnen", "Alle Prozesse mit CPU- und RAM-Verbrauch"));
                break;

            case ChatIntent.SlowPrograms:
                b.AppendLine("Programme mit dem höchsten Ressourcenverbrauch (aktuelle Messung):");
                b.AppendLine("CPU:");
                foreach (var p in s.TopCpuProcesses.Take(5)) b.AppendLine($"• {p.Name}: {p.CpuPercent.ToString("0.0", De)} %");
                b.AppendLine("Arbeitsspeicher:");
                foreach (var p in s.TopMemoryProcesses.Take(5)) b.AppendLine($"• {p.Name}: {ByteFormat.Format(p.WorkingSetBytes)}");
                b.AppendLine();
                b.AppendLine($"Außerdem starten {s.StartupEntries.Count(e => e.Enabled)} Programme automatisch mit Windows.");
                b.AppendLine("Ein hoher Verbrauch ist nicht automatisch ein Problem – ein Browser mit vielen Tabs oder ein laufendes Spiel belegen legitim viele Ressourcen. Unbekannte Programme sind nicht automatisch schädlich.");
                proposals.Add(Page("processes", "Prozess-Manager öffnen", "Prozesse prüfen und bei Bedarf beenden"));
                proposals.Add(Page("startup", "Autostart-Manager öffnen", "Automatisch startende Programme verwalten"));
                break;

            case ChatIntent.AnalyzeMemory:
            {
                var m = s.Latest;
                if (m is not null && m.MemoryTotalBytes > 0)
                {
                    b.AppendLine($"Installiert: {ByteFormat.Format((long)m.MemoryTotalBytes)}. In Verwendung: {P(m.MemoryUsedPercent)}, verfügbar: {ByteFormat.Format((long)m.MemoryAvailableBytes)}.");
                    if (m.StandbyCacheBytes is { } standby) b.AppendLine($"Davon sind {ByteFormat.Format(standby)} Standby-Cache: Windows hält dort zuletzt verwendete Daten vor und gibt den Speicher sofort frei, wenn ein Programm ihn braucht. Das ist gewollt und beschleunigt das System.");
                    b.AppendLine($"Zugesicherter Speicher: {ByteFormat.Format((long)m.CommitUsedBytes)} von {ByteFormat.Format((long)m.CommitLimitBytes)} ({P(m.CommitPercent)}).");
                }
                else b.AppendLine("Es liegen noch keine Speichermesswerte vor.");
                b.AppendLine("Größte Verbraucher:");
                foreach (var p in s.TopMemoryProcesses.Take(6)) b.AppendLine($"• {p.Name}: {ByteFormat.Format(p.WorkingSetBytes)}");
                if (s.Memory.Average is >= 85) b.AppendLine("Der Arbeitsspeicher ist dauerhaft knapp. Wirksam sind: speicherintensive Programme schließen, Autostart ausdünnen oder RAM aufrüsten.");
                else b.AppendLine("Die Speicherauslastung ist unkritisch.");
                b.AppendLine("„RAM-Bereiniger“ biete ich bewusst nicht an – sie verschieben Daten nur in die Auslagerungsdatei und machen das System eher langsamer.");
                proposals.Add(Page("ram", "RAM-Optimierung öffnen", "Details, Verlauf und Auslagerungsdatei"));
                break;
            }

            case ChatIntent.CreateGameProfile:
                b.AppendLine("Ein Spielprofil legt fest, was beim Start eines bestimmten Spiels vorübergehend geändert wird – etwa Energieplan, Prozesspriorität und bevorzugte Grafikeinheit – und stellt beim Beenden alles wieder her.");
                b.AppendLine("Öffnen Sie „Gaming Boost“, wählen Sie das Spiel aus der erkannten Liste (oder fügen Sie es manuell hinzu) und stellen Sie das Profil zusammen. Die automatische Aktivierung müssen Sie pro Spiel ausdrücklich erlauben.");
                proposals.Add(Page("gaming", "Gaming Boost öffnen", "Spiel auswählen und Profil anlegen"));
                break;

            case ChatIntent.ListRiskyTweaks:
            {
                var risky = _catalog.All.Where(a => a.Metadata.Risk >= RiskLevel.Moderate || a.Metadata.IsExperimental).OrderByDescending(a => a.Metadata.Risk).ThenBy(a => a.Metadata.Name).ToList();
                b.AppendLine($"Diese {risky.Count} Änderungen haben mindestens ein mittleres Risiko oder gelten als experimentell:");
                foreach (var action in risky)
                    b.AppendLine($"• {action.Metadata.Name} – Risiko: {action.Metadata.Risk.ToGerman()}{(action.Metadata.IsExperimental ? " (experimentell)" : "")}{(action.Metadata.IsReversible ? "" : ", nicht umkehrbar")}");
                b.AppendLine();
                b.AppendLine("Keine davon wird automatisch ausgeführt. Experimentelle Änderungen sind nur einzeln im Expertenlabor möglich, nachdem Sie es ausdrücklich freigeschaltet haben.");
                proposals.Add(Page("advanced", "Expertenlabor öffnen", "Erläuterungen, Diagnosen und Einzelbestätigung"));
                break;
            }

            case ChatIntent.UndoLast:
            {
                var last = _lastChange();
                if (last is null)
                {
                    b.AppendLine("Im Verlauf gibt es derzeit keine aktive, umkehrbare Änderung – es ist nichts rückgängig zu machen.");
                    proposals.Add(Page("history", "Optimierungsverlauf anzeigen", "Alle bisherigen Änderungen"));
                }
                else
                {
                    b.AppendLine($"Die letzte aktive Änderung ist „{last.ActionName}“ vom {last.Timestamp.ToLocalTime().ToString("g", De)} ({last.OriginalDisplay} → {last.IntendedDisplay}).");
                    b.AppendLine("Bestätigen Sie unten, wenn ich den ursprünglichen Zustand wiederherstellen soll. Bis dahin wurde nichts verändert.");
                    proposals.Add(new ChatProposal { Kind = ProposalKind.RollbackLast, Title = "Letzte Änderung rückgängig machen", Description = $"„{last.ActionName}“ zurücksetzen auf: {last.OriginalDisplay}" });
                    kind = Core.Chat.ChatMessageKind.PlannedAction;
                }
                break;
            }

            case ChatIntent.SlowInternet:
                b.AppendLine($"Aktueller Durchsatz: Download {(s.Latest?.NetworkDownBytesPerSecond is { } d ? ByteFormat.Rate(d) : "nicht verfügbar")}, Upload {(s.Latest?.NetworkUpBytesPerSecond is { } u ? ByteFormat.Rate(u) : "nicht verfügbar")}.");
                b.AppendLine($"Latenz zum Testziel: {(s.Latest?.LatencyMs is { } latency ? latency.ToString("0", De) + " ms" : "nicht gemessen (die Latenzmessung ist standardmäßig aus und kann in den Einstellungen mit von Ihnen freigegebenen Zielen aktiviert werden)")}.");
                var adapters = info.NetworkAdapters.Where(a => a.IsUp).ToList();
                foreach (var a in adapters.Take(3)) b.AppendLine($"• Adapter „{a.Name}“ ({a.Type}){(a.SpeedBitsPerSecond is { } speed ? $", Verbindungsgeschwindigkeit {speed / 1_000_000} Mbit/s" : "")}");
                b.AppendLine();
                b.AppendLine("Typische Ursachen für langsames Internet liegen außerhalb von Windows-Einstellungen: schwaches WLAN-Signal, ausgelasteter Router, Downloads anderer Geräte oder der Anschluss selbst. Die Netzwerkdiagnose misst Paketverlust, Jitter und DNS-Antwortzeit und zeigt, wo es hakt.");
                b.AppendLine("Ein anderer DNS-Server beschleunigt nur die Namensauflösung, nicht die Verbindung in Spielen.");
                if (_catalog.Contains(SystemToolCatalog.DnsFlush))
                    proposals.Add(new ChatProposal { Kind = ProposalKind.Action, Title = "DNS-Cache leeren", Description = "Hilft bei veralteten oder fehlerhaften Namensauflösungen", Requests = new[] { new PlanRequest(SystemToolCatalog.DnsFlush) } });
                proposals.Add(Page("network", "Netzwerkdiagnose öffnen", "Ping, Paketverlust, Jitter, DNS-Zeit und Traceroute"));
                kind = Core.Chat.ChatMessageKind.Recommendation;
                break;

            case ChatIntent.Temperatures:
                b.AppendLine($"ACPI-Thermalzone: {(s.Latest?.ThermalZoneCelsius is { } zone ? zone.ToString("0", De) + " °C" : "nicht verfügbar – dieses System stellt den Wert nicht bereit")}.");
                b.AppendLine($"CPU-Kerntemperatur: {(s.Latest?.CpuPackageCelsius is { } pkg ? pkg.ToString("0", De) + " °C" : "nicht verfügbar – Windows bietet dafür keine allgemeine Schnittstelle; dazu wäre ein Sensor-Plugin des Hardwareherstellers nötig")}.");
                b.AppendLine($"GPU-Temperatur: {(s.Latest?.GpuTemperatureCelsius is { } gpu ? gpu.ToString("0", De) + " °C" : "nicht verfügbar (wird derzeit nur für NVIDIA-Grafikkarten über die Treiberschnittstelle ausgelesen)")}.");
                b.AppendLine(s.ThermalLimitObserved
                    ? "Windows meldet eine zeitweise Begrenzung der Prozessorleistung – ein Hinweis auf ein Wärme- oder Energielimit. Prüfen Sie Kühlung und Lüfter."
                    : "Es wurde keine Begrenzung der Prozessorleistung beobachtet.");
                b.AppendLine("Ich erfinde keine Temperaturwerte: Was nicht messbar ist, bleibt als „nicht verfügbar“ ausgewiesen. Im System-Monitor können Sie Warnungen mit eigenen Schwellenwerten einrichten.");
                proposals.Add(Page("monitor", "System-Monitor öffnen", "Verlauf ansehen und Temperaturwarnungen einrichten"));
                break;

            case ChatIntent.AutomationPlan:
                b.AppendLine("Ein automatischer Optimierungsplan besteht aus Regeln der Form „Auslöser → Bedingungen → Aktionen“. Sinnvolle Beispiele:");
                b.AppendLine("• Spiel gestartet + Netzbetrieb → Gaming-Profil aktivieren; Spiel beendet → vorherige Einstellungen wiederherstellen.");
                b.AppendLine("• Wechsel auf Akkubetrieb → Profil „Akkusparen“; Netzteil angeschlossen → „Ausbalanciert“.");
                b.AppendLine("• CPU-Temperatur dauerhaft hoch → Benachrichtigung statt Leistungsprofil.");
                b.AppendLine();
                b.AppendLine("Automatisch laufen ausschließlich umkehrbare Aktionen bis Risikostufe „Niedrig“. Abklingzeiten und eine Pendelsperre verhindern ständiges Hin- und Herschalten, widersprüchliche Regeln werden im Editor angezeigt. Die Automatisierung ist standardmäßig ausgeschaltet.");
                proposals.Add(Page("automation", "Automatisierungszentrale öffnen", "Regeln anlegen und Vorlagen verwenden"));
                break;

            case ChatIntent.Storage:
                foreach (var v in info.Volumes) b.AppendLine($"• Laufwerk {v.Letter}: {ByteFormat.Format(v.FreeBytes)} frei von {ByteFormat.Format(v.TotalBytes)} ({v.FreePercent.ToString("0", De)} % frei)");
                if (info.Volumes.Any(v => v.FreePercent < 12)) b.AppendLine("Mindestens ein Laufwerk ist knapp. Die Bereinigung zeigt vorab genau, welche Dateien gelöscht würden – persönliche Dateien werden nie automatisch angefasst.");
                else b.AppendLine("Der freie Speicherplatz ist unkritisch.");
                b.AppendLine("Bereinigungen sind nicht umkehrbar und werden deshalb nie automatisch ausgeführt.");
                proposals.Add(Page("cleanup", "Windows-Bereinigung öffnen", "Kategorien prüfen und gezielt bereinigen"));
                proposals.Add(Page("storage", "Speicheroptimierung öffnen", "Laufwerkszustand, große Dateien und Duplikate"));
                break;

            case ChatIntent.Startup:
            {
                var enabled = s.StartupEntries.Where(e => e.Enabled).ToList();
                b.AppendLine($"Es starten {enabled.Count} Programme automatisch mit Windows:");
                foreach (var e in enabled.Take(12)) b.AppendLine($"• {e.DisplayName}{(string.IsNullOrEmpty(e.Publisher) ? "" : $" ({e.Publisher})")}");
                if (enabled.Count > 12) b.AppendLine($"… und {enabled.Count - 12} weitere.");
                b.AppendLine("Im Autostart-Manager lassen sich Einträge deaktivieren, ohne sie zu löschen – das ist jederzeit umkehrbar. Sicherheitssoftware und Treiberdienste sollten aktiv bleiben.");
                proposals.Add(Page("startup", "Autostart-Manager öffnen", "Einträge prüfen und deaktivieren"));
                break;
            }

            case ChatIntent.Power:
                b.AppendLine($"Aktiver Energieplan: {s.ActivePowerPlan?.Name ?? "unbekannt"}. Stromversorgung: {(s.Context.OnBatteryPower ? "Akkubetrieb" : "Netzbetrieb")}{(s.Latest?.BatteryPercent is { } pct ? $", Akkustand {pct} %" : "")}.");
                b.AppendLine("Verfügbare Pläne: " + (s.PowerPlans.Count == 0 ? "nicht ermittelbar" : string.Join(", ", s.PowerPlans.Select(p => p.Name))) + ".");
                b.AppendLine("Leistungsorientierte Pläne erhöhen Verbrauch, Abwärme und Lüftergeräusch; sparsame Pläne drosseln den Prozessor. „Ausbalanciert“ ist für die meisten Situationen die beste Wahl.");
                AddActions("Energieplan-Empfehlung prüfen", "Basierend auf Stromversorgung und Ziel", _advisor.Analyze(s, goal).Where(r => r.Area == ActionCategory.Power).SelectMany(r => r.Actions));
                proposals.Add(Page("power", "Energieverwaltung öffnen", "Pläne vergleichen und Einstellungen anpassen"));
                break;

            case ChatIntent.Privacy:
            {
                var open = RegistryTweakCatalog.All.Where(t => t.Tags.Contains(RegistryTweakCatalog.TagPrivacy) && t.Tags.Contains(RegistryTweakCatalog.TagSafe) && s.ActionStates.GetValueOrDefault(t.ActionId) == false).ToList();
                b.AppendLine(open.Count == 0
                    ? "Die risikoarmen Datenschutzeinstellungen (Werbe-ID, Vorschläge, Tipps) sind bereits gesetzt."
                    : $"{open.Count} risikoarme Datenschutzeinstellungen stehen noch auf dem Windows-Standard, zum Beispiel: {string.Join(", ", open.Take(4).Select(t => t.Name))}.");
                b.AppendLine("Wichtig: Telemetrie lässt sich reduzieren, aber nicht vollständig entfernen. Windows Update, Defender und SmartScreen werden von dieser Anwendung nicht abgeschaltet.");
                AddActions("Datenschutz-Grundeinstellungen anwenden", "Ohne Funktionsverlust, jederzeit umkehrbar", open.Select(t => new PlanRequest(t.ActionId, ActionParameters.Empty, t.ExpectedBehavior)));
                proposals.Add(Page("privacy", "Datenschutz-Zentrum öffnen", "Alle Datenschutzeinstellungen mit Erklärung"));
                break;
            }

            case ChatIntent.SystemInfo:
                b.AppendLine($"Windows: {info.OsName} {info.OsDisplayVersion} (Build {info.OsBuild})");
                b.AppendLine($"Prozessor: {info.CpuName} – {info.CpuCores?.ToString() ?? "?"} Kerne, {info.CpuLogicalProcessors} Threads");
                foreach (var g in info.Gpus) b.AppendLine($"Grafik: {g.Name}{(g.DedicatedMemoryBytes is { } vram ? $" ({ByteFormat.Format(vram)})" : "")}");
                b.AppendLine($"Arbeitsspeicher: {ByteFormat.Format(info.MemoryTotalBytes)}{(info.MemorySpeedMhz is { } mhz ? $" bei {mhz} MHz" : "")}");
                foreach (var drive in info.Drives) b.AppendLine($"Datenträger: {drive.Model} ({drive.MediaType}, {ByteFormat.Format(drive.SizeBytes)})");
                proposals.Add(Page("dashboard", "Dashboard öffnen", "Alle Systeminformationen"));
                break;

            default:
                b.AppendLine("Das habe ich leider nicht verstanden. Ohne eingerichtetes Sprachmodell erkenne ich nur bestimmte Themen. Versuchen Sie es zum Beispiel mit:");
                b.AppendLine("• „Warum ist meine CPU-Auslastung so hoch?“");
                b.AppendLine("• „Welche Programme verlangsamen meinen PC?“");
                b.AppendLine("• „Optimiere meinen PC für Gaming.“");
                b.AppendLine("• „Mache die letzte Optimierung rückgängig.“");
                b.AppendLine("In den Einstellungen können Sie optional ein lokales Sprachmodell (Ollama) oder einen Cloud-Dienst einrichten, um frei formulierte Fragen zu stellen.");
                break;
        }

        return new RuleBasedAnswer(b.ToString().TrimEnd(), proposals, kind);
    }
}
