using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusOptimizer.AI;
using NexusOptimizer.App.Services;
using NexusOptimizer.App.ViewModels;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Automation;
using NexusOptimizer.Core.Benchmark;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Diagnostics;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Gaming;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Logging;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Optimization;
using NexusOptimizer.Core.Orchestration;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Tools;
using NexusOptimizer.Core.Tuning;
using NexusOptimizer.Data;
using NexusOptimizer.Windows;
using NexusOptimizer.Windows.Diagnostics;
using NexusOptimizer.Windows.Gaming;
using NexusOptimizer.Windows.Hardware;
using NexusOptimizer.Windows.Monitoring;
using NexusOptimizer.Windows.SystemAccess;

namespace NexusOptimizer.App;

/// <summary>Zuletzt erkannte Hardwareinformationen – für Stellen, die synchron darauf zugreifen müssen.</summary>
public sealed class SystemInfoCache
{
    public SystemInfo? Current { get; set; }
}

/// <summary>Verdrahtet alle Dienste. Es gibt genau einen Aktionskatalog; nur dessen Einträge sind ausführbar.</summary>
public static class Composition
{
    public static ServiceProvider Build(StartupOptions options)
    {
        var dataDirectory = options.DataDirectory ?? NexusDatabase.DataDirectory;
        Directory.CreateDirectory(dataDirectory);

        var services = new ServiceCollection();
        services.AddSingleton(options);

        var logProvider = new FileLoggerProvider(Path.Combine(dataDirectory, "logs"));
        services.AddSingleton(logProvider);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(logProvider);
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // ── Datenhaltung ──
        services.AddSingleton(new NexusDatabase(Path.Combine(dataDirectory, "nexus.db")));
        services.AddSingleton<ITransactionJournal, SqliteTransactionJournal>();
        services.AddSingleton<IMetricHistoryStore, SqliteMetricHistoryStore>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<INotificationStore, SqliteNotificationStore>();
        services.AddSingleton<IChatStore, SqliteChatStore>();
        services.AddSingleton<IFeedbackStore, SqliteFeedbackStore>();
        services.AddSingleton<IAutomationStore, SqliteAutomationStore>();
        services.AddSingleton<IProfileStore, SqliteProfileStore>();
        services.AddSingleton<IGameStore, SqliteGameStore>();
        services.AddSingleton<IBenchmarkStore, SqliteBenchmarkStore>();
        services.AddSingleton<ITuningStore, SqliteTuningStore>();
        services.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(Path.Combine(dataDirectory, "secrets")));
        services.AddSingleton<SettingsService>();
        services.AddSingleton<NotificationCenter>();

        // ── Windows-Zugriffe ──
        var platform = new WindowsPlatformServices();
        var infoCache = new SystemInfoCache();
        services.AddSingleton(platform);
        services.AddSingleton(infoCache);
        services.AddSingleton(platform.Registry);
        services.AddSingleton(platform.Power);
        services.AddSingleton(platform.Services);
        services.AddSingleton(platform.Startup);
        services.AddSingleton<IProcessMonitor>(platform.Processes);
        services.AddSingleton<IProcessControl>(platform.Processes);
        services.AddSingleton(platform.Network);
        services.AddSingleton(platform.Tools);
        services.AddSingleton(platform.Restore);
        services.AddSingleton(platform.Cleanup);
        services.AddSingleton<IHardwareInfoProvider>(sp => new WindowsHardwareInfoProvider(platform.Power, sp.GetService<ILogger<WindowsHardwareInfoProvider>>()));
        services.AddSingleton<ISystemDiagnostics, WindowsDiagnostics>();
        services.AddSingleton<IGameLibraryScanner>(sp => new WindowsGameLibraryScanner(sp.GetService<ILogger<WindowsGameLibraryScanner>>()));
        services.AddSingleton(_ => new WindowsMetricsCollector());
        services.AddSingleton<IMetricsCollector>(sp => sp.GetRequiredService<WindowsMetricsCollector>());
        services.AddSingleton(sp => new MonitoringService(sp.GetRequiredService<IMetricsCollector>(), sp.GetRequiredService<IMetricHistoryStore>(), sp.GetService<ILogger<MonitoringService>>()));
        services.AddSingleton<AlertEngine>();

