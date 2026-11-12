using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Settings;

namespace NexusOptimizer.AI.Providers;

/// <summary>Gemeinsame HTTP-Grundlage: Zeitlimits für Verbindungsaufbau und Leerlauf, Fehlerabbildung auf deutsche Meldungen.</summary>
public abstract class HttpAiProviderBase : IAiProvider
{
    protected static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(45);
    protected static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);
    protected readonly HttpClient Http;

    protected HttpAiProviderBase(HttpClient http)
    {
        Http = http;
        Http.Timeout = Timeout.InfiniteTimeSpan; // Streaming: Zeitlimits werden pro Phase über CancellationToken gesteuert
    }

    public abstract AiProviderKind Kind { get; }
    public abstract string DisplayName { get; }
    public abstract string Model { get; }
    public abstract bool IsLocal { get; }
    public abstract Task<AiConnectionResult> ValidateAsync(CancellationToken cancellationToken = default);
    public abstract IAsyncEnumerable<string> StreamAsync(AiRequest request, CancellationToken cancellationToken = default);

    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FirstByteTimeout);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiErrorKind.Timeout, $"{DisplayName} hat nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiErrorKind.Network, IsLocal
                ? $"{DisplayName} ist nicht erreichbar. Läuft der Dienst auf diesem Computer?"
                : $"{DisplayName} ist nicht erreichbar. Bitte prüfen Sie die Internetverbindung.", ex);
        }

        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AiProviderException(AiErrorKind.Unauthorized, $"{DisplayName} hat den API-Schlüssel abgelehnt."),
            HttpStatusCode.NotFound => new AiProviderException(AiErrorKind.ModelNotFound, $"Das Modell „{Model}“ ist bei {DisplayName} nicht verfügbar."),
            HttpStatusCode.TooManyRequests => new AiProviderException(AiErrorKind.RateLimited, $"{DisplayName} meldet zu viele Anfragen. Bitte versuchen Sie es in Kürze erneut."),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new AiProviderException(AiErrorKind.InvalidRequest, $"{DisplayName} hat die Anfrage als ungültig zurückgewiesen."),
            >= HttpStatusCode.InternalServerError => new AiProviderException(AiErrorKind.ServerError, $"{DisplayName} meldet einen Serverfehler ({(int)status})."),
            _ => new AiProviderException(AiErrorKind.Other, $"{DisplayName} antwortete mit dem Status {(int)status}."),
        };
    }

    /// <summary>Liest die Antwort zeilenweise; bricht ab, wenn länger als <see cref="IdleTimeout"/> keine Daten eintreffen.</summary>
    protected async IAsyncEnumerable<string> ReadLinesAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(IdleTimeout);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiProviderException(AiErrorKind.Timeout, $"Die Antwort von {DisplayName} ist abgebrochen (keine Daten mehr empfangen).");
            }
            catch (IOException ex)
            {
                throw new AiProviderException(AiErrorKind.Network, $"Die Verbindung zu {DisplayName} wurde unterbrochen.", ex);
            }
            if (line is null) yield break;
            yield return line;
        }
    }

    protected static string RoleName(ChatRole role) => role == ChatRole.Assistant ? "assistant" : "user";
}

/// <summary>Lokales Sprachmodell über einen Ollama-Dienst (http://localhost:11434). Es verlassen keine Daten den Computer.</summary>
public sealed class OllamaProvider : HttpAiProviderBase
{
    private readonly Uri _endpoint;

    public OllamaProvider(HttpClient http, string endpoint, string model) : base(http)
    {
        _endpoint = AiEndpointRules.Validate(endpoint);
        Model = model;
    }

    public override AiProviderKind Kind => AiProviderKind.Ollama;
    public override string DisplayName => "Ollama (lokal)";
    public override string Model { get; }
    public override bool IsLocal => AiEndpointRules.IsLoopback(_endpoint);

