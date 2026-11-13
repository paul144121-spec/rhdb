# NEXUS Optimizer AI

Windows-Optimierer mit ehrlicher, überprüfbarer Arbeitsweise: **Analyse → Vorschau → Bestätigung → Sicherung → Ausführung → Verifikation → Rückgängig-Möglichkeit**.
Technik: C# / .NET 10 (LTS), WPF (MVVM, Dependency Injection), SQLite, optionale lokale (Ollama) oder Cloud-KI (Claude, OpenAI-kompatibel).

> **Entwicklungsstand 0.1.0 – kein fertiges Produkt.** Was umgesetzt, was nur kompiliert und was getestet ist, steht ehrlich in
> [docs/TESTSTATUS.md](docs/TESTSTATUS.md). Die Anwendung wurde auf Linux entwickelt und noch **nicht auf einem echten Windows-System** ausgeführt.

## Dokumentation
| Datei | Inhalt |
|---|---|
| [docs/ARCHITEKTUR.md](docs/ARCHITEKTUR.md) | Aufbau, Technikwahl, Ausführungs-Engine, Rückgängig-Mechanik |
| [docs/SICHERHEIT.md](docs/SICHERHEIT.md) | Sicherheitsmodell, Rechtetrennung, KI-Grenzen, Datenschutz |
| [docs/BUILD.md](docs/BUILD.md) | Bauen, Testen, Veröffentlichen |
| [docs/TESTSTATUS.md](docs/TESTSTATUS.md) | Verifiziert / nicht verifiziert / nicht umgesetzt |
| [docs/INSTALLER.md](docs/INSTALLER.md) | Installer, Deinstallation, Code-Signierung |

## Schnellstart (Windows 10/11 x64, .NET-10-SDK)
```powershell
dotnet build NexusOptimizer.sln -c Release
dotnet test tests\NexusOptimizer.Core.Tests
dotnet run --project src\NexusOptimizer.App
```
