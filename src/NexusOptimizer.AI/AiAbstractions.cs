using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Settings;

namespace NexusOptimizer.AI;

public sealed record AiChatMessage(ChatRole Role, string Text);

public sealed record AiRequest(string SystemPrompt, IReadOnlyList<AiChatMessage> Messages)
{
    /// <summary>Stabiler Schlüssel für den Antwort-Cache.</summary>
    public string CacheKey(string model)
    {
        var builder = new StringBuilder(model).Append('\n').Append(SystemPrompt);
        foreach (var message in Messages) builder.Append('\n').Append((int)message.Role).Append(':').Append(message.Text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

public enum AiErrorKind
{
    NotConfigured,
    Unauthorized,
    ModelNotFound,
    RateLimited,
    ServerError,
    Network,
    Timeout,
    Refused,
    InvalidRequest,
    ConsentRequired,
    Other,
}

/// <summary>Fehler eines KI-Anbieters mit deutscher, für die Anzeige geeigneter Meldung. Enthält nie Schlüssel oder Rohantworten.</summary>
public sealed class AiProviderException : Exception
{
    public AiErrorKind Kind { get; }
    public AiProviderException(AiErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

public sealed record AiConnectionResult(bool Success, string Message, IReadOnlyList<string> Models);

/// <summary>Abstraktion eines Sprachmodell-Anbieters. Die Ausgabe ist unstrukturierter, nicht vertrauenswürdiger Text.</summary>
public interface IAiProvider
{
    AiProviderKind Kind { get; }
    string DisplayName { get; }
    string Model { get; }
    /// <summary>True, wenn Anfragen den Computer nicht verlassen.</summary>
    bool IsLocal { get; }
    Task<AiConnectionResult> ValidateAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamAsync(AiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Gleitendes Zeitfenster zur Begrenzung von Anfragen (Kostenschutz bei Cloud-Diensten).</summary>
public sealed class AiRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _stamps = new();

    public AiRateLimiter(int maxRequests = 15, TimeSpan? window = null, TimeProvider? time = null)
    {
        _maxRequests = maxRequests;
        _window = window ?? TimeSpan.FromMinutes(1);
        _time = time ?? TimeProvider.System;
    }

    public bool TryAcquire(out TimeSpan retryAfter)
    {
        lock (_stamps)
        {
            var now = _time.GetUtcNow();
            while (_stamps.Count > 0 && now - _stamps.Peek() >= _window) _stamps.Dequeue();
            if (_stamps.Count >= _maxRequests)
            {
                retryAfter = _window - (now - _stamps.Peek());
                return false;
            }
            _stamps.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

/// <summary>Kleiner Zwischenspeicher im Arbeitsspeicher für identische Anfragen (z. B. erneutes Öffnen derselben Erklärung).</summary>
public sealed class AiResponseCache
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Text)> _entries = new();
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private readonly TimeProvider _time;

    public AiResponseCache(TimeSpan? lifetime = null, int capacity = 64, TimeProvider? time = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(10);
        _capacity = capacity;
        _time = time ?? TimeProvider.System;
    }

    public bool TryGet(string key, out string text)
    {
        if (_entries.TryGetValue(key, out var entry) && _time.GetUtcNow() - entry.At < _lifetime)
        {
            text = entry.Text;
            return true;
        }
        _entries.TryRemove(key, out _);
        text = "";
        return false;
    }

    public void Set(string key, string text)
    {
        if (_entries.Count >= _capacity)
        {
            foreach (var old in _entries.OrderBy(e => e.Value.At).Take(_capacity / 4 + 1)) _entries.TryRemove(old.Key, out _);
        }
        _entries[key] = (_time.GetUtcNow(), text);
    }

    public void Clear() => _entries.Clear();
}

public static class AiEndpointRules
{
    public static bool IsLoopback(Uri uri) => uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prüft eine Endpunktadresse: nur http/https, keine eingebetteten Zugangsdaten, unverschlüsselt nur lokal.</summary>
    public static Uri Validate(string? endpoint, bool allowPlainHttpOnLoopbackOnly = true)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new AiProviderException(AiErrorKind.NotConfigured, "Die Adresse des KI-Dienstes ist nicht gültig.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new AiProviderException(AiErrorKind.NotConfigured, "Die Adresse des KI-Dienstes muss mit http:// oder https:// beginnen.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new AiProviderException(AiErrorKind.NotConfigured, "Die Adresse des KI-Dienstes darf keine Zugangsdaten enthalten.");
        if (allowPlainHttpOnLoopbackOnly && uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
            throw new AiProviderException(AiErrorKind.NotConfigured, "Unverschlüsselte Verbindungen (http://) sind nur zu Diensten auf diesem Computer zulässig.");
        return uri;
    }
}
