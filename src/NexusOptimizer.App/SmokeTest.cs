using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using NexusOptimizer.App.Services;
using NexusOptimizer.App.ViewModels;
using NexusOptimizer.App.Views;
using NexusOptimizer.Core.Actions;
using NexusOptimizer.Core.Engine;
using NexusOptimizer.Core.Models;
using NexusOptimizer.Core.Settings;

namespace NexusOptimizer.App;

/// <summary>
/// Selbsttest der Oberfläche (Startparameter --smoke-test &lt;Ordner&gt;): lädt jede Seite, speichert ein Bild davon und protokolliert
/// Ladefehler, Bindungsfehler und unbehandelte Ausnahmen. Der Test verändert nichts am System – er öffnet nur Vorschauen und bricht sie ab.
/// </summary>
internal static class SmokeTest
{
    private static readonly List<string> Unhandled = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record PageResult(string Key, string Title, bool Ok, string? Error, IReadOnlyList<string> BindingErrors, long Milliseconds, string? Screenshot);

    private sealed class BindingListener : TraceListener
    {
        private readonly StringBuilder _current = new();
        private readonly List<string> _messages = new();

        public override void Write(string? message) => _current.Append(message);

        public override void WriteLine(string? message)
        {
            _current.Append(message);
            var text = _current.ToString().Trim();
            _current.Clear();
            if (text.Length > 0) lock (_messages) _messages.Add(text.Length > 600 ? text[..600] : text);
        }

        public IReadOnlyList<string> Take()
        {
            lock (_messages)
            {
                var copy = _messages.Distinct().ToList();
                _messages.Clear();
                return copy;
            }
        }
    }

    public static void RecordUnhandled(Exception exception)
    {
        lock (Unhandled) Unhandled.Add(exception.ToString());
    }

    public static void WriteFailure(string directory, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "smoke.json"), JsonSerializer.Serialize(new { ok = false, startupError = exception.ToString() }, Json));
        }
        catch (Exception)
        {
            // Nichts weiter möglich.
        }
    }

    public static async Task<int> RunAsync(MainWindow window, ShellViewModel shell, IServiceProvider services, string directory)
    {
        Directory.CreateDirectory(directory);
        var listener = new BindingListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.ResourceDictionarySource.Listeners.Add(listener);
        PresentationTraceSources.ResourceDictionarySource.Switch.Level = SourceLevels.Warning;

        var results = new List<PageResult>();
        var extras = new List<string>();
        await SettleAsync(1500).ConfigureAwait(true);
        listener.Take();

        var index = 0;
        foreach (var item in PageRegistry.All)
        {
            index++;
            var watch = Stopwatch.StartNew();
            string? error = null;
            string? screenshot = null;
            try
            {
                await shell.NavigateAsync(item.Key).ConfigureAwait(true);
                await SettleAsync(900).ConfigureAwait(true);
                error = shell.CurrentPage?.ErrorMessage;
                if (shell.CurrentPage?.Key != item.Key) error = "Die Seite wurde nicht angezeigt.";
                screenshot = $"{index:00}-{item.Key}.png";
                Save(window, Path.Combine(directory, screenshot));
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }
            results.Add(new PageResult(item.Key, item.Title, error is null, error, listener.Take(), watch.ElapsedMilliseconds, screenshot));
        }

        // Dialogebene: Vorschau einer harmlosen, umkehrbaren Einstellung öffnen, abbilden und abbrechen – es wird nichts ausgeführt.
        try
        {
            var ui = services.GetRequiredService<UiState>();
            var coordinator = services.GetRequiredService<ActionCoordinator>();
            var catalog = services.GetRequiredService<ActionCatalog>();
            var candidates = catalog.All.Where(a => a.Metadata is { RequiredPrivilege: PrivilegeLevel.User, IsReversible: true, Parameters.Count: 0, IsExperimental: false }).Take(40).Select(a => new PlanRequest(a.Metadata.Id)).ToList();
            var pending = coordinator.RunAsync(candidates, ExecutionSource.User, "Vorschau (Selbsttest)");
            for (var i = 0; i < 40 && ui.ActiveDialog is null; i++) await SettleAsync(150).ConfigureAwait(true);
            await SettleAsync(400).ConfigureAwait(true);
            Save(window, Path.Combine(directory, "90-dialog-vorschau.png"));
            extras.Add("Dialog: " + (ui.ActiveDialog?.GetType().Name ?? "keiner"));
            switch (ui.ActiveDialog)
            {
                case PlanPreviewDialogViewModel preview: preview.CancelCommand.Execute(null); break;
                case MessageDialogViewModel message: message.CloseCommand.Execute(null); break;
            }
            await pending.ConfigureAwait(true);
            await SettleAsync(200).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            extras.Add("Dialogtest fehlgeschlagen: " + ex.Message);
        }

        // Helles Design.
        try
        {
            var settings = services.GetRequiredService<SettingsService>();
            var theme = services.GetRequiredService<ThemeService>();
            theme.Apply(settings.Current with { Theme = "Light" });
            await shell.NavigateAsync("dashboard").ConfigureAwait(true);
            await SettleAsync(700).ConfigureAwait(true);
            Save(window, Path.Combine(directory, "91-dashboard-hell.png"));
            theme.Apply(settings.Current);
        }
        catch (Exception ex)
        {
            extras.Add("Designwechsel fehlgeschlagen: " + ex.Message);
        }

        var extraBindingErrors = listener.Take();
        List<string> unhandled;
        lock (Unhandled) unhandled = Unhandled.ToList();
        var ok = results.All(r => r.Ok) && unhandled.Count == 0;
        var report = new
        {
            ok,
            pages = results.Count,
            failed = results.Count(r => !r.Ok),
            bindingErrors = results.Sum(r => r.BindingErrors.Count) + extraBindingErrors.Count,
            unhandled,
            extras,
            extraBindingErrors,
            results,
        };
        File.WriteAllText(Path.Combine(directory, "smoke.json"), JsonSerializer.Serialize(report, Json));
        return ok ? 0 : 1;
    }

    private static async Task SettleAsync(int milliseconds)
    {
        await Task.Delay(milliseconds).ConfigureAwait(true);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
