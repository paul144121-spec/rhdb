using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Engine;

public sealed record ExecutionProgress(int StepIndex, int StepCount, string ActionName, string Phase, string? Detail = null);

public sealed record ExecutionOptions
{
    public bool StopOnFirstFailure { get; init; }
    /// <summary>Bei einem Fehler alle bereits erfolgreichen Schritte dieses Laufs in umgekehrter Reihenfolge zurückrollen.</summary>
    public bool RollbackAllOnFailure { get; init; }
    public IProgress<ExecutionProgress>? Progress { get; init; }
    public string SourceLabel { get; init; } = "";
    /// <summary>Schlüssel (<see cref="PlanStep.Key"/>) der Schritte, die der Benutzer einzeln bestätigt hat.</summary>
    public IReadOnlySet<string> IndividuallyConfirmed { get; init; } = new HashSet<string>();
}

public sealed record StepResult(PlanStep Step, TransactionRecord? Transaction, bool Success, string Message)
{
    public bool Verified => Transaction?.Status == TransactionStatus.Verified;
}

public sealed record BatchResult
{
    public required string BatchId { get; init; }
    public required IReadOnlyList<StepResult> Steps { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>True, wenn der Lauf abgelehnt wurde, weil bereits eine Optimierung läuft.</summary>
    public bool Busy { get; init; }
    public bool RolledBackAfterFailure { get; init; }
    public int SucceededCount => Steps.Count(s => s.Success);
    public int FailedCount => Steps.Count(s => !s.Success);
    public bool AllSucceeded => !Busy && !Cancelled && Steps.Count > 0 && Steps.All(s => s.Success);
    public RestartRequirement Restart => Steps.Where(s => s.Success).Select(s => s.Step.Metadata.Restart).DefaultIfEmpty(RestartRequirement.None).Max();
}

/// <summary>
/// Vertrauenswürdige Ausführungs-Engine. Führt ausschließlich Pläne des <see cref="OptimizationPlanner"/> aus und schreibt
/// jeden Schritt vor und nach der Änderung in das dauerhafte Journal, damit unterbrochene Läufe untersucht und zurückgerollt werden können.
/// </summary>
public sealed class OptimizationExecutor
{
    private readonly IActionRunner _runner;
    private readonly ITransactionJournal _journal;
    private readonly ExecutionGate _gate;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public OptimizationExecutor(IActionRunner runner, ITransactionJournal journal, ExecutionGate gate, ILogger<OptimizationExecutor>? logger = null, TimeProvider? time = null)
    {
        _runner = runner;
        _journal = journal;
        _gate = gate;
        _logger = logger ?? NullLogger<OptimizationExecutor>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<BatchResult>? BatchCompleted;

    public async Task<BatchResult> ExecuteAsync(OptimizationPlan plan, ExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExecutionOptions();
        var batchId = Guid.NewGuid().ToString("N");

        if (!_gate.TryEnter())
        {
            _logger.LogWarning("Optimierungslauf abgelehnt: Es läuft bereits ein Vorgang.");
            return new BatchResult { BatchId = batchId, Steps = Array.Empty<StepResult>(), Busy = true };
        }

        var results = new List<StepResult>();
        var cancelled = false;
        var rolledBack = false;
        var privilegeLost = false;

        try
        {
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                // Hängt der Schritt von einem in diesem Lauf fehlgeschlagenen Schritt ab, wird er übersprungen.
                var failedDependency = step.Metadata.Dependencies.FirstOrDefault(d => results.Any(r => !r.Success && r.Step.Metadata.Id == d));
                if (failedDependency is not null)
                {
                    results.Add(new StepResult(step, null, false, $"Übersprungen, weil die Voraussetzung „{failedDependency}“ fehlgeschlagen ist."));
                    continue;
                }

                if (step.RequiresIndividualConfirmation && !options.IndividuallyConfirmed.Contains(step.Key))
                {
                    results.Add(new StepResult(step, null, false, "Übersprungen: Für diese Änderung ist eine gesonderte Bestätigung erforderlich."));
                    continue;
                }

                if (privilegeLost && step.Privilege == PrivilegeLevel.Administrator)
                {
                    results.Add(new StepResult(step, null, false, "Übersprungen: Die Administratorfreigabe wurde nicht erteilt."));
                    continue;
                }

                options.Progress?.Report(new ExecutionProgress(index, plan.Steps.Count, step.Metadata.Name, "Wird vorbereitet"));
                var (result, lostPrivilege) = await ExecuteStepAsync(plan, step, batchId, index, options).ConfigureAwait(false);
                privilegeLost |= lostPrivilege;
                results.Add(result);

                if (!result.Success && (options.StopOnFirstFailure || options.RollbackAllOnFailure)) break;
            }

            if (options.RollbackAllOnFailure && results.Any(r => !r.Success) && results.Any(r => r.Success))
            {
                rolledBack = true;
                for (var i = results.Count - 1; i >= 0; i--)
                {
                    var item = results[i];
                    if (!item.Success || item.Transaction is not { CanRollback: true } transaction) continue;
                    var outcome = await RollbackTransactionCoreAsync(transaction, force: false).ConfigureAwait(false);
                    results[i] = item with { Transaction = outcome.Transaction, Success = false, Message = "Wegen eines Fehlers im Lauf zurückgerollt: " + outcome.Message };
                }
            }
        }
        finally
        {
            _gate.Exit();
        }

        var batch = new BatchResult { BatchId = batchId, Steps = results, Cancelled = cancelled, RolledBackAfterFailure = rolledBack };
        BatchCompleted?.Invoke(this, batch);
        return batch;
    }

