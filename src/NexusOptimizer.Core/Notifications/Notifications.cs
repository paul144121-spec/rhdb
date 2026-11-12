namespace NexusOptimizer.Core.Notifications;

public enum NotificationKind
{
    OptimizationCompleted,
    OptimizationFailed,
    RestartRequired,
    HighTemperature,
    StorageLow,
    ProfileActivated,
    ProfileRestored,
    StartupRecommendation,
    BottleneckDetected,
    AiRecommendation,
    AutomationExecuted,
    RollbackCompleted,
    MonitoringAlert,
    MonitoringFailure,
    InterruptedOperation,
    General,
}

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record NotificationItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required NotificationKind Kind { get; init; }
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;
    public required string Title { get; init; }
    public string Message { get; init; } = "";
    public bool IsRead { get; init; }
    /// <summary>Seite, zu der die Benachrichtigung führt.</summary>
    public string? PageKey { get; init; }
}

public interface INotificationStore
{
    void Add(NotificationItem item);
    IReadOnlyList<NotificationItem> GetRecent(int limit = 100);
    void MarkAllRead();
    void DeleteAll();
}

public sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly List<NotificationItem> _items = new();
    public void Add(NotificationItem item) { lock (_items) _items.Add(item); }
    public IReadOnlyList<NotificationItem> GetRecent(int limit = 100) { lock (_items) return _items.AsEnumerable().Reverse().Take(limit).ToList(); }
    public void MarkAllRead() { lock (_items) for (var i = 0; i < _items.Count; i++) _items[i] = _items[i] with { IsRead = true }; }
    public void DeleteAll() { lock (_items) _items.Clear(); }
}

/// <summary>Zentrale Benachrichtigungsstelle: speichert dauerhaft und informiert die Oberfläche.</summary>
public sealed class NotificationCenter
{
    private readonly INotificationStore _store;

    public NotificationCenter(INotificationStore store) => _store = store;

    /// <summary>Abschaltbar über die Einstellungen; Fehler werden unabhängig davon immer gespeichert.</summary>
    public bool Enabled { get; set; } = true;
    public event EventHandler<NotificationItem>? Published;
    public event EventHandler? Changed;

    public NotificationItem Publish(NotificationKind kind, string title, string message = "", NotificationSeverity severity = NotificationSeverity.Info, string? pageKey = null)
    {
        var item = new NotificationItem { Kind = kind, Title = title, Message = message, Severity = severity, PageKey = pageKey };
        try { _store.Add(item); } catch (Exception) { /* Benachrichtigungen dürfen nie den auslösenden Vorgang stören */ }
        if (Enabled || severity == NotificationSeverity.Error) Published?.Invoke(this, item);
        Changed?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public IReadOnlyList<NotificationItem> GetRecent(int limit = 100) => _store.GetRecent(limit);
    public int UnreadCount => _store.GetRecent(200).Count(n => !n.IsRead);

    public void MarkAllRead()
    {
        _store.MarkAllRead();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _store.DeleteAll();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
