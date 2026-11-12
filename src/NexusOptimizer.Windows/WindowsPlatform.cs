using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusOptimizer.Core.Abstractions;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Hardware;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Power;
using NexusOptimizer.Core.Registry;
using NexusOptimizer.Core.Security;
using NexusOptimizer.Core.Services;
using NexusOptimizer.Core.Startup;
using NexusOptimizer.Core.Storage;
using NexusOptimizer.Core.SystemAccess;
using NexusOptimizer.Core.Tools;
using NexusOptimizer.Windows.Native;
using NexusOptimizer.Windows.RegistryAccess;
using NexusOptimizer.Windows.SystemAccess;

namespace NexusOptimizer.Windows;

/// <summary>Bündelt die Windows-Implementierungen der Systemzugriffe.</summary>
public sealed class WindowsPlatformServices
{
    public IRegistryStore Registry { get; } = new WindowsRegistryStore();
    public IPowerPolicy Power { get; } = new WindowsPowerPolicy();
    public IServiceManager Services { get; } = new WindowsServiceManager();
    public IStartupInventory Startup { get; }
    public WindowsProcessMonitor Processes { get; }
    public INetworkConfigurator Network { get; } = new WindowsNetworkConfigurator();
    public ISystemToolRunner Tools { get; } = new WindowsSystemToolRunner();
    public ISystemRestore Restore { get; } = new WindowsSystemRestore();
    public ICleanupLocationProvider CleanupLocations { get; } = new WindowsCleanupLocations();
    public IRecycleBin RecycleBin { get; } = new WindowsRecycleBin();
    public CleanupEngine Cleanup { get; }

    public WindowsPlatformServices()
    {
        Startup = new WindowsStartupInventory(Registry);
        Processes = new WindowsProcessMonitor(Services);
        Cleanup = new CleanupEngine(CleanupLocations, RecycleBin);
    }
}

/// <summary>
/// Erzeugt die Allowlist aller implementierten Aktionen. Die Benutzeroberfläche und die privilegierte Komponente
/// verwenden exakt denselben Katalog – was hier nicht registriert ist, kann nirgends ausgeführt werden.
/// </summary>
public static class WindowsActionCatalogFactory
{
    public static ActionCatalog Create(WindowsPlatformServices platform, Func<string, bool>? isSolidState = null)
    {
        var actions = new List<IOptimizationAction>();
        actions.AddRange(RegistryTweakCatalog.All.Select(definition => new RegistryTweakAction(definition, platform.Registry)));
        actions.Add(new PowerPlanActivateAction(platform.Power));
        actions.Add(new PowerSettingAction(platform.Power));
        actions.Add(new ServiceStartupAction(platform.Services));
        actions.Add(new StartupEntryAction(platform.Registry, platform.Startup));
        actions.Add(new GpuPreferenceAction(platform.Registry));
        actions.Add(new ProcessPriorityAction(platform.Processes));
        actions.Add(new DnsSetAction(platform.Network));
        actions.Add(new RestorePointAction(platform.Restore));
        actions.Add(new CleanupAction(platform.Cleanup, PrivilegeLevel.User));
        actions.Add(new CleanupAction(platform.Cleanup, PrivilegeLevel.Administrator));
        actions.AddRange(SystemToolCatalog.All.Select(definition => new SystemToolAction(definition, platform.Tools, isSolidState)));
        return new ActionCatalog(actions);
    }

    /// <summary>Ermittelt für ein Laufwerk („C:“), ob es laut Hardwareerkennung eine SSD ist.</summary>
    public static Func<string, bool> SolidStateLookup(Func<SystemInfo?> info) => drive =>
        info()?.Volumes.FirstOrDefault(v => v.Letter.Equals(drive, StringComparison.OrdinalIgnoreCase))?.MediaType is DriveMediaType.Ssd or DriveMediaType.Nvme;
}

/// <summary>
/// Verbindung der Benutzeroberfläche zur getrennten Administratorkomponente. Diese wird erst bei Bedarf über die
/// Windows-Benutzerkontensteuerung gestartet – der Benutzer sieht und bestätigt die UAC-Abfrage; es gibt keine stille Rechteerhöhung.
/// </summary>
public sealed class ElevatedGateway : PipeGatewayBase, IDisposable
{
    public const string HelperFileName = "NexusOptimizer.Elevated.exe";
    private readonly string _helperPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private Process? _helper;

