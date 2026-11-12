using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.App.ViewModels;
using NexusOptimizer.App.Views;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Data;

namespace NexusOptimizer.App;

public partial class NexusApp : Application
{
    private readonly StartupOptions _options;
    private ServiceProvider? _services;
    private ILogger? _logger;

    public NexusApp(StartupOptions options) => _options = options;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Die Oberfläche ist deutsch; Zahlen und Datumsangaben folgen dem deutschen Format.
        var culture = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("de-DE")));

        try
        {
            _services = Composition.Build(_options);
        }
        catch (Exception ex)
        {
            MessageBox.Show("NEXUS Optimizer AI konnte nicht gestartet werden:\n\n" + ex.Message, "NEXUS Optimizer AI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unbeobachteter Fehler in einer Hintergrundaufgabe.");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _logger.LogCritical(args.ExceptionObject as Exception, "Nicht behandelter Fehler.");

        try
        {
            var settings = _services.GetRequiredService<SettingsService>();
            var runtime = _services.GetRequiredService<AppRuntime>();
            runtime.Start();

            var shell = _services.GetRequiredService<ShellViewModel>();
            var window = new MainWindow(shell, runtime, settings);
            MainWindow = window;
            window.Closed += (_, _) => Shutdown();
            if (_options.Minimized && settings.Current.StartMinimized && !_options.IsSmokeTest) window.WindowState = WindowState.Minimized;
            window.Show();

            var database = _services.GetRequiredService<NexusDatabase>();
            if (database.CorruptBackupPath is { } backup)
            {
                _services.GetRequiredService<NotificationCenter>().Publish(NotificationKind.General, "Datenbank wurde neu angelegt",
                    "Die lokale Datenbank war beschädigt und wurde ersetzt. Die alte Datei liegt als Sicherung vor: " + backup, NotificationSeverity.Warning, "backup");
            }

            await shell.NavigateAsync(_options.InitialPage is { } page && PageRegistry.Find(page) is not null ? page : "dashboard").ConfigureAwait(true);

            if (_options.SmokeTestDirectory is { } directory)
            {
                var code = await SmokeTest.RunAsync(window, shell, _services, directory).ConfigureAwait(true);
                Shutdown(code);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Start fehlgeschlagen.");
            if (_options.IsSmokeTest)
            {
                SmokeTest.WriteFailure(_options.SmokeTestDirectory!, ex);
                Shutdown(3);
                return;
            }
            MessageBox.Show("NEXUS Optimizer AI konnte nicht gestartet werden:\n\n" + ex.Message, "NEXUS Optimizer AI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Ein Fehler in einer Seite darf die Anwendung nicht beenden: protokollieren, verständlich melden, weiterlaufen.
        _logger?.LogError(e.Exception, "Nicht behandelter Fehler in der Oberfläche.");
        e.Handled = true;
        SmokeTest.RecordUnhandled(e.Exception);
        if (_options.IsSmokeTest) return;
        try
        {
            _services?.GetService<ToastService>()?.Show("Unerwarteter Fehler", e.Exception.Message + " – Details stehen im Protokoll.", NotificationSeverity.Error);
        }
        catch (Exception)
        {
            // Die Meldung selbst darf keinen Folgefehler auslösen.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            try
            {
                // Optional: aktives Profil beim Beenden zurücknehmen (Einstellung „Profil beim Beenden wiederherstellen“).
                if (!_options.IsSmokeTest && _services.GetRequiredService<SettingsService>().Current.RestoreProfileOnExit)
                    Task.Run(() => _services.GetRequiredService<ProfileManager>().ResetActiveAsync()).Wait(TimeSpan.FromSeconds(20));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Das aktive Profil konnte beim Beenden nicht zurückgenommen werden.");
            }

            try { _services.GetRequiredService<AppRuntime>().Dispose(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Fehler beim Beenden der Hintergrunddienste."); }
            try { _services.Dispose(); }
            catch (Exception) { /* Beenden darf nicht scheitern. */ }
        }
        base.OnExit(e);
    }
}
