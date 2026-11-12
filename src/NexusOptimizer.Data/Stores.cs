using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Automation;
using NexusOptimizer.Core.Benchmark;
using NexusOptimizer.Core.Chat;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Gaming;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Profiles;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.Tuning;

namespace NexusOptimizer.Data;

/// <summary>Dauerhaftes Transaktionsjournal. Jeder Schreibvorgang wird mit synchronous=FULL festgeschrieben, bevor die Methode zurückkehrt.</summary>
public sealed class SqliteTransactionJournal : ITransactionJournal
{
    private const string Columns = "id, batch_id, timestamp, action_id, action_name, parameters_json, source, source_label, status, risk, privilege, restart, reversible, backup_json, original_display, intended_display, actual_display, verification_message, error_details, rolled_back_at";
    private readonly NexusDatabase _database;

    public SqliteTransactionJournal(NexusDatabase database) => _database = database;

    public void Insert(TransactionRecord record)
    {
        using var connection = _database.Open(durable: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO transactions (seq, {Columns})
            VALUES ((SELECT COALESCE(MAX(seq), 0) + 1 FROM transactions), $id, $batch, $ts, $action, $name, $params, $source, $label, $status, $risk, $priv, $restart, $rev, $backup, $orig, $intended, $actual, $verify, $error, $rolled);
            """;
        Bind(command, record);
        command.ExecuteNonQuery();
    }

    public void Update(TransactionRecord record)
    {
        using var connection = _database.Open(durable: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transactions SET status = $status, backup_json = $backup, original_display = $orig, intended_display = $intended, actual_display = $actual,
                verification_message = $verify, error_details = $error, rolled_back_at = $rolled
            WHERE id = $id;
            """;
        Bind(command, record);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"Journaleintrag {record.Id} nicht gefunden.");
    }

    private static void Bind(SqliteCommand command, TransactionRecord r)
    {
        command.Parameters.AddWithValue("$id", r.Id);
        command.Parameters.AddWithValue("$batch", r.BatchId);
        command.Parameters.AddWithValue("$ts", r.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$action", r.ActionId);
        command.Parameters.AddWithValue("$name", r.ActionName);
        command.Parameters.AddWithValue("$params", r.ParametersJson);
        command.Parameters.AddWithValue("$source", (int)r.Source);
        command.Parameters.AddWithValue("$label", r.SourceLabel);
        command.Parameters.AddWithValue("$status", (int)r.Status);
        command.Parameters.AddWithValue("$risk", (int)r.Risk);
        command.Parameters.AddWithValue("$priv", (int)r.PrivilegeUsed);
        command.Parameters.AddWithValue("$restart", (int)r.Restart);
        command.Parameters.AddWithValue("$rev", r.IsReversible ? 1 : 0);
        command.Parameters.AddWithValue("$backup", (object?)r.BackupJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$orig", r.OriginalDisplay);
        command.Parameters.AddWithValue("$intended", r.IntendedDisplay);
        command.Parameters.AddWithValue("$actual", r.ActualDisplay);
        command.Parameters.AddWithValue("$verify", r.VerificationMessage);
        command.Parameters.AddWithValue("$error", (object?)r.ErrorDetails ?? DBNull.Value);
        command.Parameters.AddWithValue("$rolled", r.RolledBackAt is { } at ? at.ToUnixTimeMilliseconds() : DBNull.Value);
    }

    private static TransactionRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        BatchId = reader.GetString(1),
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
        ActionId = reader.GetString(3),
        ActionName = reader.GetString(4),
        ParametersJson = reader.GetString(5),
        Source = (ExecutionSource)reader.GetInt32(6),
        SourceLabel = reader.GetString(7),
        Status = (TransactionStatus)reader.GetInt32(8),
        Risk = (RiskLevel)reader.GetInt32(9),
        PrivilegeUsed = (PrivilegeLevel)reader.GetInt32(10),
        Restart = (RestartRequirement)reader.GetInt32(11),
        IsReversible = reader.GetInt32(12) != 0,
        BackupJson = reader.IsDBNull(13) ? null : reader.GetString(13),
        OriginalDisplay = reader.GetString(14),
        IntendedDisplay = reader.GetString(15),
        ActualDisplay = reader.GetString(16),
        VerificationMessage = reader.GetString(17),
        ErrorDetails = reader.IsDBNull(18) ? null : reader.GetString(18),
        RolledBackAt = reader.IsDBNull(19) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(19)),
    };

