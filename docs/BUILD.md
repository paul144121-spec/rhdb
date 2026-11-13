# Bauen und Testen

Voraussetzung: .NET-10-SDK. Bauen unter Windows oder Linux (WPF wird unter Linux nur per `EnableWindowsTargeting` kompiliert, aber nicht ausgeführt).

```bash
dotnet restore
dotnet build NexusOptimizer.sln -c Release
dotnet test tests/NexusOptimizer.Core.Tests            # 366 Tests, plattformneutral
dotnet test tests/NexusOptimizer.Windows.Tests         # nur unter Windows (Registry mit Wegwerf-Schlüsseln)
dotnet publish src/NexusOptimizer.App -c Release -r win-x64 --self-contained true -o publish
```

`NexusOptimizer.Elevated.exe` wird mitgebaut und neben die Anwendung kopiert.

## Oberflächen-Selbsttest
```
NexusOptimizer.App.exe --smoke-test <Ordner> [--data-dir <Ordner>]
```
Lädt jede Seite, speichert PNGs, protokolliert Ladefehler/Bindungsfehler in `smoke.json`. Verändert nichts am System (öffnet nur eine abgebrochene Vorschau).

## CI
`.github/workflows/nexus-optimizer.yml` (windows-latest: Build + Tests). Bisher **nie ausgeführt**.
