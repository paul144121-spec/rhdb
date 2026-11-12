using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NexusOptimizer.App.Services;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Diagnostics;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Monitoring;

namespace NexusOptimizer.App.ViewModels;

/// <summary>Basisklasse aller Seiten. Seiten laden ihre Daten asynchron beim Betreten und halten nichts auf dem Oberflächenthread auf.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public virtual string Subtitle => "";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    public bool IsActive { get; private set; }

    public async Task ActivateAsync(object? parameter)
    {
        IsActive = true;
        try
        {
            ErrorMessage = null;
            await OnNavigatedToAsync(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = "Diese Seite konnte nicht vollständig geladen werden: " + ex.Message;
            Logger?.LogError(ex, "Fehler beim Laden der Seite {Page}.", Key);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Deactivate()
    {
        IsActive = false;
        OnNavigatedFrom();
    }

    protected virtual ILogger? Logger => null;
    protected virtual Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;
    protected virtual void OnNavigatedFrom() { }

    /// <summary>Führt eine Benutzeraktion aus und zeigt Fehler verständlich an, statt die Anwendung zu beenden.</summary>
    protected async Task GuardAsync(Func<Task> work, string failureText)
    {
        try
        {
            ErrorMessage = null;
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = $"{failureText} {ex.Message}";
            Logger?.LogError(ex, "{Failure}", failureText);
        }
    }
}

/// <summary>Seite mit Live-Messwerten: abonniert die Überwachung nur, solange die Seite sichtbar ist.</summary>
public abstract class LivePageViewModel : PageViewModel
{
    protected LivePageViewModel(MonitoringService monitoring) => Monitoring = monitoring;

    protected MonitoringService Monitoring { get; }

    protected override Task OnNavigatedToAsync(object? parameter)
    {
        Monitoring.MetricsUpdated -= OnMetricsUpdated;
        Monitoring.MetricsUpdated += OnMetricsUpdated;
        if (Monitoring.Latest is { } latest) OnMetrics(latest);
        return Task.CompletedTask;
    }

    protected override void OnNavigatedFrom() => Monitoring.MetricsUpdated -= OnMetricsUpdated;

    private void OnMetricsUpdated(object? sender, SystemMetrics metrics) => Ui.Post(() =>
    {
        if (!IsActive) return;
        try { OnMetrics(metrics); }
        catch (Exception ex) { Logger?.LogWarning(ex, "Live-Anzeige der Seite {Page} konnte nicht aktualisiert werden.", Key); }
    });

    /// <summary>Wird auf dem Oberflächenthread mit jedem neuen Messwert aufgerufen.</summary>
    protected abstract void OnMetrics(SystemMetrics metrics);

    /// <summary>Überträgt Messwert und Verlauf in eine Kachel. Fehlende Werte werden als „nicht verfügbar“ mit Begründung angezeigt – nie geschätzt.</summary>
    protected void Fill(MetricTileViewModel tile, MetricId id, SystemMetrics metrics, string detail = "", MetricId? secondary = null)
    {
        var definition = MetricCatalog.Get(id);
        var value = definition.Read(metrics);
        tile.History = Monitoring.GetLive(id);
        tile.SecondaryHistory = secondary is { } s ? Monitoring.GetLive(s) : null;
        if (value is null || double.IsNaN(value.Value))
        {
            tile.IsAvailable = false;
            tile.Value = "Nicht verfügbar";
            tile.UnavailableReason = Monitoring.UnavailableReasons.GetValueOrDefault(id) ?? "Windows stellt diesen Messwert auf diesem System nicht bereit.";
            tile.Detail = tile.UnavailableReason;
        }
        else
        {
            tile.IsAvailable = true;
            tile.Value = definition.Format(value);
            tile.UnavailableReason = null;
            tile.Detail = detail;
        }
    }
}

public sealed record InfoRow(string Label, string Value, string? Hint = null)
{
    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);
    public static InfoRow Of(string label, string? value, string? unavailableReason = null)
        => string.IsNullOrWhiteSpace(value) ? new InfoRow(label, "Nicht verfügbar", unavailableReason ?? "Windows stellt diese Angabe auf diesem System nicht bereit.") : new InfoRow(label, value);
    public static InfoRow Of(string label, bool? value, string yes = "Ja", string no = "Nein", string? unavailableReason = null)
        => value is null ? new InfoRow(label, "Nicht verfügbar", unavailableReason ?? "Windows stellt diese Angabe auf diesem System nicht bereit.") : new InfoRow(label, value.Value ? yes : no);
}

public sealed record InfoSection(string Title, IReadOnlyList<InfoRow> Rows);

/// <summary>Kachel mit einem Live-Messwert und Verlaufslinie.</summary>
public sealed partial class MetricTileViewModel : ObservableObject
{
    public MetricTileViewModel(string title, string accentKey, double maximum = 100)
    {
        Title = title;
        AccentKey = accentKey;
        Maximum = maximum;
    }

    public string Title { get; }
    /// <summary>Schlüssel der Farbressource (z. B. „Brush.Cyan“).</summary>
    public string AccentKey { get; }
    public double Maximum { get; }

    [ObservableProperty] private string _value = "–";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private IReadOnlyList<double>? _history;
    [ObservableProperty] private IReadOnlyList<double>? _secondaryHistory;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string? _unavailableReason;
}

public sealed partial class RecommendationViewModel : ObservableObject
{
    private readonly ActionCoordinator _coordinator;
    private readonly INavigationService _navigation;
    private readonly Action? _changed;

    public RecommendationViewModel(Recommendation recommendation, ActionCoordinator coordinator, INavigationService navigation, Action? changed = null)
    {
        Recommendation = recommendation;
        _coordinator = coordinator;
        _navigation = navigation;
        _changed = changed;
    }

    public Recommendation Recommendation { get; }
    public string Title => Recommendation.Title;
    public string Explanation => Recommendation.Explanation;
    public RecommendationSeverity Severity => Recommendation.Severity;
    public string SeverityText => Recommendation.Severity switch
    {
        RecommendationSeverity.Critical => "Kritisch",
        RecommendationSeverity.Warning => "Warnung",
        RecommendationSeverity.Suggestion => "Vorschlag",
        _ => "Hinweis",
    };
    public IReadOnlyList<string> Evidence => Recommendation.Evidence;
    public bool HasActions => Recommendation.Actions.Count > 0;
    public bool HasPage => Recommendation.PageKey is not null;
    public string ActionText => Recommendation.Actions.Count == 1 ? "Änderung prüfen und anwenden" : $"{Recommendation.Actions.Count} Änderungen prüfen und anwenden";

    [ObservableProperty] private bool _isDismissed;

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var result = await _coordinator.RunAsync(Recommendation.Actions, ExecutionSource.AiAssisted, Recommendation.Title, "Empfehlung").ConfigureAwait(true);
        if (result is not null) _changed?.Invoke();
    }

    [RelayCommand]
    private void Decline()
    {
        foreach (var action in Recommendation.Actions) _coordinator.RecordDeclined(action.ActionId);
        IsDismissed = true;
        _changed?.Invoke();
    }

    [RelayCommand]
    private void Open()
    {
        if (Recommendation.PageKey is { } key) _navigation.Navigate(key);
    }
}

