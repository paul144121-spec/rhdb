using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusOptimizer.Core.Monitoring;

namespace NexusOptimizer.App.Controls;

/// <summary>Liniensymbol aus einer Geometrie auf 24×24-Raster; übernimmt die Vordergrundfarbe der Umgebung.</summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon), new PropertyMetadata(null));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Icon), new PropertyMetadata(1.7));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
}

/// <summary>
/// Leichtgewichtiges Liniendiagramm für Live-Werte. Zeichnet direkt in OnRender (keine Elementbäume je Datenpunkt),
/// unterbricht die Linie bei fehlenden Werten (NaN) und skaliert wahlweise automatisch.
/// </summary>
public class LineChart : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object? defaultValue) => new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(LineChart), Render(null));
    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(nameof(SecondaryValues), typeof(IReadOnlyList<double>), typeof(LineChart), Render(null));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LineChart), Render(double.NaN));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(LineChart), Render(Brushes.MediumPurple));
    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(nameof(SecondaryStroke), typeof(Brush), typeof(LineChart), Render(Brushes.Teal));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(LineChart), Render(null));
    public static readonly DependencyProperty ShowFillProperty = DependencyProperty.Register(nameof(ShowFill), typeof(bool), typeof(LineChart), Render(true));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(LineChart), Render(1.6));
    /// <summary>Anzahl der Punkte, auf die die X-Achse ausgelegt ist (für gleichmäßiges Einlaufen von rechts). 0 = Anzahl der Werte.</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(LineChart), Render(0));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? SecondaryValues { get => (IReadOnlyList<double>?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush SecondaryStroke { get => (Brush)GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public bool ShowFill { get => (bool)GetValue(ShowFillProperty); set => SetValue(ShowFillProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 4 || height < 4) return;

        // Transparente Fläche, damit das Element Mausereignisse (Tooltip) erhält.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        if (GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            pen.Freeze();
            for (var i = 1; i < 4; i++)
            {
                var y = Math.Round(height * i / 4) + 0.5;
                dc.DrawLine(pen, new Point(0, y), new Point(width, y));
            }
        }

        var values = Values;
        var secondary = SecondaryValues;
        var max = Maximum;
        if (double.IsNaN(max) || max <= 0)
        {
            max = 0;
            if (values is not null) foreach (var v in values) if (!double.IsNaN(v) && v > max) max = v;
            if (secondary is not null) foreach (var v in secondary) if (!double.IsNaN(v) && v > max) max = v;
            max = max <= 0 ? 1 : max * 1.15;
        }

        if (secondary is { Count: > 1 }) DrawSeries(dc, secondary, max, width, height, SecondaryStroke, fill: false);
        if (values is { Count: > 1 }) DrawSeries(dc, values, max, width, height, Stroke, ShowFill);
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double> values, double max, double width, double height, Brush stroke, bool fill)
    {
        var capacity = Math.Max(Capacity, values.Count);
        var step = width / Math.Max(1, capacity - 1);
        var offset = capacity - values.Count;
        var pen = new Pen(stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        var line = new StreamGeometry();
        StreamGeometry? area = fill ? new StreamGeometry() : null;
        using (var lineContext = line.Open())
        using (var areaContext = area?.Open())
        {
            var open = false;
            double segmentStartX = 0, lastX = 0;
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (double.IsNaN(value))
                {
                    if (open && areaContext is not null)
                    {
                        areaContext.LineTo(new Point(lastX, height), false, false);
                        areaContext.LineTo(new Point(segmentStartX, height), false, false);
                    }
                    open = false;
                    continue;
                }
                var x = (offset + i) * step;
                var y = height - Math.Clamp(value / max, 0, 1) * (height - 2) - 1;
                var point = new Point(x, y);
                if (!open)
                {
                    lineContext.BeginFigure(point, false, false);
                    areaContext?.BeginFigure(point, true, true);
                    segmentStartX = x;
                    open = true;
                }
                else
                {
                    lineContext.LineTo(point, true, true);
                    areaContext?.LineTo(point, false, false);
                }
                lastX = x;
            }
            if (open && areaContext is not null)
            {
                areaContext.LineTo(new Point(lastX, height), false, false);
                areaContext.LineTo(new Point(segmentStartX, height), false, false);
            }
        }
        line.Freeze();

        if (area is not null)
        {
            area.Freeze();
            dc.PushOpacity(0.16);
            dc.DrawGeometry(stroke, null, area);
            dc.Pop();
        }
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>
/// Verlaufsdiagramm über eine Zeitachse mit Zoom (Mausrad) und Verschieben (Ziehen). Beschriftet Zeit- und Werteachse.
/// </summary>
public sealed class TimeChart : FrameworkElement
{
    private static FrameworkPropertyMetadata Render(object? defaultValue) => new(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<MetricPoint>), typeof(TimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TimeChart)d).ResetView()));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TimeChart), Render(Brushes.MediumPurple));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(TimeChart), Render(Brushes.Gray));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(TimeChart), Render(Brushes.Gray));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(MetricUnit), typeof(TimeChart), Render(MetricUnit.Percent));

    private DateTimeOffset _viewStart, _viewEnd;
    private Point? _dragStart;
    private DateTimeOffset _dragViewStart, _dragViewEnd;

    public IReadOnlyList<MetricPoint>? Points { get => (IReadOnlyList<MetricPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public MetricUnit Unit { get => (MetricUnit)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public TimeChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.SizeWE;
        ToolTip = "Mausrad: zoomen · Ziehen: verschieben · Doppelklick: gesamten Zeitraum anzeigen";
    }

    private void ResetView()
    {
        var points = Points;
        if (points is { Count: > 0 })
        {
            _viewStart = points[0].Timestamp;
            _viewEnd = points[^1].Timestamp;
            if (_viewEnd <= _viewStart) _viewEnd = _viewStart.AddMinutes(1);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var points = Points;
        if (points is not { Count: > 1 } || ActualWidth < 80) return;
        var span = (_viewEnd - _viewStart).TotalSeconds;
        var factor = e.Delta > 0 ? 0.8 : 1.25;
        var newSpan = Math.Clamp(span * factor, 30, Math.Max(60, (points[^1].Timestamp - points[0].Timestamp).TotalSeconds));
        var anchor = Math.Clamp((e.GetPosition(this).X - PlotLeft) / Math.Max(1, ActualWidth - PlotLeft), 0, 1);
        var anchorTime = _viewStart.AddSeconds(span * anchor);
        _viewStart = anchorTime.AddSeconds(-newSpan * anchor);
        _viewEnd = _viewStart.AddSeconds(newSpan);
        ClampView(points);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetView();
            InvalidateVisual();
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragViewStart = _viewStart;
        _dragViewEnd = _viewEnd;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is null || Points is not { Count: > 1 } points) return;
        var dx = e.GetPosition(this).X - _dragStart.Value.X;
        var secondsPerPixel = (_dragViewEnd - _dragViewStart).TotalSeconds / Math.Max(1, ActualWidth - PlotLeft);
        _viewStart = _dragViewStart.AddSeconds(-dx * secondsPerPixel);
        _viewEnd = _dragViewEnd.AddSeconds(-dx * secondsPerPixel);
        ClampView(points);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
    }

    private void ClampView(IReadOnlyList<MetricPoint> points)
    {
        var span = _viewEnd - _viewStart;
        if (_viewStart < points[0].Timestamp) { _viewStart = points[0].Timestamp; _viewEnd = _viewStart + span; }
        if (_viewEnd > points[^1].Timestamp) { _viewEnd = points[^1].Timestamp; _viewStart = _viewEnd - span; }
        if (_viewStart < points[0].Timestamp) _viewStart = points[0].Timestamp;
    }

    private const double PlotLeft = 64;
    private const double PlotBottomMargin = 20;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, width), Math.Max(0, height)));
        if (width < 120 || height < 60) return;

        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(string text) => new(text, CultureInfo.GetCultureInfo("de-DE"), FlowDirection.LeftToRight, typeface, 11, LabelBrush, dpi);

        var points = Points;
        if (points is not { Count: > 1 })
        {
            var empty = Label("Für diesen Zeitraum liegen noch keine aufgezeichneten Werte vor.");
            dc.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        var plotHeight = height - PlotBottomMargin;
        var plotWidth = width - PlotLeft;
        var visible = points.Where(p => p.Timestamp >= _viewStart && p.Timestamp <= _viewEnd).ToList();
        var max = visible.Count == 0 ? 1 : visible.Max(p => p.Value);
        max = Unit == MetricUnit.Percent ? Math.Max(100, max) : max <= 0 ? 1 : max * 1.15;
        var definition = new MetricDefinition(MetricId.CpuTotal, "", Unit, _ => null);

        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(plotHeight - plotHeight * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(PlotLeft, y), new Point(width, y));
            var label = Label(definition.Format(max * i / 4));
            dc.DrawText(label, new Point(PlotLeft - label.Width - 6, Math.Clamp(y - label.Height / 2, 0, plotHeight - label.Height)));
        }

        var span = (_viewEnd - _viewStart).TotalSeconds;
        if (span <= 0) return;
        var format = span > 2 * 86400 ? "dd.MM. HH:mm" : span > 3600 ? "HH:mm" : "HH:mm:ss";
        for (var i = 0; i <= 4; i++)
        {
            var time = _viewStart.AddSeconds(span * i / 4).ToLocalTime();
            var label = Label(time.ToString(format));
            var x = PlotLeft + plotWidth * i / 4;
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, PlotLeft, width - label.Width), plotHeight + 4));
        }

        if (visible.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            // Lücken von mehr als dem Fünffachen des typischen Abstands unterbrechen die Linie (Programm war beendet).
            var typicalGap = span / Math.Max(1, visible.Count) * 5 + 60;
            for (var i = 0; i < visible.Count; i++)
            {
                var x = PlotLeft + (visible[i].Timestamp - _viewStart).TotalSeconds / span * plotWidth;
                var y = plotHeight - Math.Clamp(visible[i].Value / max, 0, 1) * (plotHeight - 2) - 1;
                var gap = i > 0 && (visible[i].Timestamp - visible[i - 1].Timestamp).TotalSeconds > typicalGap;
                if (i == 0 || gap) context.BeginFigure(new Point(x, y), false, false);
                else context.LineTo(new Point(x, y), true, true);
            }
        }
        geometry.Freeze();
        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>
