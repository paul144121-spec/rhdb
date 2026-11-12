using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Settings;

namespace NexusOptimizer.AI.Providers;

/// <summary>
/// Optionaler Cloud-Anbieter: Claude über das offizielle Anthropic-SDK (Streaming).
/// Wird nur verwendet, wenn der Benutzer ausdrücklich zugestimmt und einen eigenen API-Schlüssel hinterlegt hat.
/// </summary>
public sealed class AnthropicProvider : IAiProvider
{
    public const string DefaultModel = "claude-opus-5-5";
    /// <summary>Obergrenze der Antwortlänge je Anfrage (Streaming). Thinking-Token zählen mit.</summary>
    private const long MaxOutputTokens = 64000;
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Modelle, bei denen eine von der Sicherheitsprüfung abgelehnte Anfrage serverseitig von einem Ausweichmodell beantwortet werden kann.</summary>
    private static readonly HashSet<string> FallbackCapableModels = new(StringComparer.Ordinal)
    {
        "claude-fable-5-2", "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5",
    };

    private readonly AnthropicClient _client;

    public AnthropicProvider(string apiKey, string? model)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AiProviderException(AiErrorKind.NotConfigured, "Für Claude ist kein API-Schlüssel hinterlegt.");
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _client = new AnthropicClient { ApiKey = apiKey };
    }

    public AiProviderKind Kind => AiProviderKind.Anthropic;
    public string DisplayName => "Claude (Anthropic, Cloud)";
    public string Model { get; }
    public bool IsLocal => false;

    public async Task<AiConnectionResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Kostenfreie Prüfung: Modellliste abrufen statt eine Nachricht zu senden.
            var page = await _client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
            var models = page.Items.Select(m => m.ID).ToList();
            if (models.Count > 0 && !models.Contains(Model, StringComparer.Ordinal))
                return new AiConnectionResult(false, $"Der Schlüssel ist gültig, das Modell „{Model}“ steht diesem Konto jedoch nicht zur Verfügung.", models);
            return new AiConnectionResult(true, $"Verbindung zu Anthropic hergestellt. Modell: {Model}.", models);
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            return new AiConnectionResult(false, translated.Message, Array.Empty<string>());
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var useFallbacks = FallbackCapableModels.Contains(Model);
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = MaxOutputTokens,
            System = request.SystemPrompt,
            // Thinking bleibt adaptiv (Standard); die Tiefe wird über den Aufwand gesteuert.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            Messages = request.Messages.Select(ToParam).ToList(),
            // Lehnt die Sicherheitsprüfung eine Anfrage ab, beantwortet Anthropic sie innerhalb desselben Aufrufs mit einem Ausweichmodell.
            Betas = useFallbacks ? [ServerSideFallbackBeta] : null,
            Fallbacks = useFallbacks ? new BetaFallbacksParam(new Default()) : null,
        };

        var refused = false;
        string? stopReason = null;
        await using var enumerator = _client.Beta.Messages.CreateStreaming(parameters, cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            BetaRawMessageStreamEvent streamEvent;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                streamEvent = enumerator.Current;
            }
            catch (Exception ex) when (Translate(ex) is { } translated)
            {
                throw translated;
            }

            if (streamEvent.TryPickContentBlockDelta(out var blockDelta) && blockDelta.Delta.TryPickText(out var text))
            {
                if (!string.IsNullOrEmpty(text.Text)) yield return text.Text;
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                stopReason = messageDelta.Delta.StopReason?.Raw();
                refused = stopReason == "refusal";
            }
        }

        if (refused)
            throw new AiProviderException(AiErrorKind.Refused, "Claude hat diese Anfrage aus Sicherheitsgründen nicht beantwortet. Bitte formulieren Sie die Frage anders.");
        if (stopReason == "max_tokens")
            yield return "\n\n(Die Antwort wurde wegen der Längenbegrenzung gekürzt.)";
    }

    private static BetaMessageParam ToParam(AiChatMessage message)
    {
        if (message.Role == ChatRole.Assistant) return new BetaAssistantMessageParam { Content = message.Text };
        return new BetaUserMessageParam { Content = message.Text };
    }

    /// <summary>Bildet SDK-Ausnahmen – spezifischste zuerst – auf deutsche Meldungen ab. Abbrüche durch den Benutzer werden nicht übersetzt.</summary>
    private static AiProviderException? Translate(Exception exception) => exception switch
    {
        OperationCanceledException => null,
        AiProviderException own => own,
        AnthropicUnauthorizedException or AnthropicForbiddenException => new AiProviderException(AiErrorKind.Unauthorized, "Anthropic hat den API-Schlüssel abgelehnt. Bitte prüfen Sie den Schlüssel in den Einstellungen.", exception),
        AnthropicNotFoundException => new AiProviderException(AiErrorKind.ModelNotFound, "Das ausgewählte Claude-Modell ist nicht verfügbar. Bitte wählen Sie in den Einstellungen ein anderes Modell.", exception),
        AnthropicRateLimitException => new AiProviderException(AiErrorKind.RateLimited, "Das Anfragelimit bei Anthropic ist erreicht. Bitte versuchen Sie es in Kürze erneut.", exception),
        AnthropicBadRequestException => new AiProviderException(AiErrorKind.InvalidRequest, "Anthropic hat die Anfrage als ungültig zurückgewiesen (z. B. Modell oder Kontoeinstellung nicht passend).", exception),
        Anthropic5xxException => new AiProviderException(AiErrorKind.ServerError, "Anthropic meldet derzeit einen Serverfehler. Bitte versuchen Sie es später erneut.", exception),
        AnthropicIOException => new AiProviderException(AiErrorKind.Network, "Anthropic ist nicht erreichbar. Bitte prüfen Sie die Internetverbindung.", exception),
        AnthropicApiException => new AiProviderException(AiErrorKind.Other, "Die Anfrage an Anthropic ist fehlgeschlagen.", exception),
        HttpRequestException => new AiProviderException(AiErrorKind.Network, "Anthropic ist nicht erreichbar. Bitte prüfen Sie die Internetverbindung.", exception),
        _ => new AiProviderException(AiErrorKind.Other, "Unerwarteter Fehler bei der Anfrage an Anthropic.", exception),
    };
}