/// <summary>Darstellung einer parameterlosen Aktion (z. B. eines Registrierungs-Tweaks) mit Ist-Zustand, Anwenden und Rückgängigmachen.</summary>
public sealed partial class TweakItemViewModel : ObservableObject
{
    private readonly IOptimizationAction _action;
    private readonly ActionCoordinator _coordinator;
    private readonly ITransactionJournal _journal;
    private TransactionRecord? _lastActive;

    public TweakItemViewModel(IOptimizationAction action, ActionCoordinator coordinator, ITransactionJournal journal)
    {
        _action = action;
        _coordinator = coordinator;
        _journal = journal;
    }

    private ActionMetadata Metadata => _action.Metadata;
    public string Id => Metadata.Id;
    public string Name => Metadata.Name;
    public string Description => Metadata.Description;
    public ActionCategory Category => Metadata.Category;
    public string CategoryText => Metadata.Category.ToGerman();
    public RiskLevel Risk => Metadata.Risk;
    public string RiskText => Metadata.Risk.ToGerman();
    public string ExpectedEffect => Metadata.ExpectedEffect;
    public string SideEffects => Metadata.SideEffects;
    public bool IsExperimental => Metadata.IsExperimental;
    public bool RequiresAdmin => Metadata.RequiredPrivilege == PrivilegeLevel.Administrator;
    public string? RestartText => Metadata.Restart == RestartRequirement.None ? null : Metadata.Restart.ToGerman();
    public string Details => string.Join(Environment.NewLine, Metadata.TechnicalDetails.Concat(new[]
    {
        "Tweak-ID: " + Metadata.Id,
        "Unterstützte Versionen: " + Metadata.SupportedVersions.Describe(),
        "Erforderliche Berechtigung: " + Metadata.RequiredPrivilege.ToGerman(),
        "Neustart: " + Metadata.Restart.ToGerman(),
        "Sicherung: " + Metadata.BackupStrategy,
        "Wiederherstellung: " + Metadata.RollbackStrategy,
        "Verifikation: " + Metadata.VerificationStrategy,
    }));
    public string SearchText => $"{Name} {Description} {CategoryText} {Id}".ToLowerInvariant();