    private async Task<(StepResult Result, bool PrivilegeLost)> ExecuteStepAsync(OptimizationPlan plan, PlanStep step, string batchId, int index, ExecutionOptions options)
    {
        var metadata = step.Metadata;
        var record = new TransactionRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            BatchId = batchId,
            Timestamp = _time.GetUtcNow(),
            ActionId = metadata.Id,
            ActionName = metadata.Name,
            ParametersJson = step.Parameters.ToJson(),
            Source = plan.Source,
            SourceLabel = options.SourceLabel,
            Status = TransactionStatus.Pending,
            Risk = step.Risk.Level,
            PrivilegeUsed = step.Privilege,
            Restart = metadata.Restart,
            IsReversible = metadata.IsReversible,
            OriginalDisplay = step.Preview.CurrentValue,
            IntendedDisplay = step.Preview.NewValue,
        };

        // 1. Journal vor jeder Systemänderung.
        _journal.Insert(record);

        void Report(string phase, string? detail = null) => options.Progress?.Report(new ExecutionProgress(index, plan.Steps.Count, metadata.Name, phase, detail));
        var lineProgress = new Progress<string>(line => Report("Wird ausgeführt", line));

        try
        {
            // 2. Ursprungszustand sichern und dauerhaft ablegen.
            Report("Sicherung wird erstellt");
            // Die Schritte selbst werden nicht abgebrochen: Ein begonnener Schritt läuft immer bis zur Verifikation durch.
            var backup = await _runner.BackupAsync(metadata.Id, step.Parameters, CancellationToken.None).ConfigureAwait(false);
            record = record with { BackupJson = backup.PayloadJson, Status = TransactionStatus.BackedUp };
            _journal.Update(record);

            // 3. Anwenden.
            Report("Wird angewendet");
            var apply = await _runner.ApplyAsync(metadata.Id, step.Parameters, lineProgress, CancellationToken.None).ConfigureAwait(false);
            if (!apply.Success)
            {
                record = record with { Status = TransactionStatus.Failed, ErrorDetails = Combine(apply.Message, apply.ErrorDetails) };
                _journal.Update(record);
                record = await TryRevertPartialAsync(record).ConfigureAwait(false);
                _logger.LogWarning("Aktion {ActionId} fehlgeschlagen: {Message}", metadata.Id, apply.Message);
                return (new StepResult(step, record, false, apply.Message), false);
            }

            record = record with { Status = TransactionStatus.Applied, ActualDisplay = apply.Message };
            _journal.Update(record);

            // 4. Verifizieren – Erfolg wird nur bei bestätigtem Zielzustand gemeldet.
            Report("Wird verifiziert");
            var verification = await _runner.VerifyAsync(metadata.Id, step.Parameters, CancellationToken.None).ConfigureAwait(false);
            record = record with
            {
                Status = verification.Verified ? TransactionStatus.Verified : TransactionStatus.VerificationFailed,
                ActualDisplay = verification.Actual,
                VerificationMessage = verification.Message,
            };
            _journal.Update(record);

            if (!verification.Verified)
            {
                _logger.LogWarning("Verifikation von {ActionId} fehlgeschlagen: {Message}", metadata.Id, verification.Message);
                return (new StepResult(step, record, false, "Die Änderung wurde geschrieben, konnte aber nicht bestätigt werden. " + verification.Message), false);
            }

            _logger.LogInformation("Aktion {ActionId} angewendet und verifiziert.", metadata.Id);
            return (new StepResult(step, record, true, apply.Message), false);
        }
        catch (PrivilegeUnavailableException ex)
        {
            record = record with { Status = TransactionStatus.Failed, ErrorDetails = ex.Message };
            _journal.Update(record);
            return (new StepResult(step, record, false, ex.Message), true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler bei Aktion {ActionId}.", metadata.Id);
            var hadBackup = record.BackupJson is not null;
            record = record with { Status = TransactionStatus.Failed, ErrorDetails = ex.GetType().Name + ": " + ex.Message };
            _journal.Update(record);
            if (hadBackup) record = await TryRevertPartialAsync(record).ConfigureAwait(false);
            return (new StepResult(step, record, false, "Unerwarteter Fehler: " + ex.Message), false);
        }
    }

