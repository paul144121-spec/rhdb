using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Security;

/// <summary>Zulässige Operationen der privilegierten Komponente. Es gibt bewusst keine generische Befehlsausführung.</summary>
public enum PipeOperation
{
    Ping,
    Backup,
    Apply,
    Verify,
    Rollback,
    Shutdown,
}

public sealed record PipeRequest
{
    public int ProtocolVersion { get; init; } = PipeProtocol.Version;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public PipeOperation Operation { get; init; }
    public string ActionId { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = new();
    public string? BackupJson { get; init; }
    public string? BackupOriginalDisplay { get; init; }
    public string? BackupIntendedDisplay { get; init; }
    public bool Force { get; init; }
}

public enum PipeMessageKind
{
    Result,
    Progress,
}

public sealed record PipeResponse
{
    public string RequestId { get; init; } = "";
    public PipeMessageKind Kind { get; init; } = PipeMessageKind.Result;
    public bool Ok { get; init; }
    /// <summary>Fehler auf Protokoll-/Validierungsebene (nicht: fachlicher Fehlschlag der Aktion).</summary>
    public string? Error { get; init; }
    /// <summary>JSON des jeweiligen Ergebnisobjekts (BackupData, OperationResult, VerificationResult, RollbackResult).</summary>
    public string? PayloadJson { get; init; }
    public string? ProgressLine { get; init; }
}

/// <summary>Längenpräfigierte JSON-Nachrichten über eine benannte Pipe; mit harter Größenbegrenzung.</summary>
public static class PipeProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public const string PipeNamePrefix = "NexusOptimizerAI.Elevated.";

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxMessageBytes) throw new InvalidDataException("Die Nachricht überschreitet die zulässige Größe.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Liest eine Nachricht; liefert null, wenn die Gegenseite die Verbindung geschlossen hat.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : class
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("Ungültige Nachrichtenlänge.");

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException("Die Nachricht wurde nicht vollständig empfangen.");
        try
        {
            return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions { MaxDepth = 8 }) ?? throw new InvalidDataException("Leere Nachricht.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Die Nachricht ist kein gültiges JSON.", ex);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException("Verbindung unerwartet beendet.");
            offset += read;
        }
        return true;
    }
}

/// <summary>
/// Serverseitige Verarbeitung in der privilegierten Komponente. Vertraut dem Aufrufer nicht: Jede Anforderung wird vollständig
/// neu geprüft (Protokollversion, Operation, Allowlist, Parameterschema, tatsächlich benötigte Berechtigung, Systemkompatibilität).
/// </summary>
public sealed class PipeRequestHandler
{
    private readonly ActionCatalog _catalog;
    private readonly IActionRunner _runner;
    private readonly Func<SystemContext> _context;

    public PipeRequestHandler(ActionCatalog catalog, Func<SystemContext> context)
    {
        _catalog = catalog;
        _runner = new LocalActionRunner(catalog);
        _context = context;
    }

    public async Task<PipeResponse> HandleAsync(PipeRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        PipeResponse Reject(string error) => new() { RequestId = Sanitize(request.RequestId), Ok = false, Error = error };

        if (request.ProtocolVersion != PipeProtocol.Version) return Reject("Nicht unterstützte Protokollversion.");
        if (!Enum.IsDefined(request.Operation)) return Reject("Unbekannte Operation.");
        if (request.Operation is PipeOperation.Ping or PipeOperation.Shutdown) return new PipeResponse { RequestId = Sanitize(request.RequestId), Ok = true };

        if (!ActionCatalog.IsValidActionId(request.ActionId) || _catalog.Find(request.ActionId) is not { } action)
            return Reject("Die angeforderte Aktion ist nicht in der Allowlist enthalten.");
        if (request.Parameters is null || request.Parameters.Count > 16) return Reject("Ungültige Parameter.");

        var parameters = new ActionParameters(request.Parameters);
        var validation = action.ValidateParameters(parameters);
        if (!validation.IsValid) return Reject("Parameterprüfung fehlgeschlagen: " + validation.ErrorText);

        // Die privilegierte Komponente führt ausschließlich Aktionen aus, die tatsächlich Administratorrechte benötigen.
        if (action.GetRequiredPrivilege(parameters) != PrivilegeLevel.Administrator)
            return Reject("Diese Aktion benötigt keine Administratorrechte und wird hier nicht ausgeführt.");
        // Experimentelle Aktionen bleiben zulässig, da die Freischaltung in der Oberfläche erfolgt; kritische Sicherheitsziele sind bereits durch den Katalog ausgeschlossen.

        var context = _context();
        var support = await action.IsSupportedAsync(context, parameters, cancellationToken).ConfigureAwait(false);
        if (!support.IsSupported && request.Operation is PipeOperation.Apply or PipeOperation.Backup) return Reject("Nicht kompatibel: " + support.Reason);

        try
        {
            string payload;
            switch (request.Operation)
            {
                case PipeOperation.Backup:
                    payload = JsonSerializer.Serialize(await _runner.BackupAsync(request.ActionId, parameters, cancellationToken).ConfigureAwait(false));
                    break;
                case PipeOperation.Apply:
                    payload = JsonSerializer.Serialize(await _runner.ApplyAsync(request.ActionId, parameters, progress, cancellationToken).ConfigureAwait(false));
                    break;
                case PipeOperation.Verify:
                    payload = JsonSerializer.Serialize(await _runner.VerifyAsync(request.ActionId, parameters, cancellationToken).ConfigureAwait(false));
                    break;
                case PipeOperation.Rollback:
                    if (request.BackupJson is null || request.BackupJson.Length > 256 * 1024) return Reject("Die Sicherungsdaten fehlen oder sind zu groß.");
                    var backup = new BackupData(request.ActionId, request.BackupJson, request.BackupOriginalDisplay ?? "", request.BackupIntendedDisplay ?? "");
                    payload = JsonSerializer.Serialize(await _runner.RollbackAsync(request.ActionId, parameters, backup, request.Force, cancellationToken).ConfigureAwait(false));
                    break;
                default:
                    return Reject("Unbekannte Operation.");
            }
            return new PipeResponse { RequestId = Sanitize(request.RequestId), Ok = true, PayloadJson = payload };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Reject("Fehler bei der Ausführung: " + ex.Message);
        }
    }