    public override async Task<AiConnectionResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/tags")), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var models = document.RootElement.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList()
                : new List<string>();
            if (models.Count == 0) return new AiConnectionResult(false, "Ollama ist erreichbar, es ist aber noch kein Modell installiert (z. B. „ollama pull llama3.2“).", models);
            if (!string.IsNullOrEmpty(Model) && !models.Contains(Model, StringComparer.OrdinalIgnoreCase))
                return new AiConnectionResult(false, $"Ollama ist erreichbar, das Modell „{Model}“ ist jedoch nicht installiert.", models);
            return new AiConnectionResult(true, $"Verbindung zu Ollama hergestellt – {models.Count} Modell(e) verfügbar.", models);
        }
        catch (AiProviderException ex)
        {
            return new AiConnectionResult(false, ex.Message, Array.Empty<string>());
        }
        catch (JsonException)
        {
            return new AiConnectionResult(false, "Der Dienst unter dieser Adresse antwortet nicht wie Ollama.", Array.Empty<string>());
        }
    }

    public override async IAsyncEnumerable<string> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new AiProviderException(AiErrorKind.NotConfigured, "Für Ollama ist kein Modell ausgewählt.");

        var messages = new List<object> { new { role = "system", content = request.SystemPrompt } };
        messages.AddRange(request.Messages.Select(m => (object)new { role = RoleName(m.Role), content = m.Text }));
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/chat"))
        {
            Content = JsonContent.Create(new { model = Model, messages, stream = true }),
        };

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        await foreach (var line in ReadLinesAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? chunk = null;
            var done = false;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new AiProviderException(AiErrorKind.ServerError, "Ollama meldet einen Fehler: " + Truncate(error.ToString()));
                if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)) chunk = content.GetString();
                done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                continue; // einzelne unlesbare Zeilen überspringen
            }
            if (!string.IsNullOrEmpty(chunk)) yield return chunk;
            if (done) yield break;
        }
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}

/// <summary>
/// Sprachmodell über eine OpenAI-kompatible Chat-Completions-Schnittstelle (für Dienste anderer Anbieter oder selbst betriebene Server).
/// Der API-Schlüssel wird ausschließlich im Authorization-Header übertragen.
/// </summary>
public sealed class OpenAiCompatibleProvider : HttpAiProviderBase
{
    private readonly Uri _endpoint;
    private readonly string? _apiKey;

    public OpenAiCompatibleProvider(HttpClient http, string endpoint, string model, string? apiKey) : base(http)
    {
        _endpoint = AiEndpointRules.Validate(endpoint);
        Model = model;
        _apiKey = apiKey;
    }

    public override AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
    public override string DisplayName => IsLocal ? "OpenAI-kompatibler Dienst (lokal)" : $"OpenAI-kompatibler Dienst ({_endpoint.Host})";
    public override string Model { get; }
    public override bool IsLocal => AiEndpointRules.IsLoopback(_endpoint);

    private HttpRequestMessage Create(HttpMethod method, string relative)
    {
        var message = new HttpRequestMessage(method, new Uri(_endpoint, relative));
        if (!string.IsNullOrEmpty(_apiKey)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return message;
    }

    public override async Task<AiConnectionResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(Create(HttpMethod.Get, "models"), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var models = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).OrderBy(id => id).ToList()
                : new List<string>();
            return new AiConnectionResult(true, $"Verbindung hergestellt – {models.Count} Modell(e) gemeldet.", models);
        }
        catch (AiProviderException ex)
        {
            return new AiConnectionResult(false, ex.Message, Array.Empty<string>());
        }
        catch (JsonException)
        {
            return new AiConnectionResult(false, "Der Dienst antwortet nicht im erwarteten Format.", Array.Empty<string>());
        }
    }

    public override async IAsyncEnumerable<string> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new AiProviderException(AiErrorKind.NotConfigured, "Es ist kein Modell ausgewählt.");

        var messages = new List<object> { new { role = "system", content = request.SystemPrompt } };
        messages.AddRange(request.Messages.Select(m => (object)new { role = RoleName(m.Role), content = m.Text }));
        using var message = Create(HttpMethod.Post, "chat/completions");
        message.Content = JsonContent.Create(new { model = Model, messages, stream = true });

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        await foreach (var line in ReadLinesAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;
            if (payload.Length == 0) continue;

            string? chunk = null;
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    chunk = content.GetString();
            }
            catch (JsonException)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(chunk)) yield return chunk;
        }
    }
}
