using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusOptimizer.AI.Providers;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Settings;

namespace NexusOptimizer.AI;

public interface IAiProviderFactory
{
    /// <summary>Liefert den Anbieter gemäß Einstellungen oder null für das Regelwerk; <paramref name="note"/> nennt ggf. den Grund.</summary>
    IAiProvider? Create(AppSettings settings, out string? note);
}

/// <summary>Erzeugt den konfigurierten Anbieter – oder null, wenn das Regelwerk verwendet wird. Cloud-Anbieter setzen eine ausdrückliche Zustimmung voraus.</summary>
public sealed class AiProviderFactory : IAiProviderFactory
{
    public const string AnthropicKeyName = "anthropic-api-key";
    public const string OpenAiKeyName = "openai-compatible-api-key";

    private readonly SettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly Func<HttpClient> _httpFactory;

    public AiProviderFactory(SettingsService settings, ISecretStore secrets, Func<HttpClient>? httpFactory = null)
    {
        _settings = settings;
        _secrets = secrets;
        _httpFactory = httpFactory ?? (() => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false }));
    }

    /// <summary>Liefert den Anbieter gemäß Einstellungen. Bei null nennt <paramref name="note"/> den Grund, sofern ein Sprachmodell gewählt, aber nicht nutzbar ist.</summary>
    public IAiProvider? Create(out string? note) => Create(_settings.Current, out note);

    public IAiProvider? Create(AppSettings settings, out string? note)
    {
        note = null;
        try
        {
            switch (settings.AiProvider)
            {
                case AiProviderKind.Ollama:
                {
                    var provider = new OllamaProvider(_httpFactory(), settings.OllamaEndpoint, settings.OllamaModel);
                    if (!provider.IsLocal && !settings.CloudAiConsent)
                    {
                        note = "Der eingetragene Ollama-Dienst läuft nicht auf diesem Computer. Dafür ist die Zustimmung zur Datenübermittlung in den Einstellungen erforderlich.";
                        return null;
                    }
                    if (string.IsNullOrWhiteSpace(settings.OllamaModel)) { note = "Für Ollama ist noch kein Modell ausgewählt."; return null; }
                    return provider;
                }

                case AiProviderKind.Anthropic:
                {
                    if (!settings.CloudAiConsent) { note = "Für den Cloud-Dienst liegt keine Zustimmung zur Datenübermittlung vor."; return null; }
                    var key = _secrets.Get(AnthropicKeyName);
                    if (string.IsNullOrWhiteSpace(key)) { note = "Für Claude ist kein API-Schlüssel hinterlegt."; return null; }
                    return new AnthropicProvider(key, settings.AnthropicModel);
                }

                case AiProviderKind.OpenAiCompatible:
                {
                    var provider = new OpenAiCompatibleProvider(_httpFactory(), settings.OpenAiEndpoint, settings.OpenAiModel, _secrets.Get(OpenAiKeyName));
                    if (!provider.IsLocal && !settings.CloudAiConsent) { note = "Für den Cloud-Dienst liegt keine Zustimmung zur Datenübermittlung vor."; return null; }
                    if (string.IsNullOrWhiteSpace(settings.OpenAiModel)) { note = "Es ist noch kein Modell ausgewählt."; return null; }
                    return provider;
                }

                default:
                    return null;
            }
        }
        catch (AiProviderException ex)
        {
            note = ex.Message;
            return null;
        }
    }
}

public abstract record ChatStreamItem;
public sealed record ChatTextChunk(string Text) : ChatStreamItem;
public sealed record ChatNotice(string Text, bool IsError) : ChatStreamItem;
public sealed record ChatProposalItem(ChatProposal Proposal) : ChatStreamItem;
public sealed record ChatCompleted(string ProviderLabel, ChatMessageKind Kind, bool UsedLanguageModel) : ChatStreamItem;

/// <summary>
/// Steuert den Chat: wählt Regelwerk oder Sprachmodell, liefert Antworten als Strom, prüft Vorschläge und speichert den Verlauf.
/// Der Assistent kann nie selbst etwas ausführen – Vorschläge werden der Oberfläche als geprüfte Karten übergeben.
/// </summary>
public sealed class ChatEngine
{
    public const string RuleBasedLabel = "Regelwerk (ohne KI-Modell)";
    private const int MaxHistoryMessages = 16;
    private const int MaxHistoryCharacters = 12_000;
    private const int MaxUserMessageLength = 4000;

    private readonly SettingsService _settings;
    private readonly SystemSnapshotService _snapshots;
    private readonly RuleBasedAdvisor _advisor;
    private readonly ActionCatalog _catalog;
    private readonly IChatStore _store;
    private readonly IAiProviderFactory _providers;
    private readonly RuleBasedResponder _responder;
    private readonly AiRateLimiter _limiter;
    private readonly ILogger _logger;
    private bool _disclosureShown;