    private List<TransactionRecord> Query(string where, Action<SqliteCommand>? bind = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM transactions {where};";
        bind?.Invoke(command);
        using var reader = command.ExecuteReader();
        var list = new List<TransactionRecord>();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    public TransactionRecord? Get(string id) => Query("WHERE id = $id", c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();

    public IReadOnlyList<TransactionRecord> GetRecent(int limit = 200)
        => Query("ORDER BY seq DESC LIMIT $limit", c => c.Parameters.AddWithValue("$limit", limit));

    public IReadOnlyList<TransactionRecord> GetBatch(string batchId)
        => Query("WHERE batch_id = $batch ORDER BY seq ASC", c => c.Parameters.AddWithValue("$batch", batchId));

    public IReadOnlyList<TransactionRecord> GetIncomplete()
        => Query($"WHERE status IN ({(int)TransactionStatus.Pending}, {(int)TransactionStatus.BackedUp}, {(int)TransactionStatus.Applied}) ORDER BY seq ASC");

    public IReadOnlyList<TransactionRecord> GetLaterActive(TransactionRecord record)
    {
        var later = Query(
            $"WHERE action_id = $action AND seq > (SELECT seq FROM transactions WHERE id = $id) AND status IN ({(int)TransactionStatus.Applied}, {(int)TransactionStatus.Verified}, {(int)TransactionStatus.VerificationFailed}) ORDER BY seq ASC",
            c => { c.Parameters.AddWithValue("$action", record.ActionId); c.Parameters.AddWithValue("$id", record.Id); });
        return later.Where(r => r.Parameters.Equals(record.Parameters)).ToList();
    }

    public void DeleteAll()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transactions;";
        command.ExecuteNonQuery();
    }
}

public sealed class SqliteMetricHistoryStore : IMetricHistoryStore
{
    private readonly NexusDatabase _database;

    public SqliteMetricHistoryStore(NexusDatabase database) => _database = database;

    public void Append(DateTimeOffset timestamp, IReadOnlyDictionary<MetricId, double> values)
    {
        if (values.Count == 0) return;
        using var connection = _database.Open(durable: false);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO metrics (timestamp, metric, value) VALUES ($ts, $metric, $value);";
        var ts = command.Parameters.Add("$ts", SqliteType.Integer);
        var metric = command.Parameters.Add("$metric", SqliteType.Integer);
        var value = command.Parameters.Add("$value", SqliteType.Real);
        ts.Value = timestamp.ToUnixTimeMilliseconds();
        foreach (var (id, v) in values)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            metric.Value = (int)id;
            value.Value = v;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<MetricPoint> Query(MetricId metric, DateTimeOffset from, DateTimeOffset to, int maxPoints = 600)
    {
        using var connection = _database.Open(durable: false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT timestamp, value FROM metrics WHERE metric = $metric AND timestamp >= $from AND timestamp <= $to ORDER BY timestamp ASC;";
        command.Parameters.AddWithValue("$metric", (int)metric);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        var points = new List<MetricPoint>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) points.Add(new MetricPoint(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetDouble(1)));
        }
        return Downsample(points, maxPoints);
    }