/// Einheitlicher Seitenrahmen: Titel und Untertitel der Seite, Ladebalken, Fehlerhinweis und scrollbarer Inhalt.
/// Der Datenkontext ist das jeweilige Seiten-ViewModel.
/// </summary>
public sealed class PageLayout : ContentControl
{
    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(PageLayout), new PropertyMetadata(null));
    public static readonly DependencyProperty ScrollableProperty = DependencyProperty.Register(nameof(Scrollable), typeof(bool), typeof(PageLayout), new PropertyMetadata(true));
    public static readonly DependencyProperty HeaderContentTemplateProperty = DependencyProperty.Register(nameof(HeaderContentTemplate), typeof(DataTemplate), typeof(PageLayout), new PropertyMetadata(null));

    public DataTemplate? HeaderContentTemplate
    {
        get => (DataTemplate?)GetValue(HeaderContentTemplateProperty);
        set => SetValue(HeaderContentTemplateProperty, value);
    }

    /// <summary>Inhalt rechts neben dem Seitentitel (z. B. Schaltflächen).</summary>
    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    /// <summary>false: Der Inhalt füllt die verfügbare Höhe und scrollt selbst (z. B. Tabellen).</summary>
    public bool Scrollable
    {
        get => (bool)GetValue(ScrollableProperty);
        set => SetValue(ScrollableProperty, value);
    }
}