    private static string Sanitize(string? id) => id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit) ? id : "invalid";
}

/// <summary>Clientseitige Abbildung von <see cref="IActionRunner"/> auf Pipe-Nachrichten.</summary>
public abstract class PipeGatewayBase : IPrivilegedGateway
{
    public abstract bool IsConnected { get; }

    /// <summary>Stellt die Verbindung her (inklusive UAC-Abfrage) und sendet eine Anforderung; Fortschrittszeilen werden weitergereicht.</summary>
    protected abstract Task<PipeResponse> SendAsync(PipeRequest request, IProgress<string>? progress, CancellationToken cancellationToken);

    private async Task<T> CallAsync<T>(PipeRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var response = await SendAsync(request, progress, cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.PayloadJson is null)
            throw new InvalidOperationException(response.Error ?? "Die privilegierte Komponente hat die Anforderung abgelehnt.");
        return JsonSerializer.Deserialize<T>(response.PayloadJson) ?? throw new InvalidDataException("Leere Antwort der privilegierten Komponente.");
    }

    private static PipeRequest Request(PipeOperation operation, string actionId, ActionParameters parameters)
        => new() { Operation = operation, ActionId = actionId, Parameters = parameters.Values.ToDictionary(p => p.Key, p => p.Value) };

    public Task<BackupData> BackupAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default)
        => CallAsync<BackupData>(Request(PipeOperation.Backup, actionId, parameters), null, cancellationToken);

    public Task<OperationResult> ApplyAsync(string actionId, ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => CallAsync<OperationResult>(Request(PipeOperation.Apply, actionId, parameters), progress, cancellationToken);

    public Task<VerificationResult> VerifyAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default)
        => CallAsync<VerificationResult>(Request(PipeOperation.Verify, actionId, parameters), null, cancellationToken);

    public Task<RollbackResult> RollbackAsync(string actionId, ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
        => CallAsync<RollbackResult>(Request(PipeOperation.Rollback, actionId, parameters) with
        {
            BackupJson = backup.PayloadJson, BackupOriginalDisplay = backup.OriginalDisplay, BackupIntendedDisplay = backup.IntendedDisplay, Force = force,
        }, null, cancellationToken);
}

/// <summary>Entfernt personenbezogene und vertrauliche Angaben aus Protokollzeilen.</summary>
public static class LogRedactor
{
    private static readonly System.Text.RegularExpressions.Regex[] SecretPatterns =
    {
        new(@"sk-ant-[A-Za-z0-9_\-]{8,}", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"sk-[A-Za-z0-9_\-]{16,}", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._\-+/=]{8,}", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"(?i)(x-api-key|api[_-]?key|authorization|password|passwort|token|secret)(""?\s*[:=]\s*""?)(?!\[ENTFERNT\]|Bearer\b|Basic\b)([^\s"",;]{4,})", System.Text.RegularExpressions.RegexOptions.Compiled),
    };

    private static readonly System.Text.RegularExpressions.Regex UserPath = new(@"(?i)([A-Z]:\\Users\\)([^\\""\s]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = SecretPatterns[0].Replace(text, "[SCHLÜSSEL ENTFERNT]");
        result = SecretPatterns[1].Replace(result, "[SCHLÜSSEL ENTFERNT]");
        result = SecretPatterns[2].Replace(result, m => m.Groups[1].Value + " [ENTFERNT]");
        result = SecretPatterns[3].Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + "[ENTFERNT]");
        result = UserPath.Replace(result, m => m.Groups[1].Value + "[Benutzer]");

        var user = Environment.UserName;
        if (user.Length >= 3) result = result.Replace(user, "[Benutzer]", StringComparison.OrdinalIgnoreCase);
        var machine = Environment.MachineName;
        if (machine.Length >= 3) result = result.Replace(machine, "[Computer]", StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
