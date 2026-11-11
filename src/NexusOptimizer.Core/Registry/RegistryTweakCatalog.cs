using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Registry;

/// <summary>
/// Stark typisierter Katalog aller Registrierungs-Tweaks. Aufgenommen werden nur Einstellungen, die Windows selbst
/// über Einstellungen/Richtlinien setzt oder die von Microsoft dokumentiert sind. Verbreitete „Internet-Mythen“
/// ohne belegte Wirkung (z. B. Win32PrioritySeparation, TCP-ACK-Tweaks) sind bewusst nicht enthalten.
/// </summary>
public static class RegistryTweakCatalog
{
    public const string TagSafe = "safe";
    public const string TagBalanced = "balanced";
    public const string TagGaming = "gaming";
    public const string TagPerformance = "performance";
    public const string TagPrivacy = "privacy";
    public const string TagPreference = "preference";
    public const string TagExpert = "expert";
    public const string TagLowEnd = "low-end";

    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";
    private const string ExplorerAdvanced = CurrentVersion + @"\Explorer\Advanced";
    private const string ContentDelivery = CurrentVersion + @"\ContentDeliveryManager";
    private const string SystemProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    private static readonly RegistryValueData Zero = RegistryValueData.DWord(0);
    private static readonly RegistryValueData One = RegistryValueData.DWord(1);

    public static IReadOnlyList<RegistryTweakDefinition> All { get; } = Build();

    public static RegistryTweakDefinition? Find(string id) => All.FirstOrDefault(t => t.Id == id || t.ActionId == id);

    private static RegistryTweakDefinition User(string id, string name, string description, ActionCategory category, string key, string value,
        RegistryValueData optimized, RegistryValueData? windowsDefault, string optimizedLabel, string defaultLabel, string expected,
        string sideEffects = "Keine bekannten Nebenwirkungen.", RiskLevel risk = RiskLevel.VeryLow, RestartRequirement restart = RestartRequirement.None,
        WindowsVersionRange? versions = null, params string[] tags)
        => new()
        {
            Id = id, Name = name, Description = description, Category = category, Root = RegistryRoot.CurrentUser, KeyPath = key, ValueName = value,
            OptimizedValue = optimized, WindowsDefault = windowsDefault, OptimizedLabel = optimizedLabel, DefaultLabel = defaultLabel,
            ExpectedBehavior = expected, SideEffects = sideEffects, Risk = risk, Restart = restart, Versions = versions ?? WindowsVersionRange.All, Tags = tags,
        };

    private static RegistryTweakDefinition Machine(string id, string name, string description, ActionCategory category, string key, string value,
        RegistryValueData optimized, RegistryValueData? windowsDefault, string optimizedLabel, string defaultLabel, string expected,
        string sideEffects, RiskLevel risk, RestartRequirement restart = RestartRequirement.None, WindowsVersionRange? versions = null, params string[] tags)
        => new()
        {
            Id = id, Name = name, Description = description, Category = category, Root = RegistryRoot.LocalMachine, KeyPath = key, ValueName = value,
            OptimizedValue = optimized, WindowsDefault = windowsDefault, OptimizedLabel = optimizedLabel, DefaultLabel = defaultLabel,
            ExpectedBehavior = expected, SideEffects = sideEffects, Risk = risk, Restart = restart, Versions = versions ?? WindowsVersionRange.All, Tags = tags,
        };

