# Installer und Signierung

`installer/NexusOptimizerAI.iss` (Inno Setup 6) installiert die veröffentlichte win-x64-Ausgabe nach `%ProgramFiles%\NEXUS Optimizer AI`.

- Anwendung startet mit Standardrechten; `NexusOptimizer.Elevated.exe` liegt daneben und wird nur per UAC gestartet.
- Autostart wird **nicht** vom Installer gesetzt (nur auf ausdrücklichen Wunsch in den Einstellungen).
- **Deinstallation** entfernt Programmdateien und den Autostart-Eintrag. Benutzerdaten (`%LocalAppData%\NexusOptimizerAI`) bleiben auf Nachfrage erhalten oder werden gelöscht; **Windows-Änderungen werden nicht automatisch zurückgenommen** – vorher im Bereich „Sicherung & Wiederherstellung“ zurücksetzen.
- Der Installer wurde **nicht gebaut und nicht getestet** (Inno Setup ist unter Linux nicht verfügbar).

## Code-Signierung
Es ist **kein** Zertifikat hinterlegt; die Dateien sind unsigniert. Zum Signieren (mit eigenem Zertifikat):
```
signtool sign /fd SHA256 /tr <Zeitstempelserver> /td SHA256 /f <zertifikat.pfx> NexusOptimizer.App.exe NexusOptimizer.Elevated.exe
```
Erst danach darf eine Signatur behauptet werden.