    /// <summary>Verdichtet auf höchstens <paramref name="maxPoints"/> Punkte durch Mittelwertbildung gleich großer Abschnitte.</summary>
    public static IReadOnlyList<MetricPoint> Downsample(IReadOnlyList<MetricPoint> points, int maxPoints)
    {
        if (maxPoints < 1 || points.Count <= maxPoints) return points;
        var result = new List<MetricPoint>(maxPoints);
        var bucketSize = (double)points.Count / maxPoints;
        for (var bucket = 0; bucket < maxPoints; bucket++)
        {
            var start = (int)(bucket * bucketSize);
            var end = Math.Min(points.Count, Math.Max(start + 1, (int)((bucket + 1) * bucketSize)));
            double sum = 0;
            for (var i = start; i < end; i++) sum += points[i].Value;
            result.Add(new MetricPoint(points[start + (end - start) / 2].Timestamp, sum / (end - start)));
        }
        return result;
    }

    public int Prune(DateTimeOffset olderThan)
    {
        using var connection = _database.Open(durable: false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metrics WHERE timestamp < $limit;";
        command.Parameters.AddWithValue("$limit", olderThan.ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }

    public void DeleteAll()
    {
        using var connection = _database.Open(durable: false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metrics;";
        command.ExecuteNonQuery();
    }
}

/// <summary>Einfache JSON-Dokumentablage je Sammlung – Grundlage der übrigen Speicher.</summary>
public sealed class DocumentCollection<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() }, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly NexusDatabase _database;
    private readonly string _collection;

    public DocumentCollection(NexusDatabase database, string collection)
    {
        _database = database;
        _collection = collection;
    }

    public void Upsert(string id, T document, DateTimeOffset? timestamp = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO documents (collection, id, timestamp, json) VALUES ($c, $id, $ts, $json) ON CONFLICT(collection, id) DO UPDATE SET timestamp = excluded.timestamp, json = excluded.json;";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(document, Options));
        command.ExecuteNonQuery();
    }

    public T? Get(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM documents WHERE collection = $c AND id = $id;";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? Deserialize(json) : null;
    }

    /// <summary>Liefert Dokumente nach Zeitstempel; unlesbare (beschädigte) Einträge werden übersprungen statt den Aufruf scheitern zu lassen.</summary>
    public IReadOnlyList<T> GetAll(int limit = 10_000, bool newestFirst = false)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT json FROM documents WHERE collection = $c ORDER BY timestamp {(newestFirst ? "DESC" : "ASC")}, id ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            if (Deserialize(reader.GetString(0)) is { } item) list.Add(item);
        }
        return list;
    }

    public bool Delete(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM documents WHERE collection = $c AND id = $id;";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public void DeleteAll()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM documents WHERE collection = $c;";
        command.Parameters.AddWithValue("$c", _collection);
        command.ExecuteNonQuery();
    }

    /// <summary>Begrenzt die Sammlung auf die neuesten <paramref name="keep"/> Einträge.</summary>
    public void Trim(int keep)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM documents WHERE collection = $c AND id NOT IN (SELECT id FROM documents WHERE collection = $c ORDER BY timestamp DESC LIMIT $keep);";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$keep", keep);
        command.ExecuteNonQuery();
    }

    private static T? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
    }
}

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly DocumentCollection<AppSettings> _documents;
    public SqliteSettingsStore(NexusDatabase database) => _documents = new DocumentCollection<AppSettings>(database, "settings");
    /// <summary>Fehlende oder beschädigte Einstellungen führen zu den Standardwerten – nie zu einem Startabbruch.</summary>
    public AppSettings Load() => _documents.Get("app") ?? new AppSettings();
    public void Save(AppSettings settings) => _documents.Upsert("app", settings);
}

public sealed class SqliteNotificationStore : INotificationStore
{
    private const int MaxItems = 500;
    private readonly DocumentCollection<NotificationItem> _documents;
    private int _sinceTrim;
    public SqliteNotificationStore(NexusDatabase database) => _documents = new DocumentCollection<NotificationItem>(database, "notifications");

