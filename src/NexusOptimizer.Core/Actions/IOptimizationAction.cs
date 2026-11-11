using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Security;

namespace NexusOptimizer.Core.Actions;

/// <summary>
/// Vertrag jeder Optimierungsaktion. Es existiert bewusst keine Methode, die beliebige Befehle
/// oder Zeichenketten ausführt – jede Aktion ist fest implementiert und stark typisiert.
/// </summary>
public interface IOptimizationAction
{
    /// <summary>GetMetadata()</summary>
    ActionMetadata Metadata { get; }

    /// <summary>IsSupported() / AnalyzeCompatibility()</summary>
    Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>ValidateParameters()</summary>
    ValidationResult ValidateParameters(ActionParameters parameters);

    /// <summary>Tatsächlich benötigte Berechtigung; kann von den Parametern abhängen (z. B. HKCU vs. HKLM).</summary>
    PrivilegeLevel GetRequiredPrivilege(ActionParameters parameters);

    /// <summary>EvaluateRisk()</summary>
    RiskAssessment EvaluateRisk(ActionParameters parameters, SystemContext context);

    /// <summary>DetectCurrentState()</summary>
    Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>PreviewChanges()</summary>
    Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>CreateBackup()</summary>
    Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>Apply()</summary>
    Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Verify()</summary>
    Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rollback(). Ohne <paramref name="force"/> wird nichts verändert, wenn der aktuelle Zustand nicht mehr dem
    /// von dieser Aktion gesetzten Zustand entspricht (Konflikt mit späteren Änderungen).
    /// </summary>
    Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default);
}

/// <summary>Basisklasse mit sinnvollen Standardimplementierungen.</summary>
public abstract class OptimizationActionBase : IOptimizationAction
{
    public abstract ActionMetadata Metadata { get; }

    public virtual Task<SupportResult> IsSupportedAsync(SystemContext context, ActionParameters parameters, CancellationToken cancellationToken = default)
    {
        if (!Metadata.SupportedVersions.Includes(context.WindowsBuild))
        {
            return Task.FromResult(SupportResult.NotSupported(
                $"Nicht für diese Windows-Version vorgesehen (unterstützt: {Metadata.SupportedVersions.Describe()}, erkannt: Build {context.WindowsBuild})."));
        }
        return Task.FromResult(SupportResult.Supported);
    }

    public virtual ValidationResult ValidateParameters(ActionParameters parameters)
        => ParameterValidator.Validate(Metadata.Parameters, parameters);

    public virtual PrivilegeLevel GetRequiredPrivilege(ActionParameters parameters) => Metadata.RequiredPrivilege;

    public virtual RiskAssessment EvaluateRisk(ActionParameters parameters, SystemContext context) => RiskAssessment.From(Metadata);

    public abstract Task<ActionState> DetectCurrentStateAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    public abstract Task<ChangePreview> PreviewAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    public abstract Task<BackupData> CreateBackupAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    public abstract Task<OperationResult> ApplyAsync(ActionParameters parameters, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    public abstract Task<VerificationResult> VerifyAsync(ActionParameters parameters, CancellationToken cancellationToken = default);

    public abstract Task<RollbackResult> RollbackAsync(ActionParameters parameters, BackupData backup, bool force, CancellationToken cancellationToken = default);

    protected void EnsureValid(ActionParameters parameters)
    {
        var validation = ValidateParameters(parameters);
        if (!validation.IsValid) throw new ArgumentException(validation.ErrorText);
    }

    protected static RollbackResult NotReversible() => RollbackResult.Fail("Diese Aktion kann nicht automatisch rückgängig gemacht werden.");
}

/// <summary>Allowlist aller implementierten Aktionen. Nur hier registrierte Aktionen können überhaupt ausgeführt werden.</summary>
public sealed class ActionCatalog
{
    private readonly Dictionary<string, IOptimizationAction> _actions = new(StringComparer.Ordinal);

    public ActionCatalog(IEnumerable<IOptimizationAction> actions)
    {
        foreach (var action in actions) Register(action);
    }

    public void Register(IOptimizationAction action)
    {
        var id = action.Metadata.Id;
        if (!IsValidActionId(id)) throw new ArgumentException($"Ungültige Aktions-ID '{id}'.");
        if (!_actions.TryAdd(id, action)) throw new ArgumentException($"Aktions-ID '{id}' ist doppelt vergeben.");
    }

    public IReadOnlyCollection<IOptimizationAction> All => _actions.Values;
    public IOptimizationAction? Find(string id) => id is not null && _actions.TryGetValue(id, out var action) ? action : null;
    public IOptimizationAction Get(string id) => Find(id) ?? throw new KeyNotFoundException($"Unbekannte Aktion '{id}'.");
    public bool Contains(string id) => _actions.ContainsKey(id);
    public IEnumerable<IOptimizationAction> ByCategory(ActionCategory category) => _actions.Values.Where(a => a.Metadata.Category == category);
    public IEnumerable<IOptimizationAction> WithTag(string tag) => _actions.Values.Where(a => a.Metadata.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

    public static bool IsValidActionId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 96 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');
}