    private static IReadOnlyList<RegistryTweakDefinition> Build()
    {
        var win11 = WindowsVersionRange.Windows11Only;
        var win10 = WindowsVersionRange.Windows10Only;
        var list = new List<RegistryTweakDefinition>
        {
            // ───────────── Explorer ─────────────
            User("explorer.show-file-extensions", "Dateinamenerweiterungen anzeigen",
                "Zeigt im Explorer die Endungen bekannter Dateitypen (z. B. „.exe“, „.pdf“) an. Das erleichtert das Erkennen getarnter ausführbarer Dateien.",
                ActionCategory.Explorer, ExplorerAdvanced, "HideFileExt", Zero, One, "Erweiterungen sichtbar", "Erweiterungen ausgeblendet",
                "Dateiendungen werden in allen Explorer-Fenstern angezeigt.", restart: RestartRequirement.ExplorerRestart, tags: new[] { TagSafe, TagBalanced, TagPreference }),
            User("explorer.show-hidden-files", "Versteckte Dateien anzeigen",
                "Blendet versteckte Dateien und Ordner im Explorer ein. Geschützte Systemdateien bleiben weiterhin ausgeblendet.",
                ActionCategory.Explorer, ExplorerAdvanced, "Hidden", One, RegistryValueData.DWord(2), "Versteckte Elemente sichtbar", "Versteckte Elemente ausgeblendet",
                "Versteckte Elemente erscheinen halbtransparent im Explorer.", "Mehr sichtbare Elemente; versehentliches Löschen versteckter Dateien wird wahrscheinlicher.",
                restart: RestartRequirement.ExplorerRestart, tags: new[] { TagPreference }),
            User("explorer.launch-to-this-pc", "Explorer mit „Dieser PC“ öffnen",
                "Öffnet neue Explorer-Fenster mit der Laufwerksübersicht statt mit dem Schnellzugriff bzw. der Startseite.",
                ActionCategory.Explorer, ExplorerAdvanced, "LaunchTo", One, RegistryValueData.DWord(2), "Start in „Dieser PC“", "Start im Schnellzugriff / auf der Startseite",
                "Neue Explorer-Fenster zeigen direkt die Laufwerke.", tags: new[] { TagPreference }),
            User("explorer.separate-process", "Ordnerfenster in eigenem Prozess starten",
                "Startet Ordnerfenster in einem vom Desktop getrennten Prozess. Ein hängendes Ordnerfenster reißt dann Taskleiste und Desktop nicht mit.",
                ActionCategory.Explorer, ExplorerAdvanced, "SeparateProcess", One, Zero, "Getrennter Prozess", "Gemeinsamer Prozess",
                "Bessere Fehlerisolierung des Explorers.", "Etwas höherer Arbeitsspeicherbedarf pro geöffnetem Ordnerfenster.",
                restart: RestartRequirement.ExplorerRestart, tags: new[] { TagPreference }),
            User("explorer.compact-view", "Kompakte Ansicht im Explorer",
                "Verringert die Abstände zwischen Elementen im Windows-11-Explorer, sodass mehr Einträge sichtbar sind.",
                ActionCategory.Explorer, ExplorerAdvanced, "UseCompactMode", One, Zero, "Kompakte Ansicht", "Standardabstände",
                "Dichtere Darstellung der Dateiliste.", "Kleinere Zeilenabstände sind per Touch schwerer zu treffen.", versions: win11, tags: new[] { TagPreference }),
            User("explorer.disable-sync-provider-ads", "Hinweise von Synchronisierungsanbietern ausblenden",
                "Unterdrückt Werbe- und Hinweisbanner (z. B. für OneDrive) im Explorer.",
                ActionCategory.Notifications, ExplorerAdvanced, "ShowSyncProviderNotifications", Zero, One, "Hinweise ausgeblendet", "Hinweise eingeblendet",
                "Keine Anbieterhinweise mehr im Explorer.", "Auch nützliche Hinweise des Synchronisierungsanbieters entfallen.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),

            // ───────────── Oberfläche / visuelle Effekte ─────────────
            User("visual.disable-transparency", "Transparenzeffekte deaktivieren",
                "Schaltet die halbtransparenten Flächen von Startmenü, Taskleiste und Fenstern ab. Entlastet vor allem schwächere Grafikeinheiten geringfügig.",
                ActionCategory.VisualEffects, CurrentVersion + @"\Themes\Personalize", "EnableTransparency", Zero, One, "Transparenz aus", "Transparenz an",
                "Flächen werden deckend dargestellt; minimal weniger GPU-Last bei der Desktopdarstellung.", "Die Oberfläche wirkt weniger modern.",
                tags: new[] { TagLowEnd, TagPerformance }),
            User("visual.disable-window-animations", "Fensteranimationen beim Minimieren/Maximieren deaktivieren",
                "Fenster erscheinen und verschwinden ohne Animation. Das fühlt sich auf langsamen Systemen direkter an.",
                ActionCategory.VisualEffects, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", RegistryValueData.Text("0"), RegistryValueData.Text("1"), "Animationen aus", "Animationen an",
                "Minimieren und Maximieren erfolgen ohne Übergang.", "Übergänge wirken abrupter.", restart: RestartRequirement.SignOut, tags: new[] { TagLowEnd, TagPerformance }),
            User("visual.disable-taskbar-animations", "Taskleistenanimationen deaktivieren",
                "Schaltet Animationen in der Taskleiste ab.",
                ActionCategory.VisualEffects, ExplorerAdvanced, "TaskbarAnimations", Zero, One, "Animationen aus", "Animationen an",
                "Taskleistensymbole reagieren ohne Übergangsanimation.", tags: new[] { TagLowEnd, TagPerformance }),
            User("visual.disable-aero-peek", "Desktopvorschau (Aero Peek) deaktivieren",
                "Deaktiviert die Desktopvorschau beim Zeigen auf das rechte Ende der Taskleiste.",
                ActionCategory.VisualEffects, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", Zero, One, "Vorschau aus", "Vorschau an",
                "Keine Desktopvorschau mehr.", tags: new[] { TagLowEnd }),
            User("responsiveness.menu-show-delay", "Menüverzögerung verkürzen",
                "Verkürzt die Wartezeit, bevor Untermenüs aufklappen, von 400 ms auf 100 ms.",
                ActionCategory.Responsiveness, @"Control Panel\Desktop", "MenuShowDelay", RegistryValueData.Text("100"), RegistryValueData.Text("400"), "100 ms", "400 ms",
                "Untermenüs öffnen sich schneller.", "Untermenüs können beim Überfahren ungewollt aufklappen.", restart: RestartRequirement.SignOut,
                tags: new[] { TagBalanced, TagPerformance, TagPreference }),
            User("interface.dark-app-theme", "Dunkles App-Design verwenden",
                "Stellt Apps, die das Windows-Farbschema beachten, auf das dunkle Design um.",
                ActionCategory.Interface, CurrentVersion + @"\Themes\Personalize", "AppsUseLightTheme", Zero, One, "Dunkles Design", "Helles Design",
                "Unterstützte Apps erscheinen dunkel.", tags: new[] { TagPreference }),

            // ───────────── Taskleiste / Suche ─────────────
            User("taskbar.align-left", "Taskleiste linksbündig ausrichten",
                "Richtet Startschaltfläche und Symbole der Windows-11-Taskleiste links aus.",
                ActionCategory.Taskbar, ExplorerAdvanced, "TaskbarAl", Zero, One, "Linksbündig", "Zentriert", "Die Taskleiste entspricht dem klassischen Layout.",
                versions: win11, tags: new[] { TagPreference }),
            User("taskbar.hide-task-view", "Schaltfläche „Aktive Anwendungen“ ausblenden",
                "Entfernt die Taskansicht-Schaltfläche aus der Taskleiste. Die Tastenkombination Win+Tab funktioniert weiterhin.",
                ActionCategory.Taskbar, ExplorerAdvanced, "ShowTaskViewButton", Zero, One, "Ausgeblendet", "Eingeblendet", "Mehr Platz in der Taskleiste.",
                tags: new[] { TagPreference }),
            User("taskbar.end-task", "„Task beenden“ im Taskleisten-Kontextmenü",
                "Ergänzt das Kontextmenü von Taskleistensymbolen um den Eintrag „Task beenden“.",
                ActionCategory.Taskbar, ExplorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", One, Zero, "Aktiviert", "Deaktiviert",
                "Hängende Programme lassen sich direkt über die Taskleiste beenden.", "Ungespeicherte Daten des beendeten Programms gehen verloren.",
                versions: new WindowsVersionRange(22631), tags: new[] { TagPreference }),
            User("search.taskbar-icon-only", "Suchfeld der Taskleiste auf Symbol verkleinern",
                "Zeigt statt des breiten Suchfelds nur ein Suchsymbol in der Taskleiste.",
                ActionCategory.Search, CurrentVersion + @"\Search", "SearchboxTaskbarMode", One, null, "Nur Symbol", "Suchfeld",
                "Mehr Platz in der Taskleiste.", tags: new[] { TagPreference }),
            User("search.disable-search-highlights", "Such-Highlights deaktivieren",
                "Blendet wechselnde Illustrationen und Online-Inhalte im Suchfeld und im Suchfenster aus.",
                ActionCategory.Search, CurrentVersion + @"\SearchSettings", "IsDynamicSearchBoxEnabled", Zero, One, "Highlights aus", "Highlights an",
                "Das Suchfenster lädt keine Tagesinhalte mehr.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            new RegistryTweakDefinition
            {
                Id = "search.disable-web-suggestions", Name = "Websuchvorschläge im Startmenü deaktivieren",
                Description = "Die Windows-Suche liefert nur noch lokale Ergebnisse und sendet Suchbegriffe nicht mehr an Bing. Wird über einen Benutzer-Richtlinienwert gesetzt.",
                Category = ActionCategory.Search, Root = RegistryRoot.CurrentUser, KeyPath = @"Software\Policies\Microsoft\Windows\Explorer", ValueName = "DisableSearchBoxSuggestions",
                OptimizedValue = One, WindowsDefault = Zero, OptimizedLabel = "Websuchvorschläge aus", DefaultLabel = "Websuchvorschläge an",
                ExpectedBehavior = "Die Suche reagiert meist direkter und überträgt keine Eingaben an den Suchdienst.",
                SideEffects = "Keine Webergebnisse im Startmenü; Windows kennzeichnet die Einstellung als verwaltet.",
                Risk = RiskLevel.Low, Restart = RestartRequirement.SignOut, Versions = new WindowsVersionRange(WindowsVersionRange.Windows10_2004),
                Tags = new[] { TagPrivacy, TagBalanced },
            },
            User("search.disable-bing-legacy", "Bing-Suche im Startmenü deaktivieren (ältere Windows-10-Versionen)",
                "Schaltet auf älteren Windows-10-Versionen die Bing-Integration der Suche ab.",
                ActionCategory.Search, CurrentVersion + @"\Search", "BingSearchEnabled", Zero, One, "Bing-Suche aus", "Bing-Suche an",
                "Nur lokale Suchergebnisse.", "Keine Webergebnisse im Startmenü.", versions: new WindowsVersionRange(WindowsVersionRange.Windows10FirstBuild, WindowsVersionRange.Windows10_2004 - 1),
                tags: new[] { TagPrivacy }),

            // ───────────── Gaming ─────────────
            User("gaming.enable-game-mode", "Windows-Spielmodus aktivieren",
                "Der Spielmodus priorisiert das Spiel im Vordergrund und unterdrückt währenddessen Treiberinstallationen durch Windows Update sowie Neustartaufforderungen.",
                ActionCategory.Gaming, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", One, One, "Spielmodus an", "Spielmodus aus",
                "Gleichmäßigere Ressourcenzuteilung für erkannte Spiele. Eine messbare Verbesserung ist system- und spielabhängig.",
                tags: new[] { TagSafe, TagBalanced, TagGaming, TagPerformance }),
            User("gaming.disable-background-recording", "Hintergrundaufzeichnung der Xbox Game Bar deaktivieren",
                "Deaktiviert die dauerhafte Spielaufzeichnung im Hintergrund („Aufzeichnen, was passiert ist“). Diese Funktion belegt Encoder-, CPU- und Datenträgerressourcen.",
                ActionCategory.Gaming, @"System\GameConfigStore", "GameDVR_Enabled", Zero, One, "Hintergrundaufzeichnung aus", "Hintergrundaufzeichnung an",
                "Weniger Hintergrundlast während des Spielens, sofern die Aufzeichnung aktiv war.", "Rückwirkende Clips („die letzten 30 Sekunden“) sind nicht mehr möglich.",
                RiskLevel.Low, tags: new[] { TagBalanced, TagGaming, TagPerformance }),
            User("gaming.disable-app-capture", "Spielaufnahmen über die Game Bar deaktivieren",
                "Schaltet die Aufnahmefunktion (Clips, Screenshots, Übertragung) der Xbox Game Bar ab.",
                ActionCategory.Gaming, CurrentVersion + @"\GameDVR", "AppCaptureEnabled", Zero, One, "Aufnahme aus", "Aufnahme an",
                "Der Aufnahmedienst wird beim Spielstart nicht mehr vorbereitet.", "Aufnahmen und Screenshots über Win+Alt+R bzw. Win+Alt+Druck funktionieren nicht mehr.",
                RiskLevel.Low, tags: new[] { TagGaming, TagPerformance }),
            User("gaming.disable-game-bar-tips", "Tipps der Game Bar beim Spielstart ausblenden",
                "Unterdrückt das Hinweisfenster der Xbox Game Bar beim Start eines Spiels.",
                ActionCategory.Gaming, @"Software\Microsoft\GameBar", "ShowStartupPanel", Zero, One, "Tipps aus", "Tipps an",
                "Keine Einblendung beim Spielstart.", tags: new[] { TagSafe, TagBalanced, TagGaming }),
            new RegistryTweakDefinition
            {
                Id = "gpu.enable-hags", Name = "Hardwarebeschleunigte GPU-Planung aktivieren",
                Description = "Überträgt die Verwaltung des Grafikspeichers und der GPU-Warteschlange vom Betriebssystem an die Grafikkarte (HAGS). Nur verfügbar, wenn Grafikkarte und Treiber (WDDM 2.7 oder neuer) die Funktion unterstützen.",
                Category = ActionCategory.Gpu, Root = RegistryRoot.LocalMachine, KeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "HwSchMode",
                OptimizedValue = RegistryValueData.DWord(2), WindowsDefault = One, OptimizedLabel = "GPU-Planung an", DefaultLabel = "GPU-Planung aus",
                ExpectedBehavior = "Kann die Latenz in einigen Spielen senken und ist Voraussetzung für bestimmte Treiberfunktionen (z. B. Frame Generation). Die Wirkung ist stark spiel- und treiberabhängig und nicht garantiert.",
                SideEffects = "Auf einzelnen Systemen wurden Ruckler, höherer VRAM-Bedarf oder Probleme mit Aufnahme- und VR-Software beobachtet.",
                Risk = RiskLevel.Moderate, Restart = RestartRequirement.Reboot, Versions = new WindowsVersionRange(WindowsVersionRange.Windows10_2004),
                RequiresExistingValue = true, ExcludeFromAutomation = true, Tags = new[] { TagGaming, TagPerformance, TagExpert },
            },

            // ───────────── Energie / Hintergrund / Netzwerk / Speicher ─────────────
            Machine("power.disable-fast-startup", "Schnellstart deaktivieren",
                "Beim Schnellstart speichert Windows den Kernelzustand beim Herunterfahren und lädt ihn beim Start wieder. Ohne Schnellstart wird der Kernel bei jedem Start frisch initialisiert.",
                ActionCategory.Power, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", Zero, One, "Schnellstart aus", "Schnellstart an",
                "Herunterfahren setzt Treiber und Kernel vollständig zurück; das behebt manche Treiber- und Dual-Boot-Probleme.",
                "Der Kaltstart dauert insbesondere auf Festplatten länger.", RiskLevel.Low, tags: new[] { TagPreference }),
            User("background.disable-background-apps", "Hintergrundaktivität von Store-Apps deaktivieren",
                "Verhindert, dass Apps aus dem Microsoft Store im Hintergrund ausgeführt werden (globaler Schalter von Windows 10).",
                ActionCategory.BackgroundApps, CurrentVersion + @"\BackgroundAccessApplications", "GlobalUserDisabled", One, Zero, "Hintergrund-Apps aus", "Hintergrund-Apps an",
                "Weniger Hintergrundaktivität und Akkuverbrauch durch Store-Apps.", "Apps wie Mail, Kalender oder Wecker aktualisieren sich nicht mehr im Hintergrund und senden keine Benachrichtigungen.",
                RiskLevel.Low, versions: win10, tags: new[] { TagBalanced, TagPerformance }),
            Machine("network.delivery-optimization-no-peering", "Übermittlungsoptimierung: keine Uploads an andere PCs",
                "Windows-Updates werden nur noch direkt von Microsoft geladen und nicht mehr mit anderen PCs im lokalen Netzwerk oder Internet ausgetauscht (Richtlinie „Downloadmodus: Nur HTTP“).",
                ActionCategory.Network, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", Zero, One, "Nur direkter Download (kein Peering)", "Peering im lokalen Netzwerk",
                "Keine Upload-Bandbreite mehr für die Verteilung von Updates an andere Geräte.", "Mehrere PCs im selben Netzwerk laden Updates jeweils vollständig aus dem Internet.",
                RiskLevel.Low, tags: new[] { TagPrivacy, TagBalanced }),
            Machine("compat.enable-long-paths", "Lange Dateipfade (über 260 Zeichen) erlauben",
                "Hebt für Anwendungen, die dies unterstützen, die klassische Pfadlängengrenze von 260 Zeichen auf.",
                ActionCategory.Compatibility, @"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", One, Zero, "Lange Pfade erlaubt", "Grenze 260 Zeichen",
                "Entwicklungswerkzeuge und Synchronisierungsprogramme können tief verschachtelte Ordner verarbeiten.",
                "Ältere Programme können mit sehr langen Pfaden weiterhin nicht umgehen.", RiskLevel.Low, versions: new WindowsVersionRange(14393), tags: new[] { TagPreference }),

            // ───────────── Datenschutz ─────────────
            new RegistryTweakDefinition
            {
                Id = "privacy.disable-advertising-id", Name = "Werbe-ID deaktivieren",
                Description = "Apps dürfen die Werbe-ID nicht mehr verwenden, um Ihnen app-übergreifend personalisierte Werbung zu zeigen.",
                Category = ActionCategory.Privacy, Root = RegistryRoot.CurrentUser, KeyPath = CurrentVersion + @"\AdvertisingInfo", ValueName = "Enabled",
                OptimizedValue = Zero, WindowsDefault = One, OptimizedLabel = "Werbe-ID aus", DefaultLabel = "Werbe-ID an",
                ExpectedBehavior = "Werbung in Apps ist nicht mehr anhand der Werbe-ID personalisiert.", SideEffects = "Die Anzahl der Werbeeinblendungen ändert sich nicht.",
                PolicyOverride = new RegistryTarget(RegistryRoot.LocalMachine, RegistryViewKind.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy"),
                Tags = new[] { TagSafe, TagBalanced, TagPrivacy },
            },
            User("privacy.disable-tailored-experiences", "Individuelle Benutzererfahrung deaktivieren",
                "Microsoft verwendet Diagnosedaten nicht mehr für personalisierte Tipps, Werbung und Empfehlungen.",
                ActionCategory.Privacy, CurrentVersion + @"\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", Zero, One, "Aus", "An",
                "Keine auf Diagnosedaten basierende Personalisierung.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("privacy.disable-start-suggestions", "App-Vorschläge im Startmenü deaktivieren",
                "Blendet vorgeschlagene Apps und Inhalte im Startmenü aus.",
                ActionCategory.Privacy, ContentDelivery, "SubscribedContent-338388Enabled", Zero, One, "Vorschläge aus", "Vorschläge an",
                "Das Startmenü zeigt keine beworbenen Apps mehr.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("privacy.disable-windows-tips", "Tipps und Vorschläge von Windows deaktivieren",
                "Deaktiviert „Tipps und Vorschläge bei der Verwendung von Windows erhalten“.",
                ActionCategory.Privacy, ContentDelivery, "SubscribedContent-338389Enabled", Zero, One, "Tipps aus", "Tipps an",
                "Keine Tipp-Benachrichtigungen mehr.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("privacy.disable-silent-app-install", "Automatische Installation vorgeschlagener Apps verhindern",
                "Verhindert, dass Windows beworbene Apps ohne Nachfrage installiert.",
                ActionCategory.Privacy, ContentDelivery, "SilentInstalledAppsEnabled", Zero, One, "Automatische Installation aus", "Automatische Installation an",
                "Es werden keine vorgeschlagenen Apps mehr unaufgefordert installiert.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("privacy.disable-settings-suggestions", "Vorgeschlagene Inhalte in den Einstellungen deaktivieren",
                "Blendet Vorschläge und Empfehlungen in der Einstellungen-App aus.",
                ActionCategory.Privacy, ContentDelivery, "SubscribedContent-338393Enabled", Zero, One, "Vorschläge aus", "Vorschläge an",
                "Die Einstellungen-App zeigt keine beworbenen Inhalte mehr.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("privacy.disable-welcome-experience", "Windows-Willkommensseite nach Updates deaktivieren",
                "Unterdrückt die Willkommensseite mit Neuigkeiten und Vorschlägen nach Funktionsupdates.",
                ActionCategory.Privacy, ContentDelivery, "SubscribedContent-310093Enabled", Zero, One, "Willkommensseite aus", "Willkommensseite an",
                "Nach Updates erscheint keine Vollbild-Willkommensseite mehr.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("privacy.disable-lockscreen-tips", "Tipps und Wissenswertes auf dem Sperrbildschirm deaktivieren",
                "Blendet Tipps, Tricks und Hinweise auf dem Sperrbildschirm aus.",
                ActionCategory.Privacy, ContentDelivery, "RotatingLockScreenOverlayEnabled", Zero, One, "Sperrbildschirm-Tipps aus", "Sperrbildschirm-Tipps an",
                "Der Sperrbildschirm zeigt keine Hinweistexte mehr.", tags: new[] { TagSafe, TagPrivacy }),
            User("privacy.disable-app-launch-tracking", "Nachverfolgung von App-Starts deaktivieren",
                "Windows protokolliert nicht mehr, welche Apps Sie starten, um Startmenü und Suchergebnisse zu personalisieren.",
                ActionCategory.Privacy, ExplorerAdvanced, "Start_TrackProgs", Zero, One, "Nachverfolgung aus", "Nachverfolgung an",
                "Keine Nutzungsstatistik über gestartete Apps.", "Die Liste „Meistverwendet“ im Startmenü bleibt leer.", tags: new[] { TagPrivacy }),
            User("privacy.disable-language-list-access", "Websites den Zugriff auf die Sprachliste verwehren",
                "Websites können nicht mehr auf Ihre Liste installierter Sprachen zugreifen, um lokal relevante Inhalte anzubieten.",
                ActionCategory.Privacy, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", One, Zero, "Zugriff verwehrt", "Zugriff erlaubt",
                "Weniger Merkmale für Browser-Fingerprinting.", "Manche Websites erscheinen nicht mehr automatisch in Ihrer Sprache.", tags: new[] { TagPrivacy }),
            User("privacy.disable-typing-personalization", "Personalisierung von Tastatureingaben deaktivieren",
                "Windows erstellt kein persönliches Wörterbuch mehr aus Ihren Tastatureingaben.",
                ActionCategory.Privacy, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", One, Zero, "Erfassung eingeschränkt", "Erfassung erlaubt",
                "Tastatureingaben werden nicht zur Personalisierung ausgewertet.", "Wortvorschläge und Autokorrektur werden weniger treffsicher.", tags: new[] { TagPrivacy }),
            User("privacy.disable-inking-personalization", "Personalisierung von Freihandeingaben deaktivieren",
                "Windows wertet Stift- und Handschrifteingaben nicht mehr zur Personalisierung aus.",
                ActionCategory.Privacy, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", One, Zero, "Erfassung eingeschränkt", "Erfassung erlaubt",
                "Handschrifteingaben werden nicht zur Personalisierung ausgewertet.", "Die Handschrifterkennung passt sich nicht mehr an Ihre Schrift an.", tags: new[] { TagPrivacy }),
            User("privacy.disable-feedback-prompts", "Feedbackanfragen von Windows deaktivieren",
                "Windows fragt nicht mehr regelmäßig nach Ihrer Meinung.",
                ActionCategory.Privacy, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", Zero, null, "Nie fragen", "Automatisch",
                "Keine Feedback-Benachrichtigungen mehr.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("privacy.disable-online-speech", "Online-Spracherkennung deaktivieren",
                "Sprachaufnahmen werden nicht mehr zur cloudbasierten Erkennung an Microsoft gesendet.",
                ActionCategory.Privacy, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", Zero, null, "Online-Spracherkennung aus", "Online-Spracherkennung an",
                "Sprachdaten verlassen das Gerät nicht mehr für die Online-Erkennung.", "Diktieren (Win+H) und Sprachfunktionen einiger Apps funktionieren nicht oder nur eingeschränkt.",
                RiskLevel.Low, tags: new[] { TagPrivacy }),
            User("privacy.deny-location", "Standortzugriff für Apps verweigern",
                "Apps erhalten für dieses Benutzerkonto keinen Zugriff mehr auf den Gerätestandort.",
                ActionCategory.Privacy, CurrentVersion + @"\CapabilityAccessManager\ConsentStore\location", "Value", RegistryValueData.Text("Deny"), null, "Verweigert", "Erlaubt",
                "Apps können den Standort nicht abfragen.", "Wetter, Karten, „Mein Gerät suchen“, automatische Zeitzone und WLAN-Namensanzeige einiger Programme funktionieren eingeschränkt.",
                RiskLevel.Low, tags: new[] { TagPrivacy }),
            User("privacy.deny-camera", "Kamerazugriff für Apps verweigern",
                "Apps erhalten für dieses Benutzerkonto keinen Zugriff mehr auf die Kamera.",
                ActionCategory.Privacy, CurrentVersion + @"\CapabilityAccessManager\ConsentStore\webcam", "Value", RegistryValueData.Text("Deny"), null, "Verweigert", "Erlaubt",
                "Apps können die Kamera nicht verwenden.", "Videotelefonie und Windows-Hello-Gesichtserkennung in Apps funktionieren nicht mehr, bis der Zugriff wieder erlaubt wird.",
                RiskLevel.Moderate, tags: new[] { TagPrivacy }),
            User("privacy.deny-microphone", "Mikrofonzugriff für Apps verweigern",
                "Apps erhalten für dieses Benutzerkonto keinen Zugriff mehr auf das Mikrofon.",
                ActionCategory.Privacy, CurrentVersion + @"\CapabilityAccessManager\ConsentStore\microphone", "Value", RegistryValueData.Text("Deny"), null, "Verweigert", "Erlaubt",
                "Apps können das Mikrofon nicht verwenden.", "Sprachchat, Telefonie und Diktierfunktionen funktionieren nicht mehr, bis der Zugriff wieder erlaubt wird.",
                RiskLevel.Moderate, tags: new[] { TagPrivacy }),
            Machine("privacy.disable-activity-history", "Aktivitätsverlauf deaktivieren",
                "Windows speichert keinen Verlauf über verwendete Apps, Dateien und Websites mehr (Richtlinie „Veröffentlichung von Benutzeraktivitäten zulassen“).",
                ActionCategory.Privacy, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", Zero, One, "Aktivitätsverlauf aus", "Aktivitätsverlauf an",
                "Kein lokaler Aktivitätsverlauf.", "Funktionen, die auf dem Aktivitätsverlauf aufbauen, stehen nicht zur Verfügung; die Einstellung gilt als verwaltet.",
                RiskLevel.Low, tags: new[] { TagPrivacy }),
            Machine("privacy.limit-diagnostic-data", "Diagnosedaten auf „Erforderlich“ beschränken",
                "Beschränkt die an Microsoft gesendeten Diagnosedaten per Richtlinie auf die erforderliche Stufe. Telemetrie wird dadurch reduziert, aber nicht vollständig entfernt.",
                ActionCategory.Privacy, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", One, null, "Nur erforderliche Diagnosedaten", "Benutzerauswahl in den Einstellungen",
                "Optionale Diagnosedaten werden nicht mehr gesendet.", "Die Auswahl in den Einstellungen ist gesperrt („von Ihrer Organisation verwaltet“); einige Insider- und Diagnosefunktionen entfallen.",
                RiskLevel.Low, tags: new[] { TagPrivacy }),

            // ───────────── Benachrichtigungen ─────────────
            User("notifications.disable-lockscreen-toasts", "Benachrichtigungen auf dem Sperrbildschirm ausblenden",
                "Benachrichtigungen werden nicht mehr auf dem gesperrten Bildschirm angezeigt.",
                ActionCategory.Notifications, CurrentVersion + @"\Notifications\Settings", "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK", Zero, One, "Ausgeblendet", "Eingeblendet",
                "Inhalte von Benachrichtigungen sind bei gesperrtem Gerät nicht sichtbar.", tags: new[] { TagPrivacy }),
            User("notifications.disable-setup-suggestions", "Vorschläge zur Geräteeinrichtung deaktivieren",
                "Unterdrückt die wiederkehrende Aufforderung „Einrichtung Ihres Geräts abschließen“.",
                ActionCategory.Notifications, CurrentVersion + @"\UserProfileEngagement", "ScoobeSystemSettingEnabled", Zero, One, "Vorschläge aus", "Vorschläge an",
                "Keine Vollbild-Einrichtungsvorschläge nach der Anmeldung.", tags: new[] { TagSafe, TagBalanced, TagPrivacy }),
            User("notifications.disable-all-toasts", "Alle Popupbenachrichtigungen deaktivieren",
                "Schaltet Popupbenachrichtigungen von Apps und Absendern für dieses Benutzerkonto ab.",
                ActionCategory.Notifications, CurrentVersion + @"\PushNotifications", "ToastEnabled", Zero, One, "Benachrichtigungen aus", "Benachrichtigungen an",
                "Keine Unterbrechungen durch Popups.", "Auch wichtige Hinweise (z. B. von Sicherheits- oder Kalender-Apps) erscheinen nicht mehr.",
                RiskLevel.Low, RestartRequirement.SignOut, tags: new[] { TagPreference }),

            // ───────────── Expertenlabor ─────────────
            new RegistryTweakDefinition
            {
                Id = "expert.mmcss-system-responsiveness", Name = "MMCSS: CPU-Reserve für Hintergrundaufgaben auf 10 % senken",
                Description = "Der Multimedia Class Scheduler reserviert standardmäßig 20 % der CPU-Zeit für Aufgaben niedriger Priorität, wenn Multimedia-Threads laufen. Der Wert 10 ist das dokumentierte Minimum.",
                Category = ActionCategory.AdvancedSystem, Root = RegistryRoot.LocalMachine, KeyPath = SystemProfile, ValueName = "SystemResponsiveness",
                OptimizedValue = RegistryValueData.DWord(10), WindowsDefault = RegistryValueData.DWord(20), OptimizedLabel = "10 % Reserve", DefaultLabel = "20 % Reserve",
                ExpectedBehavior = "Bei MMCSS-registrierten Multimedia-Threads steht etwas mehr CPU-Zeit zur Verfügung. Ein messbarer Nutzen in Spielen ist nicht belegt.",
                SideEffects = "Hintergrundaufgaben können unter Multimedialast langsamer reagieren.",
                Risk = RiskLevel.Moderate, Restart = RestartRequirement.Reboot, Experimental = true, Tags = new[] { TagExpert },
            },
            new RegistryTweakDefinition
            {
                Id = "expert.mmcss-network-throttling", Name = "MMCSS: Netzwerkdrosselung bei Multimediawiedergabe deaktivieren",
                Description = "Windows begrenzt während der Multimediawiedergabe die Verarbeitung von Nicht-Multimedia-Netzwerkpaketen (Standardindex 10). Der Wert 0xFFFFFFFF schaltet diese Drosselung ab (von Microsoft dokumentiert).",
                Category = ActionCategory.AdvancedSystem, Root = RegistryRoot.LocalMachine, KeyPath = SystemProfile, ValueName = "NetworkThrottlingIndex",
                OptimizedValue = RegistryValueData.DWord(0xFFFFFFFF), WindowsDefault = RegistryValueData.DWord(10), OptimizedLabel = "Drosselung aus", DefaultLabel = "Index 10 (Standard)",
                ExpectedBehavior = "Relevant nur bei sehr hohem Netzwerkdurchsatz parallel zur Audio-/Videowiedergabe. Für Spiele-Latenz ist kein Nutzen belegt.",
                SideEffects = "Unter hoher Netzwerklast kann es zu Tonaussetzern bei der Wiedergabe kommen.",
                Risk = RiskLevel.Moderate, Restart = RestartRequirement.Reboot, Experimental = true, Tags = new[] { TagExpert },
            },
            new RegistryTweakDefinition
            {
                Id = "expert.clear-pagefile-at-shutdown", Name = "Auslagerungsdatei beim Herunterfahren leeren",
                Description = "Überschreibt die Auslagerungsdatei bei jedem Herunterfahren. Dies ist eine Datenschutz-/Sicherheitsoption und keine Leistungsoptimierung.",
                Category = ActionCategory.Memory, Root = RegistryRoot.LocalMachine, KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "ClearPageFileAtShutdown",
                OptimizedValue = One, WindowsDefault = Zero, OptimizedLabel = "Leeren beim Herunterfahren", DefaultLabel = "Nicht leeren",
                ExpectedBehavior = "Ausgelagerte Speicherinhalte bleiben nach dem Herunterfahren nicht auf dem Datenträger zurück.",
                SideEffects = "Das Herunterfahren dauert deutlich länger; zusätzliche Schreibvorgänge auf dem Datenträger.",
                Risk = RiskLevel.Moderate, Restart = RestartRequirement.Reboot, Experimental = true, Tags = new[] { TagExpert },
            },
        };

        var duplicate = list.GroupBy(t => t.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Doppelte Tweak-ID im Katalog: {duplicate.Key}");
        return list;
    }
}