    public void Add(NotificationItem item)
    {
        _documents.Upsert(item.Id, item, item.Timestamp);
        if (Interlocked.Increment(ref _sinceTrim) % 50 == 0) _documents.Trim(MaxItems);
    }

    public IReadOnlyList<NotificationItem> GetRecent(int limit = 100) => _documents.GetAll(limit, newestFirst: true);

    public void MarkAllRead()
    {
        foreach (var item in _documents.GetAll(MaxItems).Where(i => !i.IsRead)) _documents.Upsert(item.Id, item with { IsRead = true }, item.Timestamp);
    }

    public void DeleteAll() => _documents.DeleteAll();
}

public sealed class SqliteChatStore : IChatStore
{
    private readonly DocumentCollection<ChatMessageRecord> _documents;
    public SqliteChatStore(NexusDatabase database) => _documents = new DocumentCollection<ChatMessageRecord>(database, "chat");
    public void Append(ChatMessageRecord message) => _documents.Upsert(message.Id, message, message.Timestamp);
    public IReadOnlyList<ChatMessageRecord> GetAll(int limit = 500) => _documents.GetAll(limit, newestFirst: true).Reverse().ToList();
    public void DeleteAll() => _documents.DeleteAll();
}

public sealed class SqliteFeedbackStore : IFeedbackStore
{
    private sealed record Entry(string ActionId, FeedbackKind Kind, string? Note, DateTimeOffset Timestamp);
    private readonly DocumentCollection<Entry> _documents;
    public SqliteFeedbackStore(NexusDatabase database) => _documents = new DocumentCollection<Entry>(database, "feedback");

    public void Record(string actionId, FeedbackKind kind, string? note = null)
    {
        var now = DateTimeOffset.UtcNow;
        _documents.Upsert(Guid.NewGuid().ToString("N"), new Entry(actionId, kind, note, now), now);
    }

    public IReadOnlySet<string> GetDeclined()
        => _documents.GetAll().GroupBy(e => e.ActionId).Where(g => g.OrderBy(e => e.Timestamp).Last().Kind is FeedbackKind.Declined or FeedbackKind.Regressed).Select(g => g.Key).ToHashSet();

    public IReadOnlyDictionary<string, int> GetCounts(FeedbackKind kind)
        => _documents.GetAll().Where(e => e.Kind == kind).GroupBy(e => e.ActionId).ToDictionary(g => g.Key, g => g.Count());

    public void Clear() => _documents.DeleteAll();
}

public sealed class SqliteAutomationStore : IAutomationStore
{
    private readonly DocumentCollection<AutomationRule> _rules;
    private readonly DocumentCollection<AutomationRunRecord> _history;
    private int _sinceTrim;

    public SqliteAutomationStore(NexusDatabase database)
    {
        _rules = new DocumentCollection<AutomationRule>(database, "automation-rules");
        _history = new DocumentCollection<AutomationRunRecord>(database, "automation-history");
    }

    public IReadOnlyList<AutomationRule> GetRules() => _rules.GetAll();
    public void SaveRule(AutomationRule rule) => _rules.Upsert(rule.Id, rule);
    public void DeleteRule(string ruleId) => _rules.Delete(ruleId);

    public void AppendHistory(AutomationRunRecord record)
    {
        _history.Upsert(Guid.NewGuid().ToString("N"), record, record.Timestamp);
        if (Interlocked.Increment(ref _sinceTrim) % 50 == 0) _history.Trim(1000);
    }

    public IReadOnlyList<AutomationRunRecord> GetHistory(string? ruleId = null, int limit = 200)
        => _history.GetAll(1000, newestFirst: true).Where(h => ruleId is null || h.RuleId == ruleId).Take(limit).ToList();
}

public sealed class SqliteProfileStore : IProfileStore
{
    private sealed record ActiveState(string? ProfileId, string? BatchId);
    private readonly DocumentCollection<OptimizationProfile> _profiles;
    private readonly DocumentCollection<ActiveState> _state;

