# Sicherheitsmodell

- **Rechtetrennung**: Die Oberfläche läuft mit Standardrechten (`asInvoker`). Administratoraktionen laufen in `NexusOptimizer.Elevated.exe`, gestartet über die sichtbare UAC-Abfrage – nie still.
- **Pipe-Absicherung**: zufälliger Pipe-Name, ACL auf die Client-SID, Prüfung von Client-PID und Programmpfad (Geschwisterdatei `NexusOptimizer.App.exe`), Protokollversion, Längenbegrenzung. Der Helfer validiert jede Anfrage erneut und lehnt Nicht-Administrator-Aktionen ab; Leerlauf-Timeout 10 min.
- **Keine freien Befehle**: weder Shell- noch Registry-Freitext. Parameter werden gegen `ParameterSpec` (Typ, Bereich, Muster, Länge) geprüft; Werkzeuge (SFC, DISM …) laufen mit festen Argumenten.
- **Registry-Schutz**: `RegistryPathGuard` sperrt Defender, Firewall, UAC, LSA, Secure Boot, Device Guard, Run-Schlüssel, IFEO, Winlogon u. a.; explizite 64/32-Bit-Sichten.
- **Dienste/Prozesse**: geschützte Dienste (Sicherheit, Updates, Anmeldung, Wiederherstellung) und Windows-/Sicherheitsprozesse sind nicht änderbar.
- **Sicherheitsfunktionen** werden nie automatisch deaktiviert; sicherheitsrelevante Werte sind nur lesbar, mit geführter manueller Anleitung.
- **Geheimnisse**: API-Schlüssel per DPAPI im Benutzerprofil, nie im Klartext, nie in Protokollen (`LogRedactor`). Keine fest eincodierten Zugangsdaten.
- **KI**: lokal zuerst; Cloud nur mit Opt-in und Vorschau des gesendeten Kontexts; nie Passwörter, Tokens, Dokumente, Benutzer-/Computernamen. Ratenbegrenzung gegen Kostenfallen. Modelltext = nicht vertrauenswürdige Eingabe.
- **Updates**: Manifest-Signaturprüfung und SHA-256-Paketprüfung implementiert; ein Download-/Installationsweg ist **nicht** umgesetzt.
- **Signierung**: Es wird **keine** Signatur behauptet. Binärdateien sind unsigniert, bis ein Zertifikat verwendet wurde (siehe INSTALLER.md).
- **Datenschutz**: keine versteckte Telemetrie; alle Daten lokal unter `%LocalAppData%\NexusOptimizerAI`; „Alle Anwendungsdaten löschen“ in den Einstellungen.
