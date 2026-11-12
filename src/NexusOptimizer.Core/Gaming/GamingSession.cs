using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Monitoring;

namespace NexusOptimizer.Core.Gaming;

/// <summary>Benutzerdefiniertes Profil für ein Spiel (Abschnitt 10.2).</summary>
public sealed record GameProfile
{
    public string GameId { get; init; } = "";
    public string GameName { get; init; } = "";
    /// <summary>Dateinamen (ohne Pfad), an denen der Start des Spiels erkannt wird.</summary>
    public List<string> ProcessNames { get; init; } = new();
    public string? ExecutablePath { get; init; }
    /// <summary>Der Benutzer hat die automatische Aktivierung für dieses Spiel ausdrücklich erlaubt.</summary>
    public bool AutoActivate { get; init; }
    public bool OnlyOnAcPower { get; init; } = true;
    /// <summary>„balanced“, „high-performance“, „ultimate“ oder null (nicht ändern).</summary>
    public string? PowerPlan { get; init; } = "high-performance";
    /// <summary>Prioritätsklasse des Spielprozesses oder null (nicht ändern).</summary>
    public string? ProcessPriority { get; init; }
    /// <summary>„default“, „power-saving“, „high-performance“ oder null (nicht ändern).</summary>
    public string? GpuPreference { get; init; }
    /// <summary>Hintergrundprogramme, deren Beenden der Benutzer für dieses Spiel ausdrücklich erlaubt hat.</summary>
    public List<string> CloseProcesses { get; init; } = new();
    public string? LaunchArguments { get; init; }
    public bool RestoreOnExit { get; init; } = true;
    public bool RecordSession { get; init; } = true;
    public int MonitoringIntervalSeconds { get; init; } = 2;
}

public sealed record FrameStatistics(int FrameCount, double AverageFps, double OnePercentLowFps, double PointOnePercentLowFps, double MedianFrameTimeMs, double P95FrameTimeMs, double P99FrameTimeMs, double FrameTimeStdDevMs)
{
    /// <summary>
    /// Berechnet Kennzahlen aus einer Reihe von Frametimes (ms). „1 % Low“ ist die mittlere Bildrate der langsamsten 1 % der Frames.
    /// Liefert null, wenn zu wenige Frames für eine belastbare Aussage vorliegen.
    /// </summary>
    public static FrameStatistics? FromFrameTimes(IReadOnlyList<double> frameTimesMs)
    {
        var valid = frameTimesMs.Where(t => t > 0 && !double.IsNaN(t) && !double.IsInfinity(t)).ToArray();
        if (valid.Length < 100) return null;

        var sorted = (double[])valid.Clone();
        Array.Sort(sorted);
        var mean = valid.Average();
        var variance = valid.Sum(t => (t - mean) * (t - mean)) / (valid.Length - 1);

        double LowFps(double fraction)
        {
            var count = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
            var slowest = sorted.Skip(sorted.Length - count).Average();
            return 1000.0 / slowest;
        }

        return new FrameStatistics(valid.Length, 1000.0 / mean, LowFps(0.01), LowFps(0.001),
            Benchmark.Statistics.Percentile(sorted, 50), Benchmark.Statistics.Percentile(sorted, 95), Benchmark.Statistics.Percentile(sorted, 99), Math.Sqrt(variance));
    }
}

/// <summary>Optionale Quelle für Frametimes (z. B. PresentMon). Ohne Anbieter werden FPS-Werte als „nicht verfügbar“ ausgewiesen.</summary>
public interface IFrameCaptureProvider
{
    bool IsAvailable { get; }
    string UnavailableReason { get; }
    Task StartAsync(int processId, string processName, CancellationToken cancellationToken);
    /// <summary>Beendet die Aufzeichnung und liefert die Frametimes in Millisekunden.</summary>
    Task<IReadOnlyList<double>> StopAsync();
}

public sealed record GameSessionRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string GameId { get; init; } = "";
    public string GameName { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public bool ProfileApplied { get; init; }
    public string? ProfileNote { get; init; }
    public int Samples { get; init; }
    public double? AverageCpuPercent { get; init; }
    public double? PeakCpuPercent { get; init; }
    public double? AverageGpuPercent { get; init; }
    public double? PeakGpuPercent { get; init; }
    public double? AverageMemoryPercent { get; init; }
    public double? PeakGpuMemoryBytes { get; init; }
    public double? PeakTemperatureCelsius { get; init; }
    public double? PeakGpuTemperatureCelsius { get; init; }
    /// <summary>Anteil der Messpunkte mit begrenzter Prozessorleistung (0–100).</summary>
    public double? ThermalLimitedPercent { get; init; }
    public FrameStatistics? Frames { get; init; }
    public string? FrameNote { get; init; }
    public IReadOnlyList<string> BackgroundProcesses { get; init; } = Array.Empty<string>();
    public TimeSpan Duration => EndedAt - StartedAt;
}

