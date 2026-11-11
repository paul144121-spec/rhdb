using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Models;

namespace NexusOptimizer.Core.Engine;

/// <summary>Führt die zustandsverändernden Schritte einer Aktion aus – lokal oder über die privilegierte Komponente.</summary>
public interface IActionRunner
{
    Task<BackupData> BackupAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default);
    Task<OperationResult> ApplyAsync(string actionId, ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    Task<VerificationResult> VerifyAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default);
    Task<RollbackResult> RollbackAsync(string actionId, ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default);
}

/// <summary>Die Administratorfreigabe (UAC) wurde verweigert oder die privilegierte Komponente ist nicht erreichbar.</summary>
public sealed class PrivilegeUnavailableException : Exception
{
    public PrivilegeUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Kanal zur getrennten, erhöht laufenden Komponente. Stellt ausschließlich die Operationen von <see cref="IActionRunner"/> bereit.</summary>
public interface IPrivilegedGateway : IActionRunner
{
    bool IsConnected { get; }
}

/// <summary>Führt Aktionen im aktuellen Prozess aus. Jede Anforderung wird erneut gegen Katalog und Parameterschema geprüft.</summary>
public sealed class LocalActionRunner : IActionRunner
{
    private readonly ActionCatalog _catalog;

    public LocalActionRunner(ActionCatalog catalog) => _catalog = catalog;

    private IOptimizationAction Resolve(string actionId, ActionParameters parameters)
    {
        var action = _catalog.Find(actionId) ?? throw new InvalidOperationException($"Aktion '{actionId}' ist nicht in der Allowlist enthalten.");
        var validation = action.ValidateParameters(parameters);
        if (!validation.IsValid) throw new ArgumentException($"Ungültige Parameter für '{actionId}': {validation.ErrorText}");
        return action;
    }

    public Task<BackupData> BackupAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default)
        => Resolve(actionId, parameters).CreateBackupAsync(parameters, cancellationToken);

    public Task<OperationResult> ApplyAsync(string actionId, ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Resolve(actionId, parameters).ApplyAsync(parameters, progress, cancellationToken);

    public Task<VerificationResult> VerifyAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default)
        => Resolve(actionId, parameters).VerifyAsync(parameters, cancellationToken);

    public Task<RollbackResult> RollbackAsync(string actionId, ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(backup.ActionId, actionId, StringComparison.Ordinal))
            return Task.FromResult(RollbackResult.Fail("Die Sicherung gehört zu einer anderen Aktion."));
        return Resolve(actionId, parameters).RollbackAsync(parameters, backup, force, cancellationToken);
    }
}

/// <summary>
/// Leitet Aktionen, die Administratorrechte benötigen, an die privilegierte Komponente weiter,
/// sofern der aktuelle Prozess nicht selbst erhöht läuft. Alles andere läuft lokal.
/// </summary>
public sealed class RoutingActionRunner : IActionRunner
{
    private readonly ActionCatalog _catalog;
    private readonly IActionRunner _local;
    private readonly IPrivilegedGateway? _gateway;
    private readonly Func<bool> _isElevated;

    public RoutingActionRunner(ActionCatalog catalog, IActionRunner local, IPrivilegedGateway? gateway, Func<bool> isElevated)
    {
        _catalog = catalog;
        _local = local;
        _gateway = gateway;
        _isElevated = isElevated;
    }

    public bool NeedsGateway(string actionId, ActionParameters parameters)
        => !_isElevated() && _catalog.Get(actionId).GetRequiredPrivilege(parameters) == PrivilegeLevel.Administrator;

    private IActionRunner Select(string actionId, ActionParameters parameters)
    {
        if (!NeedsGateway(actionId, parameters)) return _local;
        return _gateway ?? throw new PrivilegeUnavailableException("Für diese Aktion sind Administratorrechte erforderlich, die privilegierte Komponente ist jedoch nicht verfügbar.");
    }

    public Task<BackupData> BackupAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default)
        => Select(actionId, parameters).BackupAsync(actionId, parameters, cancellationToken);

    public Task<OperationResult> ApplyAsync(string actionId, ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Select(actionId, parameters).ApplyAsync(actionId, parameters, progress, cancellationToken);

    public Task<VerificationResult> VerifyAsync(string actionId, ActionParameters parameters, CancellationToken cancellationToken = default)
        => Select(actionId, parameters).VerifyAsync(actionId, parameters, cancellationToken);

    public Task<RollbackResult> RollbackAsync(string actionId, ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default)
        => Select(actionId, parameters).RollbackAsync(actionId, parameters, backup, force, cancellationToken);
}

/// <summary>Gemeinsame Sperre: Es läuft immer nur ein Optimierungs- oder Rollback-Vorgang gleichzeitig.</summary>
public sealed class ExecutionGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    public bool IsBusy => _semaphore.CurrentCount == 0;
    public bool TryEnter() => _semaphore.Wait(0);
    public void Exit() => _semaphore.Release();
}
