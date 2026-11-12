using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusOptimizer.Core.Logging;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Security;
using NexusOptimizer.Windows;
using NexusOptimizer.Windows.Hardware;

namespace NexusOptimizer.Elevated;

/// <summary>
/// Privilegierte Komponente von NEXUS Optimizer AI. Sie wird ausschließlich von der Benutzeroberfläche über die
/// Benutzerkontensteuerung gestartet, nimmt genau eine Verbindung des aufrufenden Prozesses an und führt nur
/// Aktionen aus der gemeinsamen Allowlist aus. Es gibt keinen Weg, beliebige Befehle oder Programme zu starten.
/// </summary>
internal static class Program
{
    private const string ClientExecutableName = "NexusOptimizer.App.exe";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private enum ExitCode
    {
        Ok = 0,
        InvalidArguments = 2,
        NotElevated = 3,
        UntrustedClient = 4,
        ConnectionFailed = 5,
        UnexpectedError = 6,
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NexusOptimizerAI", "logs");
        using var loggerProvider = CreateLogger(logDirectory);
        var logger = loggerProvider?.CreateLogger("Elevated");
        try
        {
            var code = RunAsync(args, logger).GetAwaiter().GetResult();
            logger?.LogInformation("Administratorkomponente beendet ({Code}).", code);
            return (int)code;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unerwarteter Fehler in der Administratorkomponente.");
            return (int)ExitCode.UnexpectedError;
        }
    }

    private static FileLoggerProvider? CreateLogger(string directory)
    {
        try { return new FileLoggerProvider(directory, "elevated"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool TryParseArguments(string[] args, out string pipeName, out int clientPid, out SecurityIdentifier? clientSid)
    {
        pipeName = "";
        clientPid = 0;
        clientSid = null;
        if (args.Length != 6) return false;
        for (var i = 0; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe": pipeName = args[i + 1]; break;
                case "--client-pid" when int.TryParse(args[i + 1], out var pid) && pid > 4: clientPid = pid; break;
                case "--client-sid":
                    try { clientSid = new SecurityIdentifier(args[i + 1]); }
                    catch (ArgumentException) { return false; }
                    break;
                default: return false;
            }
        }
        return clientPid > 0 && clientSid is not null
               && Regex.IsMatch(pipeName, "^" + Regex.Escape(PipeProtocol.PipeNamePrefix) + "[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    }

    /// <summary>Der Aufrufer muss die Benutzeroberfläche aus demselben Installationsverzeichnis sein.</summary>
    private static bool IsTrustedClient(int processId, ILogger? logger)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            var expected = Path.Combine(AppContext.BaseDirectory, ClientExecutableName);
            var trusted = path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
            if (!trusted) logger?.LogWarning("Verbindungsversuch eines nicht vertrauenswürdigen Prozesses abgelehnt: {Path}", path);
            return trusted;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger?.LogWarning(ex, "Der aufrufende Prozess konnte nicht geprüft werden.");
            return false;
        }
    }

    private static async Task<ExitCode> RunAsync(string[] args, ILogger? logger)
    {
        if (!TryParseArguments(args, out var pipeName, out var clientPid, out var clientSid)) return ExitCode.InvalidArguments;
        if (!ProcessElevation.IsElevated) return ExitCode.NotElevated;
        if (!IsTrustedClient(clientPid, logger)) return ExitCode.UntrustedClient;

        // Zugriff auf die Pipe nur für das Benutzerkonto der Oberfläche und für dieses (erhöhte) Konto.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(clientSid!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        await using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        using (var connectTimeout = new CancellationTokenSource(ConnectTimeout))
        {
            try { await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return ExitCode.ConnectionFailed; }
        }

        // Nur genau der Prozess, der diese Komponente gestartet hat, darf verbunden sein.
        if (!NativePipe.GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var connectedPid) || connectedPid != (uint)clientPid)
        {
            logger?.LogWarning("Verbindung von unerwartetem Prozess {Pid} abgelehnt.", connectedPid);
            return ExitCode.UntrustedClient;
        }

        var platform = new WindowsPlatformServices();
        var hardware = new WindowsHardwareInfoProvider(platform.Power);
        var catalog = WindowsActionCatalogFactory.Create(platform, WindowsActionCatalogFactory.SolidStateLookup(() =>
        {
            try { return hardware.GetSystemInfoAsync().GetAwaiter().GetResult(); }
            catch (Exception) { return null; }
        }));
        var context = new SystemContext { WindowsBuild = Environment.OSVersion.Version.Build, IsElevated = true };
        var handler = new PipeRequestHandler(catalog, () => context);
        logger?.LogInformation("Administratorkomponente verbunden mit Prozess {Pid}; {Count} Aktionen in der Allowlist.", clientPid, catalog.All.Count);

        var writeLock = new SemaphoreSlim(1, 1);
        while (server.IsConnected)
        {
            PipeRequest? request;
            using (var idle = new CancellationTokenSource(IdleTimeout))
            {
                try { request = await PipeProtocol.ReadAsync<PipeRequest>(server, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { logger?.LogInformation("Leerlaufzeit überschritten – Komponente wird beendet."); break; }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    logger?.LogWarning(ex, "Ungültige oder abgebrochene Anforderung – Verbindung wird beendet.");
                    break;
                }
            }
            if (request is null || request.Operation == PipeOperation.Shutdown) break;

            var completed = false;
            var requestId = request.RequestId is { Length: > 0 and <= 64 } id && id.All(char.IsAsciiLetterOrDigit) ? id : "invalid";
            var progress = new SynchronousProgress(line =>
            {
                writeLock.Wait();
                try
                {
                    if (completed || !server.IsConnected) return;
                    PipeProtocol.WriteAsync(server, new PipeResponse { RequestId = requestId, Kind = PipeMessageKind.Progress, Ok = true, ProgressLine = line.Length > 2000 ? line[..2000] : line }).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                finally { writeLock.Release(); }
            });

            var response = await handler.HandleAsync(request, progress).ConfigureAwait(false);
            logger?.LogInformation("Anforderung {Operation} {ActionId}: {Result}", request.Operation, Sanitize(request.ActionId), response.Ok ? "ok" : "abgelehnt – " + response.Error);

            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                completed = true;
                await PipeProtocol.WriteAsync(server, response).ConfigureAwait(false);
            }
            catch (IOException) { break; }
            finally { writeLock.Release(); }
        }
        return ExitCode.Ok;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? "(leer)" : new string(value.Take(96).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());

    private sealed class SynchronousProgress : IProgress<string>
    {
        private readonly Action<string> _handler;
        public SynchronousProgress(Action<string> handler) => _handler = handler;
        public void Report(string value) => _handler(value);
    }

    private static class NativePipe
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
    }
}
