using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Engine;

/// <summary>Dauerhafter Journaleintrag einer einzelnen Systemänderung (Abschnitt 26.2).</summary>
public sealed record TransactionRecord
{
    public required string Id { get; init; }
    /// <summary>Gemeinsame Kennung aller Änderungen eines Optimierungslaufs.</summary>
    public required string BatchId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string ActionId { get; init; }
    public required string ActionName { get; init; }
    public string ParametersJson { get; init; } = "{}";
    public ExecutionSource Source { get; init; }
    public string SourceLabel { get; init; } = "";
    public TransactionStatus Status { get; init; } = TransactionStatus.Pending;
    public RiskLevel Risk { get; init; }
    public PrivilegeLevel PrivilegeUsed { get; init; }
    public RestartRequirement Restart { get; init; }
    public bool IsReversible { get; init; }
    public string? BackupJson { get; init; }
    public string OriginalDisplay { get; init; } = "";
    public string IntendedDisplay { get; init; } = "";
    public string ActualDisplay { get; init; } = "";
    public string VerificationMessage { get; init; } = "";
    public string? ErrorDetails { get; init; }
    public DateTimeOffset? RolledBackAt { get; init; }

    public ActionParameters Parameters => ActionParameters.FromJson(ParametersJson);
    public bool CanRollback => IsReversible && BackupJson is not null
        && Status is TransactionStatus.Applied or TransactionStatus.Verified or TransactionStatus.VerificationFailed or TransactionStatus.Interrupted or TransactionStatus.RollbackFailed or TransactionStatus.BackedUp;
    public bool IsActive => Status is TransactionStatus.Applied or TransactionStatus.Verified or TransactionStatus.VerificationFailed;
}

/// <summary>Dauerhaftes Transaktionsjournal. Jeder Schreibvorgang muss vor der Rückkehr persistiert sein.</summary>
public interface ITransactionJournal
{
    void Insert(TransactionRecord record);
    void Update(TransactionRecord record);
    TransactionRecord? Get(string id);
    IReadOnlyList<TransactionRecord> GetRecent(int limit = 200);
    IReadOnlyList<TransactionRecord> GetBatch(string batchId);
    /// <summary>Einträge, die beim letzten Lauf nicht abgeschlossen wurden (Status Pending/BackedUp/Applied).</summary>
    IReadOnlyList<TransactionRecord> GetIncomplete();
    /// <summary>Spätere, noch aktive Änderungen derselben Aktion mit denselben Parametern.</summary>
    IReadOnlyList<TransactionRecord> GetLaterActive(TransactionRecord record);
    void DeleteAll();
}

/// <summary>Flüchtiges Journal für Tests und Entwurfszeit.</summary>
public sealed class InMemoryTransactionJournal : ITransactionJournal
{
    private readonly List<TransactionRecord> _records = new();
    private readonly object _gate = new();

    public void Insert(TransactionRecord record) { lock (_gate) _records.Add(record); }

    public void Update(TransactionRecord record)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.Id == record.Id);
            if (index < 0) throw new KeyNotFoundException(record.Id);
            _records[index] = record;
        }
    }

    public TransactionRecord? Get(string id) { lock (_gate) return _records.FirstOrDefault(r => r.Id == id); }
    public IReadOnlyList<TransactionRecord> GetRecent(int limit = 200) { lock (_gate) return _records.AsEnumerable().Reverse().Take(limit).ToList(); }
    public IReadOnlyList<TransactionRecord> GetBatch(string batchId) { lock (_gate) return _records.Where(r => r.BatchId == batchId).ToList(); }

    public IReadOnlyList<TransactionRecord> GetIncomplete()
    {
        lock (_gate) return _records.Where(r => r.Status is TransactionStatus.Pending or TransactionStatus.BackedUp or TransactionStatus.Applied).ToList();
    }

    public IReadOnlyList<TransactionRecord> GetLaterActive(TransactionRecord record)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.Id == record.Id);
            return _records.Skip(index + 1).Where(r => r.IsActive && r.ActionId == record.ActionId && r.Parameters.Equals(record.Parameters)).ToList();
        }
    }

    public void DeleteAll() { lock (_gate) _records.Clear(); }
}