    /// <summary>Nach einem fehlgeschlagenen Anwenden wird – sofern umkehrbar – der gesicherte Ursprungszustand wiederhergestellt.</summary>
    private async Task<TransactionRecord> TryRevertPartialAsync(TransactionRecord record)
    {
        if (!record.IsReversible || record.BackupJson is null) return record;
        try
        {
            var backup = new BackupData(record.ActionId, record.BackupJson, record.OriginalDisplay, record.IntendedDisplay);
            var rollback = await _runner.RollbackAsync(record.ActionId, record.Parameters, backup, force: false, CancellationToken.None).ConfigureAwait(false);
            if (!rollback.Success && !rollback.ConflictDetected)
            {
                record = record with { ErrorDetails = Combine(record.ErrorDetails, "Automatische Wiederherstellung fehlgeschlagen: " + rollback.Message) };
                _journal.Update(record);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wiederherstellung nach Fehler bei {ActionId} nicht möglich.", record.ActionId);
            record = record with { ErrorDetails = Combine(record.ErrorDetails, "Automatische Wiederherstellung nicht möglich: " + ex.Message) };
            _journal.Update(record);
        }
        return record;
    }

    internal async Task<RollbackOutcome> RollbackTransactionCoreAsync(TransactionRecord record, bool force)
    {
        if (!record.IsReversible) return new RollbackOutcome(record, false, false, "Diese Änderung ist nicht umkehrbar.");
        if (record.Status == TransactionStatus.RolledBack) return new RollbackOutcome(record, true, false, "Bereits rückgängig gemacht.");
        if (record.BackupJson is null) return new RollbackOutcome(record, false, false, "Für diese Änderung liegt keine Sicherung vor.");

        if (!force)
        {
            var later = _journal.GetLaterActive(record);
            if (later.Count > 0)
            {
                return new RollbackOutcome(record, false, true,
                    $"Dieselbe Einstellung wurde später erneut geändert ({later[^1].Timestamp.ToLocalTime():g}). Machen Sie zuerst die neuere Änderung rückgängig oder bestätigen Sie die Wiederherstellung ausdrücklich.");
            }
        }

        try
        {
            var backup = new BackupData(record.ActionId, record.BackupJson, record.OriginalDisplay, record.IntendedDisplay);
            var result = await _runner.RollbackAsync(record.ActionId, record.Parameters, backup, force, CancellationToken.None).ConfigureAwait(false);
            if (result.ConflictDetected) return new RollbackOutcome(record, false, true, result.Message);

            record = record with
            {
                Status = result.Success ? TransactionStatus.RolledBack : TransactionStatus.RollbackFailed,
                RolledBackAt = result.Success ? _time.GetUtcNow() : null,
                ErrorDetails = result.Success ? record.ErrorDetails : Combine(record.ErrorDetails, Combine(result.Message, result.ErrorDetails)),
            };
            _journal.Update(record);
            return new RollbackOutcome(record, result.Success, false, result.Message);
        }
        catch (PrivilegeUnavailableException ex)
        {
            return new RollbackOutcome(record, false, false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback von {TransactionId} fehlgeschlagen.", record.Id);
            record = record with { Status = TransactionStatus.RollbackFailed, ErrorDetails = Combine(record.ErrorDetails, ex.Message) };
            _journal.Update(record);
            return new RollbackOutcome(record, false, false, "Unerwarteter Fehler beim Rückgängigmachen: " + ex.Message);
        }
    }

    internal bool TryEnterGate() => _gate.TryEnter();
    internal void ExitGate() => _gate.Exit();

    private static string Combine(string? first, string? second)
        => string.IsNullOrWhiteSpace(first) ? second ?? "" : string.IsNullOrWhiteSpace(second) ? first : first + " | " + second;
}

public sealed record RollbackOutcome(TransactionRecord Transaction, bool Success, bool Conflict, string Message);

/// <summary>Rückgängigmachen einzelner Änderungen, ganzer Läufe und Wiederaufnahme nach Unterbrechungen (Abschnitt 26.3).</summary>
public sealed class RollbackService
{
    private readonly OptimizationExecutor _executor;
    private readonly ITransactionJournal _journal;

    public RollbackService(OptimizationExecutor executor, ITransactionJournal journal)
    {
        _executor = executor;
        _journal = journal;
    }

    public event EventHandler<IReadOnlyList<RollbackOutcome>>? RollbackCompleted;

    public async Task<RollbackOutcome> RollbackAsync(string transactionId, bool force = false)
    {
        var record = _journal.Get(transactionId);
        if (record is null) return new RollbackOutcome(Missing(transactionId), false, false, "Der Journaleintrag wurde nicht gefunden.");
        var outcomes = await RunAsync(new[] { record }, force).ConfigureAwait(false);
        return outcomes[0];
    }

    public Task<IReadOnlyList<RollbackOutcome>> RollbackSelectedAsync(IEnumerable<string> transactionIds, bool force = false)
    {
        // Immer in umgekehrter zeitlicher Reihenfolge zurückrollen.
        var records = transactionIds.Select(_journal.Get).Where(r => r is not null).Select(r => r!).OrderByDescending(r => r.Timestamp).ToList();
        return RunAsync(records, force);
    }

    public Task<IReadOnlyList<RollbackOutcome>> RollbackBatchAsync(string batchId, bool force = false)
    {
        var records = _journal.GetBatch(batchId).Where(r => r.CanRollback && r.Status != TransactionStatus.RolledBack).Reverse().ToList();
        return RunAsync(records, force);
    }

    /// <summary>„Letzte Änderung rückgängig machen“: der jüngste noch aktive, umkehrbare Journaleintrag.</summary>
    public TransactionRecord? FindLastActive() => _journal.GetRecent(500).FirstOrDefault(r => r.IsActive && r.CanRollback);

    public async Task<RollbackOutcome?> RollbackLastAsync(bool force = false)
    {
        var last = FindLastActive();
        if (last is null) return null;
        var outcomes = await RunAsync(new[] { last }, force).ConfigureAwait(false);
        return outcomes[0];
    }

    /// <summary>
    /// Beim Programmstart aufrufen: Kennzeichnet Einträge, deren Ausführung unterbrochen wurde (Absturz, Stromausfall),
    /// damit sie im Verlauf untersucht und – soweit eine Sicherung existiert – zurückgerollt werden können.
    /// </summary>
    public IReadOnlyList<TransactionRecord> RecoverInterrupted()
    {
        var recovered = new List<TransactionRecord>();
        foreach (var record in _journal.GetIncomplete())
        {
            var updated = record.Status == TransactionStatus.Pending
                ? record with { Status = TransactionStatus.Failed, ErrorDetails = "Der Vorgang wurde vor der ersten Systemänderung unterbrochen. Es wurde nichts verändert." }
                : record with { Status = TransactionStatus.Interrupted, ErrorDetails = "Der Vorgang wurde unterbrochen. Der tatsächliche Zustand ist ungewiss; die Sicherung liegt vor und kann wiederhergestellt werden." };
            _journal.Update(updated);
            recovered.Add(updated);
        }
        return recovered;
    }

    private async Task<IReadOnlyList<RollbackOutcome>> RunAsync(IReadOnlyList<TransactionRecord> records, bool force)
    {
        if (records.Count == 0) return Array.Empty<RollbackOutcome>();
        if (!_executor.TryEnterGate())
            return records.Select(r => new RollbackOutcome(r, false, false, "Es läuft bereits ein Optimierungsvorgang. Bitte warten Sie, bis dieser abgeschlossen ist.")).ToList();

        var outcomes = new List<RollbackOutcome>(records.Count);
        try
        {
            foreach (var record in records)
                outcomes.Add(await _executor.RollbackTransactionCoreAsync(record, force).ConfigureAwait(false));
        }
        finally
        {
            _executor.ExitGate();
        }

        RollbackCompleted?.Invoke(this, outcomes);
        return outcomes;
    }

    private static TransactionRecord Missing(string id) => new()
    {
        Id = id, BatchId = "", Timestamp = DateTimeOffset.MinValue, ActionId = "", ActionName = "Unbekannt",
    };
}
