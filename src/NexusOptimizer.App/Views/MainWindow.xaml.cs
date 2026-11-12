using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NexusOptimizer.App.Services;
using NexusOptimizer.App.ViewModels;
using NexusOptimizer.Core.Settings;

namespace NexusOptimizer.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly AppRuntime _runtime;
    private readonly SettingsService _settings;

    public MainWindow(ShellViewModel shell, AppRuntime runtime, SettingsService settings)
    {
        _shell = shell;
        _runtime = runtime;
        _settings = settings;
        DataContext = shell;
        InitializeComponent();

        StateChanged += (_, _) => OnWindowStateChanged();
        shell.PropertyChanged += OnShellPropertyChanged;
        shell.State.PropertyChanged += OnUiStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        OnWindowStateChanged();
    }

    private void OnWindowStateChanged()
    {
        // Bei maximiertem Fenster ragt der Rahmen über den Bildschirmrand hinaus; der Innenabstand gleicht das aus.
        RootBorder.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaximizeIcon.Data = (System.Windows.Media.Geometry)FindResource(WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize");

        var visible = WindowState != WindowState.Minimized;
        _runtime.SetWindowVisible(visible);
        // Minimiert: Seiten lösen ihre Live-Abonnements, damit im Hintergrund nichts unnötig gezeichnet oder abgefragt wird.
        if (visible) _shell.ReactivateCurrent();
        else _shell.DeactivateCurrent();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentPage)) return;
        if (!_settings.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation) return;
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void OnUiStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UiState.ActiveDialog) || _shell.State.ActiveDialog is null) return;
        // Tastaturfokus in den Dialog setzen, sobald er aufgebaut ist.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => DialogHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shell.State.ActiveDialog is not null) return;

        if (e.Key is Key.K or Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_shell.IsSearchOpen || _shell.SearchText.Length > 0)
            {
                _shell.SearchText = "";
                e.Handled = true;
            }
            else if (_shell.IsNotificationsOpen)
            {
                _shell.IsNotificationsOpen = false;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && _shell.IsSearchOpen && SearchList.Items.Count > 0)
        {
            SearchList.SelectedIndex = 0;
            (SearchList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocusWithin && _shell.SearchResults.Count > 0)
        {
            _shell.OpenSearchResultCommand.Execute(_shell.SearchResults[0]);
            e.Handled = true;
        }
    }

    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromEvent<SearchResult>(e.OriginalSource) is { } result) _shell.OpenSearchResultCommand.Execute(result);
    }

    private void OnSearchListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchList.SelectedItem is SearchResult result)
        {
            _shell.OpenSearchResultCommand.Execute(result);
            e.Handled = true;
        }
    }

    private void OnNotificationClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromEvent<NotificationViewModel>(e.OriginalSource) is { } notification) _shell.OpenNotificationCommand.Execute(notification);
    }

    private static T? ItemFromEvent<T>(object source) where T : class
    {
        var element = source as DependencyObject;
        while (element is not null and not ListBoxItem)
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return (element as ListBoxItem)?.DataContext as T;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
