# Architektur

## Technikwahl
- **C# / .NET 10 (LTS)**: aktuelle, unterstützte Langzeitversion; direkter Zugriff auf Windows-APIs (P/Invoke, WMI/CIM, PDH, SCM, Registry, DPAPI).
- **WPF**: ausgereiftes natives Windows-UI mit MVVM; eigenes dunkles/helles Design, eigene Vektorsymbole (schriftartunabhängig).
- **SQLite** (Microsoft.Data.Sqlite): lokales Transaktionsjournal, Messwerthistorie, Profile, Regeln, Verlauf.
- **Core ist plattformneutral** und deshalb auf Linux testbar (366 Unit-Tests mit Fakes); nur `Windows`/`Elevated`/`App` sind Windows-spezifisch.

## Projekte
| Projekt | Aufgabe |
|---|---|
| `NexusOptimizer.Core` | Domäne, Aktionsmodell, Planer, Executor, Rollback, Risikorichtlinie, Regelwerk, Profile, Automatisierung, Benchmarks, Pipe-Protokoll |
| `NexusOptimizer.Data` | SQLite-Speicher (Journal, Einstellungen, Verlauf …) |
| `NexusOptimizer.AI` | Anbieter (Ollama, OpenAI-kompatibel, Claude), Kontextaufbau, Vorschlagsprüfung, deutscher Chat |
| `NexusOptimizer.Windows` | Windows-Implementierungen (Registry, Energie, Dienste, PDH, WMI, Bereinigung …) |
| `NexusOptimizer.Elevated` | Separate Administratorkomponente (UAC), Named Pipe, nur Allowlist-Aktionen |
| `NexusOptimizer.App` | WPF-Oberfläche, 27 Bereiche, Dialog- und Ablaufsteuerung |
| `tools/NexusOptimizer.Probe` | Nur lesende Prüfung der Windows-Schicht (auch unter Wine) |

## Ausführungs-Engine
1. **ActionCatalog** – feste Allowlist (`reg.*`, Energie, Dienste, Autostart, Werkzeuge …). Nur hier Registriertes ist ausführbar; es gibt keinen generischen Befehlsendpunkt.
2. **OptimizationPlanner** – prüft Parameter, Kompatibilität, Risiko (`RiskPolicy`), Abhängigkeiten, Konflikte, Reihenfolge; erzeugt Vorschauen. Nur er kann einen `OptimizationPlan` erzeugen.
3. **OptimizationExecutor** – Journal-Zustände `Pending → BackedUp → Applied → Verified | VerificationFailed | Failed`; ein begonnener Schritt wird nie mittendrin abgebrochen; Einzellauf-Sperre.
4. **RollbackService** – stellt gesicherte Ausgangszustände wieder her, erkennt Konflikte (zwischenzeitlich geänderte Werte), setzt unterbrochene Vorgänge beim Start fort bzw. markiert sie.
5. **RoutingActionRunner** – lokal oder über den privilegierten Gateway (UAC nur bei Bedarf, sichtbar).

Unbeaufsichtigte Quellen (Automatik, KI-Auto, Gaming) sind auf Risiko „Niedrig“ und `AllowInAutomation` begrenzt; experimentelle/kritische Aktionen nur durch Benutzer mit Freischaltung und Einzelbestätigung.

## Oberfläche
`ActionCoordinator` ist der einzige Weg von der UI zur Engine: planen → Vorschaudialog → Auswahl/Bestätigung → Ausführung mit Fortschritt → tatsächliches Ergebnis. Seiten rufen die Engine nie direkt auf.

## KI
Das Regelwerk (`RuleBasedAdvisor`) ist deterministisch und **kein** maschinelles Lernen; es wird überall so bezeichnet. Sprachmodelle erklären nur; Vorschläge werden als strukturierter, validierter Block geparst (nur Allowlist-Aktionen/Seiten). Modelltext wird nie ausgeführt.

## Nicht umgesetzt / offen
Aufgabenplanung (Task Scheduler), WLAN-Signalstatistik, Netzwerknutzung je Prozess, Update-Download (nur Signaturprüfung vorhanden), externe Plug-ins (nur Schnittstellen), CPU-Affinität, Energieplan-Export/-Import.