    public ChatEngine(SettingsService settings, SystemSnapshotService snapshots, RuleBasedAdvisor advisor, ActionCatalog catalog, IChatStore store,
        IAiProviderFactory providers, Func<TransactionRecord?> lastChange, AiRateLimiter? limiter = null, ILogger<ChatEngine>? logger = null)
    {
        _settings = settings;
        _snapshots = snapshots;
        _advisor = advisor;
        _catalog = catalog;
        _store = store;
        _providers = providers;
        _responder = new RuleBasedResponder(advisor, catalog, lastChange);
        _limiter = limiter ?? new AiRateLimiter();
        _logger = logger ?? NullLogger<ChatEngine>.Instance;
    }

    public static IReadOnlyList<string> SuggestedQuestions { get; } = new[]
    {
        "Warum ist meine CPU-Auslastung so hoch?",
        "Welche Programme verlangsamen meinen PC?",
        "Analysiere meine RAM-Auslastung.",
        "Optimiere meinen PC für Gaming.",
        "Welche Einstellungen sollte ich ändern?",
        "Zeige mir alle riskanten Tweaks.",
        "Warum ist mein Internet so langsam?",
        "Überwache meine Temperaturen.",
        "Erstelle einen automatischen Optimierungsplan.",
        "Mache die letzte Optimierung rückgängig.",
    };

    /// <summary>Exakt der Systemkontext, der an ein Sprachmodell gesendet würde (für die Vorschau in den Einstellungen).</summary>
    public async Task<string> PreviewContextAsync(PrivacyMode? mode = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshots.CaptureAsync(null, cancellationToken).ConfigureAwait(false);
        return AiContextBuilder.Build(snapshot, mode ?? _settings.Current.AiPrivacyMode);
    }

    public IReadOnlyList<ChatMessageRecord> LoadHistory() => _settings.Current.SaveChatHistory ? _store.GetAll() : Array.Empty<ChatMessageRecord>();

    public void ClearHistory()
    {
        _store.DeleteAll();
        _disclosureShown = false;
    }

    /// <summary>Hält eine von der Anwendung erzeugte Statusmeldung (z. B. ein tatsächliches Ausführungsergebnis) im Verlauf fest.</summary>
    public ChatMessageRecord RecordSystemMessage(string text, ChatMessageKind kind)
    {
        var record = new ChatMessageRecord { Role = ChatRole.System, Kind = kind, Text = text, Provider = "Anwendung" };
        Persist(record);
        return record;
    }

    private void Persist(ChatMessageRecord record)
    {
        if (!_settings.Current.SaveChatHistory) return;
        try { _store.Append(record); }
        catch (Exception ex) { _logger.LogWarning(ex, "Chatverlauf konnte nicht gespeichert werden."); }
    }

    public async IAsyncEnumerable<ChatStreamItem> SendAsync(string userText, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        userText = (userText ?? "").Trim();
        if (userText.Length == 0) yield break;
        if (userText.Length > MaxUserMessageLength) userText = userText[..MaxUserMessageLength];

        var settings = _settings.Current;
        var history = BuildHistory();
        Persist(new ChatMessageRecord { Role = ChatRole.User, Text = userText });

        var snapshot = await _snapshots.CaptureAsync(null, cancellationToken).ConfigureAwait(false);
        var provider = _providers.Create(settings, out var providerNote);
        if (providerNote is not null) yield return new ChatNotice(providerNote + " Die Antwort stammt deshalb aus dem Regelwerk.", IsError: false);

        if (provider is not null && !provider.IsLocal && !_limiter.TryAcquire(out var retryAfter))
        {
            yield return new ChatNotice($"Zum Schutz vor unerwarteten Kosten ist die Zahl der Cloud-Anfragen begrenzt. Bitte warten Sie {Math.Ceiling(retryAfter.TotalSeconds)} Sekunden. Diese Antwort stammt aus dem Regelwerk.", IsError: false);
            provider = null;
        }

        if (provider is not null)
        {
            var completed = false;
            string? failure = null;
            var full = new StringBuilder();
            var emitted = 0;
            var blockSeen = false;
            var fence = "```" + AiProposalParser.BlockName;

            var request = new AiRequest(
                AiPromptBuilder.Build(AiContextBuilder.Build(snapshot, settings.AiPrivacyMode), settings.AiMode, _catalog, settings.ToRiskPolicy(), settings.PrimaryGoal),
                history.Append(new AiChatMessage(ChatRole.User, userText)).ToList());

            await using var enumerator = provider.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                string chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) { completed = true; break; }
                    chunk = enumerator.Current;
                }
                catch (AiProviderException ex)
                {
                    failure = ex.Message;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Unerwarteter Fehler des KI-Anbieters.");
                    failure = "Der KI-Dienst hat einen unerwarteten Fehler gemeldet.";
                    break;
                }

                full.Append(chunk);
                if (blockSeen) continue;

