using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Analysis;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Notifications;
using NexusOptimizer.Core.Settings;
using NexusOptimizer.Core.SystemAccess;

namespace NexusOptimizer.App.Services;

public interface INavigationService
{
    void Navigate(string pageKey, object? parameter = null);
}

/// <summary>
/// Einheitlicher Ablauf für jede Systemänderung aus der Oberfläche: planen → Vorschau anzeigen → bestätigen lassen →
/// ausführen → tatsächliches Ergebnis melden. Keine Seite ruft die Ausführungs-Engine direkt auf.
/// </summary>
public sealed class ActionCoordinator
{
    private readonly OptimizationPlanner _planner;
    private readonly OptimizationExecutor _executor;
    private readonly RollbackService _rollback;
    private readonly SettingsService _settings;
    private readonly SystemSnapshotService _snapshots;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly NotificationCenter _notifications;
    private readonly IFeedbackStore _feedback;
    private readonly ILogger<ActionCoordinator> _logger;

    public ActionCoordinator(OptimizationPlanner planner, OptimizationExecutor executor, RollbackService rollback, SettingsService settings, SystemSnapshotService snapshots,
        DialogService dialogs, ToastService toasts, NotificationCenter notifications, IFeedbackStore feedback, ILogger<ActionCoordinator> logger)
    {
        _planner = planner;
        _executor = executor;
        _rollback = rollback;
        _settings = settings;
        _snapshots = snapshots;
        _dialogs = dialogs;
        _toasts = toasts;
        _notifications = notifications;
        _feedback = feedback;
        _logger = logger;
    }

    /// <summary>Wird nach jeder Ausführung oder Wiederherstellung ausgelöst, damit Seiten ihre Zustände neu lesen.</summary>
    public event EventHandler? SystemChanged;