/// <summary>Angehängte Eigenschaften für kleine Hilfen in XAML.</summary>
public static class Attach
{
    /// <summary>Hält eine Liste (z. B. Chatverlauf oder Protokoll) am unteren Ende, wenn Einträge hinzukommen.</summary>
    public static readonly DependencyProperty AutoScrollProperty = DependencyProperty.RegisterAttached("AutoScroll", typeof(bool), typeof(Attach), new PropertyMetadata(false, OnAutoScrollChanged));
    public static bool GetAutoScroll(DependencyObject element) => (bool)element.GetValue(AutoScrollProperty);
    public static void SetAutoScroll(DependencyObject element, bool value) => element.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if (e.NewValue is true) viewer.ScrollChanged += OnScrollChanged;
        else viewer.ScrollChanged -= OnScrollChanged;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Nur nachführen, wenn der Inhalt gewachsen ist und der Benutzer nicht bewusst nach oben geblättert hat.
        if (sender is ScrollViewer viewer && e.ExtentHeightChange > 0 && viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - e.ExtentHeightChange - 40)
            viewer.ScrollToEnd();
    }

    /// <summary>Führt beim Drücken der Eingabetaste in einem Textfeld den angegebenen Befehl aus.</summary>
    public static readonly DependencyProperty EnterCommandProperty = DependencyProperty.RegisterAttached("EnterCommand", typeof(System.Windows.Input.ICommand), typeof(Attach), new PropertyMetadata(null, OnEnterCommandChanged));
    public static System.Windows.Input.ICommand? GetEnterCommand(DependencyObject element) => (System.Windows.Input.ICommand?)element.GetValue(EnterCommandProperty);
    public static void SetEnterCommand(DependencyObject element, System.Windows.Input.ICommand? value) => element.SetValue(EnterCommandProperty, value);

    private static void OnEnterCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.KeyDown -= OnEnterKeyDown;
        if (e.NewValue is not null) element.KeyDown += OnEnterKeyDown;
    }

    private static void OnEnterKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;
        if (sender is not DependencyObject d || GetEnterCommand(d) is not { } command) return;
        if (sender is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}

/// <summary>Bindung für PasswordBox (WPF bietet das nicht von sich aus). Der Wert wird nur im Speicher gehalten und nie protokolliert.</summary>
public static class PasswordBinder
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string), typeof(PasswordBinder),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));
    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBinder), new PropertyMetadata(false));

    public static string GetPassword(DependencyObject d) => (string)d.GetValue(PasswordProperty);
    public static void SetPassword(DependencyObject d, string value) => d.SetValue(PasswordProperty, value);

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnBoxChanged;
        if (!(bool)box.GetValue(UpdatingProperty) && box.Password != (string)e.NewValue) box.Password = (string?)e.NewValue ?? "";
        box.PasswordChanged += OnBoxChanged;
    }

    private static void OnBoxChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        SetPassword(box, box.Password);
        box.SetValue(UpdatingProperty, false);
    }
}