        // ── Optimierungs-Engine ──
        services.AddSingleton(WindowsActionCatalogFactory.Create(platform, WindowsActionCatalogFactory.SolidStateLookup(() => infoCache.Current)));
        services.AddSingleton<ExecutionGate>();
        services.AddSingleton(sp => new ElevatedGateway(null, sp.GetService<ILogger<ElevatedGateway>>()));
        services.AddSingleton<IActionRunner>(sp => new RoutingActionRunner(sp.GetRequiredService<ActionCatalog>(), new LocalActionRunner(sp.GetRequiredService<ActionCatalog>()),
            sp.GetRequiredService<ElevatedGateway>(), () => ProcessElevation.IsElevated));
        services.AddSingleton<OptimizationPlanner>();
        services.AddSingleton(sp => new OptimizationExecutor(sp.GetRequiredService<IActionRunner>(), sp.GetRequiredService<ITransactionJournal>(), sp.GetRequiredService<ExecutionGate>(),
            sp.GetService<ILogger<OptimizationExecutor>>()));
        services.AddSingleton<RollbackService>();
        services.AddSingleton<SystemSnapshotService>();
        services.AddSingleton<RuleBasedAdvisor>();
        services.AddSingleton<OneClickOptimizer>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<BenchmarkRunner>();
        services.AddSingleton(sp => new TuningExperimentRunner(sp.GetRequiredService<OptimizationPlanner>(), sp.GetRequiredService<OptimizationExecutor>(), sp.GetRequiredService<RollbackService>(),
            sp.GetRequiredService<IFeedbackStore>(), sp.GetRequiredService<ITuningStore>()));

        // ── Automatisierung und Gaming ──
        services.AddSingleton<IAutomationActionExecutor, AutomationActionExecutor>();
        services.AddSingleton<IAutomationContext>(sp => new LiveAutomationContext(sp.GetRequiredService<MonitoringService>(), sp.GetRequiredService<IProcessMonitor>(),
            () => sp.GetRequiredService<GamingSessionManager>().Current is not null));
        services.AddSingleton(sp => new AutomationEngine(sp.GetRequiredService<IAutomationStore>(), sp.GetRequiredService<IAutomationActionExecutor>(), sp.GetRequiredService<IAutomationContext>(),
            null, sp.GetService<ILogger<AutomationEngine>>()));
        services.AddSingleton(sp => new AutomationEventPump(sp.GetRequiredService<AutomationEngine>(), sp.GetRequiredService<IAutomationStore>(), sp.GetRequiredService<IProcessMonitor>()));
        services.AddSingleton<GameProfileApplier>();
        services.AddSingleton<IGameProfileApplier>(sp => sp.GetRequiredService<GameProfileApplier>());
        services.AddSingleton<IFrameCaptureProvider>(sp => new PresentMonFrameCapture(() => sp.GetRequiredService<SettingsService>().Current.PresentMonPath));
        services.AddSingleton(sp => new GamingSessionManager(sp.GetRequiredService<IGameStore>(), sp.GetRequiredService<IProcessMonitor>(), sp.GetRequiredService<IGameProfileApplier>(),
            sp.GetRequiredService<MonitoringService>(), sp.GetRequiredService<IFrameCaptureProvider>(), null, sp.GetService<ILogger<GamingSessionManager>>()));

        // ── KI ──
        var http = new Lazy<HttpClient>(() => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) });
        services.AddSingleton<IAiProviderFactory>(sp => new AiProviderFactory(sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<ISecretStore>(), () => http.Value));
        services.AddSingleton(sp => new ChatEngine(sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<SystemSnapshotService>(), sp.GetRequiredService<RuleBasedAdvisor>(),
            sp.GetRequiredService<ActionCatalog>(), sp.GetRequiredService<IChatStore>(), sp.GetRequiredService<IAiProviderFactory>(),
            () => sp.GetRequiredService<RollbackService>().FindLastActive(), null, sp.GetService<ILogger<ChatEngine>>()));

        // ── Oberfläche ──
        services.AddSingleton<UiState>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<ActionCoordinator>();
        services.AddSingleton<AutoPilot>();
        services.AddSingleton<AppRuntime>();
        services.AddSingleton<ShellViewModel>();
        foreach (var page in PageRegistry.All) services.AddSingleton(page.PageType);

        return services.BuildServiceProvider();
    }
}