    [ObservableProperty] private string _currentValue = "Wird gelesen …";
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isSupported = true;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _managedByPolicy;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private bool _isBusy;

    public async Task RefreshAsync(SystemContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var support = await _action.IsSupportedAsync(context, ActionParameters.Empty, cancellationToken).ConfigureAwait(true);
            IsSupported = support.IsSupported;
            if (!support.IsSupported)
            {
                CurrentValue = "Auf diesem System nicht verfügbar";
                Note = support.Reason;
                CanApply = CanUndo = false;
                return;
            }

            var state = await _action.DetectCurrentStateAsync(ActionParameters.Empty, cancellationToken).ConfigureAwait(true);
            CurrentValue = state.DisplayValue;
            IsApplied = state.IsApplied == true;
            ManagedByPolicy = state.ManagedByPolicy;
            Note = state.Note;
            _lastActive = await Task.Run(() => _journal.GetRecent(300).FirstOrDefault(t => t.ActionId == Id && t.IsActive && t.CanRollback), cancellationToken).ConfigureAwait(true);
            CanUndo = _lastActive is not null;
            CanApply = state.IsAvailable && !IsApplied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CurrentValue = "Nicht lesbar";
            Note = ex.Message;
            CanApply = CanUndo = false;
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        try { await _coordinator.RunAsync(new[] { new PlanRequest(Id) }, ExecutionSource.User, Name).ConfigureAwait(true); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_lastActive is null) return;
        IsBusy = true;
        try { await _coordinator.UndoAsync(_lastActive).ConfigureAwait(true); }
        finally { IsBusy = false; }
    }
}

public sealed record GuidedWorkflowViewModel(GuidedWorkflow Workflow)
{
    public string Title => Workflow.Title;
    public string Summary => Workflow.Summary;
    public IReadOnlyList<string> Steps => Workflow.Steps.Select((s, i) => $"{i + 1}. {s}").ToList();
    public string Warning => Workflow.Warning;
    public bool HasLink => Workflow.SettingsUri is not null;
}

/// <summary>Hilfen, die mehrere Seiten gemeinsam verwenden.</summary>
public static class PageHelpers
{
    /// <summary>Öffnet eine Windows-Einstellungsseite oder eine offizielle Herstellerseite. Zulässig sind nur fest hinterlegte Schemata.</summary>
    public static bool OpenUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("ms-settings" or "windowsdefender" or "https")) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static async Task RefreshTweaksAsync(IEnumerable<TweakItemViewModel> tweaks, SystemContext context, CancellationToken cancellationToken = default)
    {
        foreach (var tweak in tweaks.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await tweak.RefreshAsync(context, cancellationToken).ConfigureAwait(true);
        }
    }

    public static string Percent(double? value) => value is null || double.IsNaN(value.Value) ? "n. v." : value.Value.ToString("0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " %";
}
