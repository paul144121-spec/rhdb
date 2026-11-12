using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Notifications;

namespace NexusOptimizer.App.Services;

/// <summary>Hilfen für den Wechsel auf den Oberflächenthread.</summary>
public static class Ui
{
    public static Dispatcher Dispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public static void Post(Action action)
    {
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public static Task InvokeAsync(Action action) => Dispatcher.CheckAccess() ? Run(action) : Dispatcher.InvokeAsync(action).Task;

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

public abstract partial class DialogViewModel : ObservableObject
{
    public string Title { get; init; } = "";
}

public sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<bool> _result = new();
    public string Message { get; init; } = "";
    public string? Details { get; init; }
    public string ConfirmText { get; init; } = "Bestätigen";
    public string CancelText { get; init; } = "Abbrechen";
    public bool IsDanger { get; init; }
    /// <summary>Wenn gesetzt, muss dieses Kontrollkästchen vor der Bestätigung aktiviert werden.</summary>
    public string? Acknowledgement { get; init; }
    public bool HasAcknowledgement => Acknowledgement is not null;
    public Task<bool> Result => _result.Task;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isAcknowledged;

    private bool CanConfirm() => Acknowledgement is null || IsAcknowledged;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => _result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(false);
}

public sealed partial class MessageDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<bool> _result = new();
    public string Message { get; init; } = "";
    public string? Details { get; init; }
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public Task Result => _result.Task;

    [RelayCommand]
    private void Close() => _result.TrySetResult(true);
}

public sealed partial class PromptDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<string?> _result = new();
    public string Message { get; init; } = "";
    public string ConfirmText { get; init; } = "Übernehmen";
    public Task<string?> Result => _result.Task;

    [ObservableProperty] private string _text = "";

    [RelayCommand]
    private void Confirm() => _result.TrySetResult(Text);

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(null);
}

public sealed partial class ProgressDialogViewModel : DialogViewModel
{
    private readonly CancellationTokenSource? _cancellation;

    public ProgressDialogViewModel(CancellationTokenSource? cancellation = null) => _cancellation = cancellation;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _value;
    [ObservableProperty] private double _maximum = 1;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _cancelRequested;
    public bool CanCancel => _cancellation is not null;
    public ObservableCollection<string> Lines { get; } = new();

    public void AddLine(string line)
    {
        if (Lines.Count >= 200) Lines.RemoveAt(0);
        Lines.Add(line);
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRequested = true;
        Status = "Wird nach dem aktuellen Schritt abgebrochen …";
        _cancellation?.Cancel();
    }
}

public sealed partial class PlanStepViewModel : ObservableObject
{
    public PlanStepViewModel(PlanStep step)
    {
        Step = step;
        _isSelected = !step.RequiresIndividualConfirmation;
    }

    public PlanStep Step { get; }
    public string Title => Step.Preview.Title;
    public string Description => Step.Metadata.Description;
    public string CurrentValue => Step.Preview.CurrentValue;
    public string NewValue => Step.Preview.NewValue;
    public RiskLevel Risk => Step.Risk.Level;
    public string RiskText => Step.Risk.Level.ToGerman();
    public string ExpectedEffect => Step.Metadata.ExpectedEffect;
    public string SideEffects => string.Join(" ", Step.Risk.Reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct());
    public bool HasSideEffects => SideEffects.Length > 0;
    public string? Note => Step.Preview.Note;
    public bool RequiresAdmin => Step.Privilege == PrivilegeLevel.Administrator;
    public bool IsReversible => Step.Metadata.IsReversible;
    public bool IsDiagnostic => Step.Metadata.IsDiagnosticOnly;
    public string ReversibleText => Step.Metadata.IsReversible ? "Wiederherstellbar: Ja" : Step.Metadata.IsDiagnosticOnly ? "Keine bleibende Änderung" : "Nicht umkehrbar";
    public string? RestartText => Step.Metadata.Restart == RestartRequirement.None ? null : Step.Metadata.Restart.ToGerman();
    public string Details => string.Join(Environment.NewLine, Step.Preview.TechnicalDetails.Concat(new[]
    {
        "Sicherung: " + Step.Metadata.BackupStrategy,
        "Wiederherstellung: " + Step.Metadata.RollbackStrategy,
        "Verifikation: " + Step.Metadata.VerificationStrategy,
    }));
    public bool RequiresIndividualConfirmation => Step.RequiresIndividualConfirmation;

    [ObservableProperty] private bool _isSelected;
}

