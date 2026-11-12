using System.IO;
using System.Windows;

namespace NexusOptimizer.App;

/// <summary>Startparameter der Anwendung.</summary>
public sealed record StartupOptions
{
    /// <summary>Im Infobereich bzw. minimiert starten (Autostart).</summary>
    public bool Minimized { get; init; }
    /// <summary>Selbsttest der Oberfläche: alle Seiten laden, als Bild speichern, Bindungsfehler protokollieren und beenden.</summary>
    public string? SmokeTestDirectory { get; init; }
    /// <summary>Abweichendes Datenverzeichnis (für Tests; Standard: %LocalAppData%\NexusOptimizerAI).</summary>
    public string? DataDirectory { get; init; }
    public string? InitialPage { get; init; }

    public bool IsSmokeTest => SmokeTestDirectory is not null;

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var next = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--minimized":
                    options = options with { Minimized = true };
                    break;
                case "--smoke-test" when next is not null:
                    options = options with { SmokeTestDirectory = Path.GetFullPath(next) };
                    i++;
                    break;
                case "--data-dir" when next is not null:
                    options = options with { DataDirectory = Path.GetFullPath(next) };
                    i++;
                    break;
                case "--page" when next is not null && next.Length <= 32 && next.All(char.IsAsciiLetter):
                    options = options with { InitialPage = next.ToLowerInvariant() };
                    i++;
                    break;
            }
        }
        return options;
    }
}

public static class Program
{
    private const string InstanceMutexName = @"Local\NexusOptimizerAI.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);

        // Nur eine Instanz je Benutzersitzung – zwei Instanzen würden sich bei Systemänderungen in die Quere kommen.
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        if (!isFirst && !options.IsSmokeTest)
        {
            MessageBox.Show("NEXUS Optimizer AI läuft bereits.", "NEXUS Optimizer AI", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        var application = new NexusApp(options);
        application.InitializeComponent();
        return application.Run();
    }
}
