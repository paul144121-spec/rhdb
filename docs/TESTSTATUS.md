# Teststatus – ehrlich

Stand: Entwicklung ausschließlich auf Linux. **Kein Test auf echtem Windows, keine Hardwaretests.**

## Verifiziert
| Was | Wie |
|---|---|
| Domänenlogik, Planer, Executor, Rollback, Risikorichtlinie, Registry-Engine (In-Memory), Regelwerk, Profile, Automatisierung, Benchmark-Statistik, KI-Parser/Chat mit Fakes, Pipe-Protokoll, Log-Schwärzung | 366 Unit-Tests (xUnit), grün |
| Windows-Registry-Zugriff, SQLite-Journal, Rollback, DPAPI, Prozessliste, Dienste/Autostart-Lesen | Ausführung der veröffentlichten win-x64-Binärdateien unter **Wine** (Probe: 14 von 15 Bereichen; Energie-API in Wine nicht implementiert) |
| Oberfläche: alle 27 Bereiche laden, keine Bindungsfehler, keine unbehandelten Ausnahmen; Vorschaudialog; helles Design | `--smoke-test` unter Wine + Xvfb, Bildschirmfotos geprüft |

Wine-Messwerte (z. B. „nicht verfügbar“ bei GPU/Datenträger) sind Wine-Eigenheiten und sagen nichts über echtes Windows.

| GitHub Actions auf windows-latest: Build Release (inkl. WPF, Elevated), 366 Core-Tests, Windows-Tests (Registry, Pipe, SQLite, DPAPI, Dienste, Hardware, Messwerte) | Erster Lauf: 406 von 407 grün; der eine Fehlschlag war eine Testverwechslung (zwei Klassen löschten denselben Registry-Ordner parallel) und ist behoben – Bestätigung durch erneuten Lauf steht aus |

## Kompiliert, aber nicht ausgeführt
- Administratorkomponente inkl. UAC-Start und Pipe-Authentifizierung.
- PDH-Zähler, WMI-Abfragen, NVML, Energie-API (`powrprof`), SCM-Änderungen, Systemwiederherstellung, SFC/DISM/CHKDSK-Läufe, Bereinigung echter Ordner, DNS-Änderung, Autostart-Umschalten auf echtem System.
- Anthropic-/Ollama-/OpenAI-Anbieter: nur Kompilierung und Tests mit Fake-HTTP; **kein echter Aufruf**.
- PresentMon-Anbindung, Spielerkennung (Steam/Epic/Xbox) gegen echte Installationen.

## Nicht umgesetzt
Siehe ARCHITEKTUR.md („Nicht umgesetzt / offen“). Funktionen, die Hardware voraussetzen (GPU-Sensoren, SMART, Akku), wurden mangels Hardware nicht getestet.

Nichts davon ist als „verifiziert“ zu verstehen, solange es hier nicht unter „Verifiziert“ steht.