public sealed partial class PlanPreviewDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<bool> _result = new();

    public PlanPreviewDialogViewModel(OptimizationPlan plan, IEnumerable<string>? notes = null, bool offerRestorePoint = false)
    {
        Plan = plan;
        foreach (var step in plan.Steps)
        {
            var item = new PlanStepViewModel(step);
            item.PropertyChanged += (_, _) => Refresh();
            Steps.Add(item);
        }
        foreach (var rejected in plan.Rejected.Where(r => r.Kind != RejectionKind.AlreadyApplied)) Rejected.Add($"{rejected.Name}: {rejected.Reason}");
        AlreadyAppliedCount = plan.Rejected.Count(r => r.Kind == RejectionKind.AlreadyApplied);
        foreach (var note in notes ?? Array.Empty<string>()) Notes.Add(note);
        OfferRestorePoint = offerRestorePoint;
        Refresh();
    }

    public OptimizationPlan Plan { get; }
    public ObservableCollection<PlanStepViewModel> Steps { get; } = new();
    public ObservableCollection<string> Rejected { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public int AlreadyAppliedCount { get; }
    public bool HasAlreadyApplied => AlreadyAppliedCount > 0;
    public string AlreadyAppliedText => $"{AlreadyAppliedCount} weitere Einstellung(en) liegen bereits im Zielzustand vor und werden nicht angefasst.";
    public bool HasRejected => Rejected.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool OfferRestorePoint { get; }
    public string ConfirmText { get; init; } = "Ausgewählte Änderungen anwenden";
    public Task<bool> Result => _result.Task;

    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _requiresElevation;
    [ObservableProperty] private bool _hasIrreversible;
    [ObservableProperty] private string? _restartText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _selectedCount;

    public IReadOnlyList<PlanStepViewModel> Selected => Steps.Where(s => s.IsSelected).ToList();

    private void Refresh()
    {
        var selected = Selected;
        SelectedCount = selected.Count;
        RequiresElevation = selected.Any(s => s.RequiresAdmin) || CreateRestorePoint;
        HasIrreversible = selected.Any(s => !s.IsReversible && !s.IsDiagnostic);
        var restart = selected.Select(s => s.Step.Metadata.Restart).DefaultIfEmpty(RestartRequirement.None).Max();
        RestartText = restart == RestartRequirement.None ? null : restart.ToGerman();
        Summary = selected.Count == 0
            ? "Keine Änderung ausgewählt."
            : $"{selected.Count} von {Steps.Count} Änderung(en) ausgewählt · höchstes Risiko: {selected.Max(s => s.Risk).ToGerman()}";
    }

    partial void OnCreateRestorePointChanged(bool value) => Refresh();

    private bool CanConfirm() => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => _result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(false);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var step in Steps.Where(s => !s.RequiresIndividualConfirmation)) step.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var step in Steps) step.IsSelected = false;
    }
}

public sealed partial class ToastViewModel : ObservableObject
{
    public required string Title { get; init; }
    public string Message { get; init; } = "";
    public NotificationSeverity Severity { get; init; }
    public string? PageKey { get; init; }
}

/// <summary>Zustand der Oberfläche, der dienstübergreifend geteilt wird: aktiver Dialog und eingeblendete Hinweise.</summary>
public sealed partial class UiState : ObservableObject
{
    [ObservableProperty] private DialogViewModel? _activeDialog;
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();
}

public sealed class ToastService
{
    private readonly UiState _state;

    public ToastService(UiState state) => _state = state;

    public void Show(string title, string message = "", NotificationSeverity severity = NotificationSeverity.Info, string? pageKey = null)
    {
        Ui.Post(() =>
        {
            var toast = new ToastViewModel { Title = title, Message = message, Severity = severity, PageKey = pageKey };
            if (_state.Toasts.Count >= 4) _state.Toasts.RemoveAt(0);
            _state.Toasts.Add(toast);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(severity is NotificationSeverity.Error or NotificationSeverity.Warning ? 9 : 5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _state.Toasts.Remove(toast);
            };
            timer.Start();
        });
    }

    public void Dismiss(ToastViewModel toast) => Ui.Post(() => _state.Toasts.Remove(toast));
}

/// <summary>Dialoge werden als Überlagerung im Hauptfenster angezeigt; mehrere Anforderungen werden nacheinander abgearbeitet.</summary>
public sealed class DialogService
{
    private readonly UiState _state;
    private readonly SemaphoreSlim _queue = new(1, 1);

    public DialogService(UiState state) => _state = state;

    private async Task<T> ShowAsync<T>(DialogViewModel dialog, Func<Task<T>> result)
    {
        await _queue.WaitAsync().ConfigureAwait(true);
        try
        {
            await Ui.InvokeAsync(() => _state.ActiveDialog = dialog).ConfigureAwait(true);
            return await result().ConfigureAwait(true);
        }
        finally
        {
            await Ui.InvokeAsync(() => { if (ReferenceEquals(_state.ActiveDialog, dialog)) _state.ActiveDialog = null; }).ConfigureAwait(true);
            _queue.Release();
        }
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Bestätigen", bool danger = false, string? acknowledgement = null, string? details = null)
    {
        var dialog = new ConfirmDialogViewModel { Title = title, Message = message, ConfirmText = confirmText, IsDanger = danger, Acknowledgement = acknowledgement, Details = details };
        return ShowAsync(dialog, () => dialog.Result);
    }

    public Task ShowMessageAsync(string title, string message, string? details = null)
    {
        var dialog = new MessageDialogViewModel { Title = title, Message = message, Details = details };
        return ShowAsync(dialog, async () => { await dialog.Result.ConfigureAwait(true); return true; });
    }

    public Task<string?> PromptAsync(string title, string message, string initial = "", string confirmText = "Übernehmen")
    {
        var dialog = new PromptDialogViewModel { Title = title, Message = message, Text = initial, ConfirmText = confirmText };
        return ShowAsync(dialog, () => dialog.Result);
    }

    public Task<bool> ShowPlanAsync(PlanPreviewDialogViewModel dialog) => ShowAsync(dialog, () => dialog.Result);

    /// <summary>Zeigt einen Fortschrittsdialog, solange <paramref name="work"/> läuft.</summary>
    public Task<T> RunWithProgressAsync<T>(ProgressDialogViewModel dialog, Func<Task<T>> work) => ShowAsync(dialog, work);

    public string? OpenFile(string filter, string title = "Datei öffnen")
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string filter, string defaultName, string title = "Speichern unter")
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultName, Title = title, OverwritePrompt = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title = "Ordner auswählen")
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }
}