                // Sichtbaren Text ausgeben, den Vorschlagsblock aber zurückhalten – auch wenn dessen Beginn über mehrere Teile verteilt eintrifft.
                var text = full.ToString();
                var fenceIndex = text.IndexOf(fence, emitted, StringComparison.OrdinalIgnoreCase);
                int safeEnd;
                if (fenceIndex >= 0)
                {
                    safeEnd = fenceIndex;
                    blockSeen = true;
                }
                else
                {
                    var hold = 0;
                    for (var k = Math.Min(fence.Length - 1, text.Length - emitted); k > 0; k--)
                    {
                        if (text.EndsWith(fence[..k], StringComparison.OrdinalIgnoreCase)) { hold = k; break; }
                    }
                    safeEnd = text.Length - hold;
                }
                if (safeEnd > emitted)
                {
                    yield return new ChatTextChunk(text[emitted..safeEnd]);
                    emitted = safeEnd;
                }
            }

            if (completed)
            {
                var parsed = AiProposalParser.Parse(full.ToString(), _catalog);
                var visible = parsed.VisibleText;
                var fullText = full.ToString();
                if (!blockSeen)
                {
                    // Zurückgehaltene Zeichen nachreichen, die doch kein Vorschlagsblock waren.
                    if (emitted < fullText.Length) yield return new ChatTextChunk(fullText[emitted..]);
                }
                else
                {
                    // Text, den das Modell noch hinter den Vorschlagsblock geschrieben hat.
                    var blockStart = fullText.IndexOf(fence, StringComparison.OrdinalIgnoreCase);
                    var blockEnd = blockStart < 0 ? -1 : fullText.IndexOf("```", blockStart + fence.Length, StringComparison.Ordinal);
                    if (blockEnd >= 0 && blockEnd + 3 < fullText.Length && fullText[(blockEnd + 3)..].Trim() is { Length: > 0 } tail)
                        yield return new ChatTextChunk("\n" + tail);
                }

                foreach (var proposal in parsed.Proposals) yield return new ChatProposalItem(proposal);
                if (parsed.Rejected.Count > 0)
                    yield return new ChatNotice("Einige Vorschläge des Sprachmodells wurden von der Sicherheitsprüfung verworfen: " + string.Join(" ", parsed.Rejected), IsError: false);

                var kind = parsed.Proposals.Count > 0 ? ChatMessageKind.Recommendation : ChatMessageKind.Explanation;
                var label = $"{provider.DisplayName} · {provider.Model}";
                Persist(new ChatMessageRecord { Role = ChatRole.Assistant, Kind = kind, Text = visible, Provider = label });
                yield return new ChatCompleted(label, kind, UsedLanguageModel: true);
                yield break;
            }

            // Fehlerfall: verständlich melden und mit dem Regelwerk antworten, damit die Anwendung auch ohne KI-Dienst nutzbar bleibt.
            yield return new ChatNotice($"{failure} Die folgende Antwort stammt aus dem Regelwerk.", IsError: true);
            if (emitted > 0) yield return new ChatTextChunk("\n\n———\n\n");
        }

        var intent = GermanIntentParser.Parse(userText);
        var answer = _responder.Respond(intent, snapshot, settings.PrimaryGoal);
        var answerText = answer.Text;
        if (!_disclosureShown)
        {
            answerText += "\n\n" + RuleBasedResponder.Disclosure;
            _disclosureShown = true;
        }

        // Die Antwort wird absatzweise übergeben, damit die Oberfläche sie wie einen Strom darstellen kann.
        foreach (var part in SplitForStreaming(answerText))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatTextChunk(part);
        }
        foreach (var proposal in answer.Proposals) yield return new ChatProposalItem(proposal);

        Persist(new ChatMessageRecord { Role = ChatRole.Assistant, Kind = answer.Kind, Text = answerText, Provider = RuleBasedLabel });
        yield return new ChatCompleted(RuleBasedLabel, answer.Kind, UsedLanguageModel: false);
    }

    private List<AiChatMessage> BuildHistory()
    {
        if (!_settings.Current.SaveChatHistory) return new List<AiChatMessage>();
        var messages = _store.GetAll(MaxHistoryMessages * 2).Where(m => m.Role is ChatRole.User or ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text)).TakeLast(MaxHistoryMessages).ToList();

        var result = new List<AiChatMessage>();
        var budget = MaxHistoryCharacters;
        for (var i = messages.Count - 1; i >= 0 && budget > 0; i--)
        {
            var text = messages[i].Text.Length <= budget ? messages[i].Text : messages[i].Text[..budget];
            budget -= text.Length;
            result.Insert(0, new AiChatMessage(messages[i].Role, text));
        }
        // Eine Unterhaltung muss mit einer Benutzernachricht beginnen und Rollen müssen sich abwechseln.
        while (result.Count > 0 && result[0].Role != ChatRole.User) result.RemoveAt(0);
        var normalized = new List<AiChatMessage>();
        foreach (var message in result)
        {
            if (normalized.Count > 0 && normalized[^1].Role == message.Role)
                normalized[^1] = normalized[^1] with { Text = normalized[^1].Text + "\n\n" + message.Text };
            else normalized.Add(message);
        }
        if (normalized.Count > 0 && normalized[^1].Role == ChatRole.User) normalized.RemoveAt(normalized.Count - 1);
        return normalized;
    }

    internal static IEnumerable<string> SplitForStreaming(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            var length = end < 0 ? text.Length - start : end - start + 1;
            yield return text.Substring(start, length);
            start += length;
        }
    }
}