    public async Task<OptimizationPlan> BuildPlanAsync(IEnumerable<PlanRequest> requests, ExecutionSource source, CancellationToken cancellationToken = default)
    {
        var context = await _snapshots.GetContextAsync(cancellationToken).ConfigureAwait(true);
        return await _planner.BuildAsync(requests, _settings.Current.ToRiskPolicy(), context, source, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Plant die Anforderungen, zeigt die Vorschau und führt nach Bestätigung aus. Liefert null, wenn nichts ausgeführt wurde.</summary>
    public async Task<BatchResult?> RunAsync(IEnumerable<PlanRequest> requests, ExecutionSource source, string title, string? sourceLabel = null, IEnumerable<string>? notes = null)
    {
        try
        {
            var plan = await BuildPlanAsync(requests, source).ConfigureAwait(true);
            return await RunPlanAsync(plan, title, sourceLabel, notes).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Planung fehlgeschlagen: {Title}", title);
            await _dialogs.ShowMessageAsync("Planung fehlgeschlagen", "Die Änderung konnte nicht vorbereitet werden. Es wurde nichts verändert.", ex.Message).ConfigureAwait(true);
            return null;
        }
    }

    public async Task<BatchResult?> RunPlanAsync(OptimizationPlan plan, string title, string? sourceLabel = null, IEnumerable<string>? notes = null)
    {
        if (plan.IsEmpty)
        {
            var reasons = plan.Rejected.Select(r => $"• {r.Name}: {r.Reason}").ToList();
            var allApplied = plan.Rejected.Count > 0 && plan.Rejected.All(r => r.Kind == RejectionKind.AlreadyApplied);
            await _dialogs.ShowMessageAsync(allApplied ? "Keine Änderung nötig" : "Nichts auszuführen",
                allApplied ? "Der Zielzustand liegt bereits vor – es wurde nichts verändert." : "Keine der angeforderten Änderungen kann auf diesem System ausgeführt werden. Es wurde nichts verändert.",
                reasons.Count > 0 ? string.Join(Environment.NewLine, reasons) : null).ConfigureAwait(true);
            return null;
        }

        var settings = _settings.Current;
        var preview = new PlanPreviewDialogViewModel(plan, notes, offerRestorePoint: plan.Steps.Any(s => s.Metadata.IsReversible || !s.Metadata.IsDiagnosticOnly)) { Title = title, CreateRestorePoint = settings.CreateRestorePointBeforeBatch };

        // Einzelne, sehr risikoarme und umkehrbare Benutzeränderungen dürfen – wenn so eingestellt – ohne Vorschaudialog laufen.
        var quick = !settings.ConfirmEveryChange && plan.Source == ExecutionSource.User && plan.Steps.Count == 1 && !plan.RequiresElevation
                    && plan.Steps[0] is { RequiresIndividualConfirmation: false } single && single.Risk.Level == RiskLevel.VeryLow && single.Metadata.IsReversible;
        if (!quick && !await _dialogs.ShowPlanAsync(preview).ConfigureAwait(true)) return null;

        var selected = quick ? plan.Steps.ToList() : preview.Selected.Select(s => s.Step).ToList();
        var subset = plan.Subset(selected.Select(s => s.Key));
        if (subset.IsEmpty) return null;
        // Die Auswahl im Vorschaudialog ist zugleich die Einzelbestätigung für kennzeichnungspflichtige Schritte.
        var confirmed = subset.Steps.Where(s => s.RequiresIndividualConfirmation).Select(s => s.Key).ToHashSet();

        using var cancellation = new CancellationTokenSource();
        var progress = new ProgressDialogViewModel(subset.Steps.Count > 1 ? cancellation : null) { Title = title, Status = "Wird vorbereitet …", Maximum = subset.Steps.Count, IsIndeterminate = subset.Steps.Count == 1 };

        BatchResult result;
        try
        {
            result = await _dialogs.RunWithProgressAsync(progress, async () =>
            {
                if (!quick && preview.CreateRestorePoint) await CreateRestorePointAsync(progress, title).ConfigureAwait(true);

                var reporter = new Progress<ExecutionProgress>(p =>
                {
                    progress.Value = p.StepIndex;
                    progress.Status = $"{p.Phase}: {p.ActionName}";
                    if (p.Detail is not null) progress.AddLine(p.Detail);
                });
                return await Task.Run(() => _executor.ExecuteAsync(subset, new ExecutionOptions { Progress = reporter, SourceLabel = sourceLabel ?? title, IndividuallyConfirmed = confirmed }, cancellation.Token)).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ausführung fehlgeschlagen: {Title}", title);
            await _dialogs.ShowMessageAsync("Ausführung fehlgeschlagen", "Bei der Ausführung ist ein unerwarteter Fehler aufgetreten. Der Verlauf zeigt, welche Schritte betroffen sind.", ex.Message).ConfigureAwait(true);
            SystemChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }

        await ReportAsync(result, title).ConfigureAwait(true);
        SystemChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private async Task CreateRestorePointAsync(ProgressDialogViewModel progress, string title)
    {
        progress.Status = "Wiederherstellungspunkt wird angefordert …";
        try
        {
            var plan = await BuildPlanAsync(new[] { new PlanRequest(RestorePointAction.ActionId, ActionParameters.Of(("description", "NEXUS Optimizer AI"))) }, ExecutionSource.User).ConfigureAwait(true);
            if (plan.IsEmpty)
            {
                progress.AddLine("Kein Wiederherstellungspunkt: " + (plan.Rejected.FirstOrDefault()?.Reason ?? "nicht verfügbar"));
                return;
            }
            var batch = await Task.Run(() => _executor.ExecuteAsync(plan, new ExecutionOptions { SourceLabel = title, IndividuallyConfirmed = plan.Steps.Select(s => s.Key).ToHashSet() })).ConfigureAwait(true);
            progress.AddLine(batch.AllSucceeded ? "Wiederherstellungspunkt erstellt." : "Kein Wiederherstellungspunkt: " + (batch.Steps.FirstOrDefault()?.Message ?? "unbekannter Grund"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Der Wiederherstellungspunkt ist eine Zusatzabsicherung; die eigene Sicherung im Journal bleibt maßgeblich.
            progress.AddLine("Kein Wiederherstellungspunkt: " + ex.Message);
        }
    }

    private async Task ReportAsync(BatchResult result, string title)
    {
        if (result.Busy)
        {
            await _dialogs.ShowMessageAsync("Vorgang läuft bereits", "Es läuft bereits eine Optimierung. Bitte warten Sie, bis sie abgeschlossen ist.").ConfigureAwait(true);
            return;
        }

        foreach (var step in result.Steps.Where(s => s.Success)) _feedback.Record(step.Step.Metadata.Id, FeedbackKind.Accepted);
        foreach (var step in result.Steps.Where(s => !s.Success && s.Transaction?.Status == TransactionStatus.VerificationFailed)) _feedback.Record(step.Step.Metadata.Id, FeedbackKind.VerificationFailed);

        var summary = $"{result.SucceededCount} von {result.Steps.Count} Änderung(en) angewendet und verifiziert.";
        if (result.Cancelled) summary += " Der Vorgang wurde abgebrochen; bereits angewendete Änderungen bleiben bestehen und stehen im Verlauf.";

        if (result.FailedCount == 0 && !result.Cancelled)
        {
            _notifications.Publish(NotificationKind.OptimizationCompleted, title, summary, NotificationSeverity.Success, "history");
            _toasts.Show(title, summary, NotificationSeverity.Success, "history");
        }
        else
        {
            _notifications.Publish(NotificationKind.OptimizationFailed, title, summary, result.SucceededCount > 0 ? NotificationSeverity.Warning : NotificationSeverity.Error, "history");
            var details = string.Join(Environment.NewLine, result.Steps.Select(s => $"{(s.Success ? "✓" : "✗")} {s.Step.Preview.Title}: {s.Message}"));
            await _dialogs.ShowMessageAsync(result.SucceededCount > 0 ? "Teilweise ausgeführt" : "Nicht ausgeführt", summary, details).ConfigureAwait(true);
        }

        if (result.Restart != RestartRequirement.None)
            _notifications.Publish(NotificationKind.RestartRequired, "Änderung wird später wirksam", result.Restart.ToGerman() + ".", NotificationSeverity.Info);
    }

    /// <summary>Macht eine einzelne Änderung nach Rückfrage rückgängig; bei Konflikten wird ausdrücklich nachgefragt.</summary>
    public async Task<bool> UndoAsync(TransactionRecord transaction, bool askFirst = true)
    {
        if (askFirst && !await _dialogs.ConfirmAsync("Änderung rückgängig machen",
                $"„{transaction.ActionName}“ wird auf den ursprünglichen Zustand zurückgesetzt:\n\n{transaction.IntendedDisplay}  →  {transaction.OriginalDisplay}", "Rückgängig machen").ConfigureAwait(true))
            return false;

        var outcome = await Task.Run(() => _rollback.RollbackAsync(transaction.Id)).ConfigureAwait(true);
        if (outcome.Conflict)
        {
            if (!await _dialogs.ConfirmAsync("Einstellung wurde zwischenzeitlich geändert", outcome.Message + "\n\nSoll der ursprüngliche Zustand trotzdem wiederhergestellt werden?", "Trotzdem wiederherstellen", danger: true).ConfigureAwait(true))
                return false;
            outcome = await Task.Run(() => _rollback.RollbackAsync(transaction.Id, force: true)).ConfigureAwait(true);
        }

        await ReportRollbackAsync(new[] { outcome }).ConfigureAwait(true);
        return outcome.Success;
    }

    public async Task<bool> UndoLastAsync()
    {
        var last = _rollback.FindLastActive();
        if (last is null)
        {
            await _dialogs.ShowMessageAsync("Nichts rückgängig zu machen", "Im Verlauf gibt es keine aktive, umkehrbare Änderung.").ConfigureAwait(true);
            return false;
        }
        return await UndoAsync(last).ConfigureAwait(true);
    }

    public async Task<bool> UndoBatchAsync(string batchId, string description)
    {
        if (!await _dialogs.ConfirmAsync("Optimierungslauf rückgängig machen", $"Alle umkehrbaren Änderungen von „{description}“ werden in umgekehrter Reihenfolge zurückgenommen.", "Rückgängig machen").ConfigureAwait(true))
            return false;
        var outcomes = await Task.Run(() => _rollback.RollbackBatchAsync(batchId)).ConfigureAwait(true);
        await ReportRollbackAsync(outcomes).ConfigureAwait(true);
        return outcomes.Count > 0 && outcomes.All(o => o.Success);
    }

    public async Task<bool> UndoSelectedAsync(IReadOnlyList<TransactionRecord> transactions)
    {
        if (transactions.Count == 0) return false;
        if (!await _dialogs.ConfirmAsync("Ausgewählte Änderungen wiederherstellen", $"{transactions.Count} Änderung(en) werden – beginnend mit der jüngsten – auf den ursprünglichen Zustand zurückgesetzt.", "Wiederherstellen").ConfigureAwait(true))
            return false;
        var outcomes = await Task.Run(() => _rollback.RollbackSelectedAsync(transactions.Select(t => t.Id))).ConfigureAwait(true);
        await ReportRollbackAsync(outcomes).ConfigureAwait(true);
        return outcomes.All(o => o.Success);
    }

    private async Task ReportRollbackAsync(IReadOnlyList<RollbackOutcome> outcomes)
    {
        SystemChanged?.Invoke(this, EventArgs.Empty);
        if (outcomes.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Nichts wiederherzustellen", "Es wurden keine aktiven, umkehrbaren Änderungen gefunden.").ConfigureAwait(true);
            return;
        }
        var failed = outcomes.Where(o => !o.Success).ToList();
        if (failed.Count == 0)
        {
            var text = outcomes.Count == 1 ? $"„{outcomes[0].Transaction.ActionName}“: {outcomes[0].Message}" : $"{outcomes.Count} Änderungen wurden zurückgesetzt.";
            _notifications.Publish(NotificationKind.RollbackCompleted, "Rückgängig gemacht", text, NotificationSeverity.Success, "history");
            _toasts.Show("Rückgängig gemacht", text, NotificationSeverity.Success, "history");
        }
        else
        {
            var details = string.Join(Environment.NewLine, outcomes.Select(o => $"{(o.Success ? "✓" : "✗")} {o.Transaction.ActionName}: {o.Message}"));
            _notifications.Publish(NotificationKind.OptimizationFailed, "Wiederherstellung unvollständig", $"{failed.Count} Änderung(en) konnten nicht zurückgesetzt werden.", NotificationSeverity.Error, "history");
            await _dialogs.ShowMessageAsync("Wiederherstellung unvollständig", $"{outcomes.Count - failed.Count} zurückgesetzt, {failed.Count} nicht.", details).ConfigureAwait(true);
        }
    }

    public void RecordDeclined(string actionId) => _feedback.Record(actionId, FeedbackKind.Declined);
}

/// <summary>Wendet Design, Akzentfarbe und Schriftskalierung zur Laufzeit an.</summary>
public sealed class ThemeService
{
    private const string PalettePrefix = "Themes/Palette.";
    public event EventHandler? Changed;
    public double FontScale { get; private set; } = 1.0;

    public void Apply(AppSettings settings)
    {
        var application = Application.Current;
        if (application is null) return;

        var theme = settings.Theme == "Light" ? "Light" : "Dark";
        var dictionaries = application.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NexusOptimizer.App;component/{PalettePrefix}{theme}.xaml", UriKind.Absolute) };
        var existing = dictionaries.FirstOrDefault(d => d.Source is not null && d.Source.OriginalString.Contains(PalettePrefix, StringComparison.OrdinalIgnoreCase));
        if (existing is null) dictionaries.Insert(0, palette);
        else dictionaries[dictionaries.IndexOf(existing)] = palette;

        if (ColorConverter.ConvertFromString(settings.AccentColor) is Color accent)
        {
            var dark = theme == "Dark";
            SetBrush(application, "Accent", accent);
            SetBrush(application, "AccentHover", dark ? Blend(accent, Colors.White, 0.18) : Blend(accent, Colors.Black, 0.14));
            SetBrush(application, "AccentPressed", Blend(accent, Colors.Black, dark ? 0.16 : 0.28));
            SetBrush(application, "AccentSoft", dark ? Blend(accent, Color.FromRgb(0x18, 0x1A, 0x23), 0.74) : Blend(accent, Colors.White, 0.84));
        }

        FontScale = settings.FontScale;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void SetBrush(Application application, string name, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        application.Resources["Brush." + name] = brush;
        application.Resources["Color." + name] = color;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount), (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount));

    public static IReadOnlyList<(string Name, string Hex)> AccentChoices { get; } = new[]
    {
        ("Violett", "#7C5CFF"), ("Elektroblau", "#3B82F6"), ("Cyan", "#06B6D4"), ("Grün", "#10B981"), ("Bernstein", "#F59E0B"), ("Rosa", "#EC4899"),
    };
}