public interface IGameStore
{
    IReadOnlyList<GameInfo> GetGames();
    void SaveGames(IReadOnlyList<GameInfo> games);
    IReadOnlyList<GameProfile> GetProfiles();
    void SaveProfile(GameProfile profile);
    void DeleteProfile(string gameId);
    void AppendSession(GameSessionRecord session);
    IReadOnlyList<GameSessionRecord> GetSessions(string? gameId = null, int limit = 100);
}

public sealed record GameProfileApplyResult(bool Applied, string? BatchId, string Note);

/// <summary>Wendet ein Spielprofil temporär an und stellt den vorherigen Zustand wieder her – über die reguläre Ausführungs-Engine.</summary>
public interface IGameProfileApplier
{
    Task<GameProfileApplyResult> ApplyAsync(GameProfile profile, int processId, CancellationToken cancellationToken);
    Task<string> RestoreAsync(string batchId, CancellationToken cancellationToken);
}

public sealed record ActiveGameSession(GameProfile Profile, int ProcessId, string ProcessName, DateTimeOffset StartedAt, string? BatchId, string Note);

/// <summary>
/// Automatischer Gaming-Modus (Abschnitt 10.3): erkennt den Start freigegebener Spiele, aktiviert das zugehörige Profil temporär,
/// zeichnet Sitzungsstatistiken auf und stellt beim Beenden die vorherige Konfiguration wieder her.
/// </summary>
public sealed class GamingSessionManager
{
    private readonly IGameStore _store;
    private readonly IProcessMonitor _processes;
    private readonly IGameProfileApplier _applier;
    private readonly MonitoringService _monitoring;
    private readonly IFrameCaptureProvider? _frames;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly List<SystemMetrics> _samples = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string> _backgroundAtStart = Array.Empty<string>();

    public GamingSessionManager(IGameStore store, IProcessMonitor processes, IGameProfileApplier applier, MonitoringService monitoring,
        IFrameCaptureProvider? frames = null, TimeProvider? time = null, ILogger<GamingSessionManager>? logger = null)
    {
        _store = store;
        _processes = processes;
        _applier = applier;
        _monitoring = monitoring;
        _frames = frames;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GamingSessionManager>.Instance;
        _monitoring.MetricsUpdated += OnMetrics;
    }

    public bool AutoModeEnabled { get; set; }
    public ActiveGameSession? Current { get; private set; }
    public event EventHandler<ActiveGameSession>? SessionStarted;
    public event EventHandler<GameSessionRecord>? SessionEnded;

    private void OnMetrics(object? sender, SystemMetrics metrics)
    {
        if (Current is not { Profile.RecordSession: true }) return;
        lock (_samples)
        {
            if (_samples.Count < 200_000) _samples.Add(metrics);
        }
    }

    /// <summary>Regelmäßig aufrufen (alle paar Sekunden). Prüft günstig anhand der Prozessnamen, ob ein Spiel gestartet oder beendet wurde.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var running = _processes.GetRunningProcessNames();

