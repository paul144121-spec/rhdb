namespace NexusOptimizer.Core.Chat;

public enum ChatRole
{
    User,
    Assistant,
    /// <summary>Von der Anwendung erzeugte Statusmeldung (z. B. tatsächliches Ausführungsergebnis) – nie vom Sprachmodell.</summary>
    System,
}

/// <summary>Art einer Assistentenaussage – die Oberfläche kennzeichnet jede Stufe eindeutig (Abschnitt 7.1).</summary>
public enum ChatMessageKind
{
    Explanation,
    Recommendation,
    PlannedAction,
    ConfirmedAction,
    ExecutedAction,
    VerifiedResult,
    Error,
}

public sealed record ChatMessageRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public ChatRole Role { get; init; }
    public ChatMessageKind Kind { get; init; } = ChatMessageKind.Explanation;
    public string Text { get; init; } = "";
    /// <summary>Name des Anbieters, der die Antwort erzeugt hat (z. B. „Regelwerk“ oder Modellname).</summary>
    public string? Provider { get; init; }
}

public interface IChatStore
{
    void Append(ChatMessageRecord message);
    IReadOnlyList<ChatMessageRecord> GetAll(int limit = 500);
    void DeleteAll();
}

public sealed class InMemoryChatStore : IChatStore
{
    private readonly List<ChatMessageRecord> _messages = new();
    public void Append(ChatMessageRecord message) { lock (_messages) _messages.Add(message); }
    public IReadOnlyList<ChatMessageRecord> GetAll(int limit = 500) { lock (_messages) return _messages.TakeLast(limit).ToList(); }
    public void DeleteAll() { lock (_messages) _messages.Clear(); }
}