    public SqliteProfileStore(NexusDatabase database)
    {
        _profiles = new DocumentCollection<OptimizationProfile>(database, "profiles");
        _state = new DocumentCollection<ActiveState>(database, "profile-state");
    }

    public IReadOnlyList<OptimizationProfile> GetCustomProfiles() => _profiles.GetAll().Select(p => p with { IsBuiltIn = false }).ToList();
    public void Save(OptimizationProfile profile) => _profiles.Upsert(profile.Id, profile with { IsBuiltIn = false });
    public void Delete(string profileId) => _profiles.Delete(profileId);

    public (string? ProfileId, string? BatchId) GetActive()
    {
        var state = _state.Get("active");
        return (state?.ProfileId, state?.BatchId);
    }

    public void SetActive(string? profileId, string? batchId) => _state.Upsert("active", new ActiveState(profileId, batchId));
}

public sealed class SqliteGameStore : IGameStore
{
    private readonly DocumentCollection<GameInfo> _games;
    private readonly DocumentCollection<GameProfile> _profiles;
    private readonly DocumentCollection<GameSessionRecord> _sessions;

    public SqliteGameStore(NexusDatabase database)
    {
        _games = new DocumentCollection<GameInfo>(database, "games");
        _profiles = new DocumentCollection<GameProfile>(database, "game-profiles");
        _sessions = new DocumentCollection<GameSessionRecord>(database, "game-sessions");
    }

    public IReadOnlyList<GameInfo> GetGames() => _games.GetAll().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public void SaveGames(IReadOnlyList<GameInfo> games)
    {
        // Manuell hinzugefügte Spiele bleiben erhalten; erkannte Einträge werden durch den aktuellen Scan ersetzt.
        var keep = _games.GetAll().Where(g => g.Platform == GamePlatform.Manual).ToList();
        _games.DeleteAll();
        foreach (var game in games.Concat(keep).GroupBy(g => g.Id).Select(g => g.First())) _games.Upsert(game.Id, game);
    }

    public IReadOnlyList<GameProfile> GetProfiles() => _profiles.GetAll();
    public void SaveProfile(GameProfile profile) => _profiles.Upsert(profile.GameId, profile);
    public void DeleteProfile(string gameId) => _profiles.Delete(gameId);
    public void AppendSession(GameSessionRecord session) => _sessions.Upsert(session.Id, session, session.EndedAt);

    public IReadOnlyList<GameSessionRecord> GetSessions(string? gameId = null, int limit = 100)
        => _sessions.GetAll(2000, newestFirst: true).Where(s => gameId is null || s.GameId == gameId).Take(limit).ToList();
}

public sealed class SqliteBenchmarkStore : IBenchmarkStore
{
    private readonly DocumentCollection<BenchmarkResult> _documents;
    public SqliteBenchmarkStore(NexusDatabase database) => _documents = new DocumentCollection<BenchmarkResult>(database, "benchmarks");
    public void Save(BenchmarkResult result) => _documents.Upsert(result.Id, result, result.Timestamp);
    public IReadOnlyList<BenchmarkResult> GetHistory(BenchmarkKind? kind = null, int limit = 200)
        => _documents.GetAll(2000, newestFirst: true).Where(r => kind is null || r.Kind == kind).Take(limit).ToList();
    public void DeleteAll() => _documents.DeleteAll();
}

public sealed class SqliteTuningStore : ITuningStore
{
    private readonly DocumentCollection<TuningExperimentResult> _documents;
    public SqliteTuningStore(NexusDatabase database) => _documents = new DocumentCollection<TuningExperimentResult>(database, "tuning");
    public void Save(TuningExperimentResult result) => _documents.Upsert(result.Id, result, result.Timestamp);
    public IReadOnlyList<TuningExperimentResult> GetHistory(int limit = 100) => _documents.GetAll(limit, newestFirst: true);
}