    public ElevatedGateway(string? helperPath = null, ILogger<ElevatedGateway>? logger = null)
    {
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, HelperFileName);
        _logger = logger ?? NullLogger<ElevatedGateway>.Instance;
    }

    public override bool IsConnected => _pipe is { IsConnected: true };
    public bool HelperExists => File.Exists(_helperPath);

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return;
        Disconnect();
        if (!HelperExists) throw new PrivilegeUnavailableException("Die Administratorkomponente wurde nicht gefunden. Bitte installieren Sie die Anwendung erneut.");

        var pipeName = PipeProtocol.PipeNamePrefix + Guid.NewGuid().ToString("N");
        string sid;
        using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User?.Value ?? throw new PrivilegeUnavailableException("Die Benutzerkennung konnte nicht ermittelt werden.");

        var start = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = true,
            Verb = "runas", // löst die sichtbare UAC-Abfrage aus
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--pipe {pipeName} --client-pid {Environment.ProcessId} --client-sid {sid}",
        };

        try
        {
            _helper = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new PrivilegeUnavailableException("Die Administratorfreigabe wurde in der Benutzerkontensteuerung abgelehnt. Es wurde nichts verändert.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new PrivilegeUnavailableException("Die Administratorkomponente konnte nicht gestartet werden: " + ex.Message, ex);
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            // Sicherstellen, dass am anderen Ende wirklich der soeben gestartete Prozess sitzt.
            if (_helper is not null && NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid) && serverPid != (uint)_helper.Id)
                throw new PrivilegeUnavailableException("Die Verbindung zur Administratorkomponente konnte nicht verifiziert werden.");

            _pipe = pipe;
            var pong = await ExchangeAsync(new PipeRequest { Operation = PipeOperation.Ping }, null, cancellationToken).ConfigureAwait(false);
            if (!pong.Ok) throw new PrivilegeUnavailableException("Die Administratorkomponente antwortet nicht wie erwartet.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            _pipe = null;
            if (cancellationToken.IsCancellationRequested) throw;
            throw new PrivilegeUnavailableException("Die Administratorkomponente hat die Verbindung nicht hergestellt.", ex);
        }
        catch
        {
            pipe.Dispose();
            _pipe = null;
            throw;
        }
    }

    private async Task<PipeResponse> ExchangeAsync(PipeRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var pipe = _pipe ?? throw new PrivilegeUnavailableException("Keine Verbindung zur Administratorkomponente.");
        await PipeProtocol.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var response = await PipeProtocol.ReadAsync<PipeResponse>(pipe, cancellationToken).ConfigureAwait(false)
                           ?? throw new PrivilegeUnavailableException("Die Administratorkomponente hat die Verbindung beendet.");
            if (response.RequestId != request.RequestId) continue;
            if (response.Kind == PipeMessageKind.Progress)
            {
                if (response.ProgressLine is not null) progress?.Report(response.ProgressLine);
                continue;
            }
            return response;
        }
    }

    protected override async Task<PipeResponse> SendAsync(PipeRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Eine begonnene privilegierte Operation wird nicht mittendrin abgebrochen.
                return await ExchangeAsync(request, progress, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "Verbindung zur Administratorkomponente verloren.");
                Disconnect();
                throw new PrivilegeUnavailableException("Die Verbindung zur Administratorkomponente wurde unterbrochen. Der Zustand der letzten Änderung wird im Verlauf als unterbrochen geführt.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Disconnect()
    {
        try { _pipe?.Dispose(); } catch (IOException) { }
        _pipe = null;
        try { _helper?.Dispose(); } catch (InvalidOperationException) { }
        _helper = null;
    }

    /// <summary>Beendet die Administratorkomponente; sie läuft nie länger als die Anwendung.</summary>
    public void Dispose()
    {
        try
        {
            if (_pipe is { IsConnected: true } pipe)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                PipeProtocol.WriteAsync(pipe, new PipeRequest { Operation = PipeOperation.Shutdown }, timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        Disconnect();
        _gate.Dispose();
    }
}
