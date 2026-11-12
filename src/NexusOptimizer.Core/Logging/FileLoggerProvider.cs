using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusOptimizer.Core.Security;

namespace NexusOptimizer.Core.Logging;

/// <summary>
/// Strukturierte Protokollierung als JSON-Zeilen mit täglicher Rotation. Jede Zeile wird vor dem Schreiben durch den
/// <see cref="LogRedactor"/> geführt, sodass weder Schlüssel noch Benutzer- oder Computernamen in Protokollen landen.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _retainDays;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    public string Directory => _directory;

    public FileLoggerProvider(string directory, string prefix = "nexus", int retainDays = 14)
    {
        _directory = directory;
        _prefix = prefix;
        _retainDays = retainDays;
        System.IO.Directory.CreateDirectory(directory);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Protokollschreiber", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    internal void Enqueue(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        var entry = new Dictionary<string, object?>
        {
            ["zeit"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["stufe"] = level.ToString(),
            ["kategorie"] = category,
            ["nachricht"] = LogRedactor.Redact(message),
        };
        if (eventId.Id != 0) entry["ereignis"] = eventId.Id;
        if (exception is not null) entry["ausnahme"] = LogRedactor.Redact(exception.ToString());

        // Ist die Warteschlange voll, wird die Zeile verworfen – Protokollierung darf die Anwendung nie blockieren.
        _queue.TryAdd(JsonSerializer.Serialize(entry, Options));
    }

    private void WriteLoop()
    {
        string? currentPath = null;
        StreamWriter? writer = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var path = Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log");
                    if (path != currentPath)
                    {
                        writer?.Dispose();
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                        currentPath = path;
                        Prune();
                    }
                    writer!.WriteLine(line);
                    if (_queue.Count == 0) writer.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    writer?.Dispose();
                    writer = null;
                    currentPath = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void Prune()
    {
        try
        {
            var limit = DateTime.Now.AddDays(-_retainDays);
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles($"{_prefix}-*.log").Where(f => f.LastWriteTime < limit)) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _provider.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try { _provider.Enqueue(logLevel, _category, eventId, formatter(state, exception), exception); }
            catch (InvalidOperationException) { /* Provider bereits beendet */ }
        }
    }
}