            if (Current is { } session)
            {
                if (running.Any(p => p.ProcessId == session.ProcessId && NameMatches(p.Name, session.ProcessName))) return;
                await EndSessionAsync(session, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!AutoModeEnabled) return;
            foreach (var profile in _store.GetProfiles().Where(p => p.AutoActivate && p.ProcessNames.Count > 0))
            {
                var match = running.FirstOrDefault(p => profile.ProcessNames.Any(n => NameMatches(p.Name, n)));
                if (match.Name is null) continue;
                await StartSessionAsync(profile, match.ProcessId, match.Name, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool NameMatches(string processName, string configured)
        => string.Equals(Path.GetFileNameWithoutExtension(processName), Path.GetFileNameWithoutExtension(configured), StringComparison.OrdinalIgnoreCase);

    private async Task StartSessionAsync(GameProfile profile, int processId, string processName, CancellationToken cancellationToken)
    {
        var latest = _monitoring.Latest;
        GameProfileApplyResult applied;
        if (profile.OnlyOnAcPower && latest?.OnAcPower == false)
        {
            applied = new GameProfileApplyResult(false, null, "Profil nicht aktiviert: Das Gerät läuft im Akkubetrieb.");
        }
        else
        {
            try
            {
                applied = await _applier.ApplyAsync(profile, processId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Spielprofil für {Game} konnte nicht angewendet werden.", profile.GameName);
                applied = new GameProfileApplyResult(false, null, "Profil nicht aktiviert: " + ex.Message);
            }
        }

        lock (_samples) _samples.Clear();
        try
        {
            _backgroundAtStart = _processes.GetProcesses()
                .Where(p => p.ProcessId != processId && p.ProcessId > 4 && (p.CpuPercent >= 3 || p.WorkingSetBytes >= 500L * 1024 * 1024))
                .OrderByDescending(p => p.CpuPercent).Take(8)
                .Select(p => $"{p.Name}: {p.CpuPercent:0.0} % CPU, {Storage.ByteFormat.Format(p.WorkingSetBytes)} RAM").ToList();
        }
        catch (Exception) { _backgroundAtStart = Array.Empty<string>(); }

        if (profile.RecordSession && _frames is { IsAvailable: true })
        {
            try { await _frames.StartAsync(processId, processName, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Frametime-Aufzeichnung konnte nicht gestartet werden."); }
        }

        _monitoring.Mode = MonitoringMode.Gaming;
        Current = new ActiveGameSession(profile, processId, processName, _time.GetUtcNow(), applied.BatchId, applied.Note);
        SessionStarted?.Invoke(this, Current);
    }

    private async Task EndSessionAsync(ActiveGameSession session, CancellationToken cancellationToken)
    {
        Current = null;
        _monitoring.Mode = MonitoringMode.Foreground;

        var note = session.Note;
        if (session.BatchId is not null && session.Profile.RestoreOnExit)
        {
            try { note += " " + await _applier.RestoreAsync(session.BatchId, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Wiederherstellung nach Spielende fehlgeschlagen.");
                note += " Wiederherstellung fehlgeschlagen: " + ex.Message;
            }
        }

        FrameStatistics? frames = null;
        var frameNote = _frames is null ? "Kein Frametime-Anbieter konfiguriert." : _frames.IsAvailable ? null : _frames.UnavailableReason;
        if (session.Profile.RecordSession && _frames is { IsAvailable: true })
        {
            try
            {
                frames = FrameStatistics.FromFrameTimes(await _frames.StopAsync().ConfigureAwait(false));
                if (frames is null) frameNote = "Zu wenige Frames für eine belastbare Auswertung.";
            }
            catch (Exception ex) { frameNote = "Frametime-Aufzeichnung fehlgeschlagen: " + ex.Message; }
        }

        SystemMetrics[] samples;
        lock (_samples)
        {
            samples = _samples.ToArray();
            _samples.Clear();
        }

        static double? Avg(IEnumerable<double?> values) { var v = values.Where(x => x is not null).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : v.Average(); }
        static double? Max(IEnumerable<double?> values) { var v = values.Where(x => x is not null).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : v.Max(); }

        var record = new GameSessionRecord
        {
            GameId = session.Profile.GameId,
            GameName = session.Profile.GameName,
            StartedAt = session.StartedAt,
            EndedAt = _time.GetUtcNow(),
            ProfileApplied = session.BatchId is not null,
            ProfileNote = note.Trim(),
            Samples = samples.Length,
            AverageCpuPercent = Avg(samples.Select(s => s.CpuTotalPercent)),
            PeakCpuPercent = Max(samples.Select(s => s.CpuTotalPercent)),
            AverageGpuPercent = Avg(samples.Select(s => s.GpuUsagePercent)),
            PeakGpuPercent = Max(samples.Select(s => s.GpuUsagePercent)),
            AverageMemoryPercent = Avg(samples.Select(s => s.MemoryUsedPercent)),
            PeakGpuMemoryBytes = Max(samples.Select(s => s.GpuDedicatedUsedBytes)),
            PeakTemperatureCelsius = Max(samples.Select(s => s.CpuPackageCelsius ?? s.ThermalZoneCelsius)),
            PeakGpuTemperatureCelsius = Max(samples.Select(s => s.GpuTemperatureCelsius)),
            ThermalLimitedPercent = samples.Any(s => s.CpuPerformanceLimitPercent is not null)
                ? 100.0 * samples.Count(s => s.IsThermallyLimited) / samples.Count(s => s.CpuPerformanceLimitPercent is not null) : null,
            Frames = frames,
            FrameNote = frameNote,
            BackgroundProcesses = _backgroundAtStart,
        };

        try { _store.AppendSession(record); }
        catch (Exception ex) { _logger.LogWarning(ex, "Spielsitzung konnte nicht gespeichert werden."); }
        SessionEnded?.Invoke(this, record);
    }
}
