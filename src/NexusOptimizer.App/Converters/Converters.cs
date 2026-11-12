using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NexusOptimizer.App.Converters;

/// <summary>true → Visible, false → Collapsed. Mit Parameter „invert“ umgekehrt. Akzeptiert auch null/leere Zeichenketten/0 als false.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter is "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Für Optionsgruppen: liefert true, wenn der gebundene Wert (als Text) dem Parameter entspricht; setzt ihn beim Auswählen zurück.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (type.IsEnum) return Enum.Parse(type, parameter.ToString()!);
        return System.Convert.ChangeType(parameter, type, CultureInfo.InvariantCulture);
    }
}

/// <summary>Wert in Prozent (0–100) → Breite relativ zur verfügbaren Breite (MultiBinding: Wert, Gesamtbreite).</summary>
public sealed class PercentWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double percent || values[1] is not double width || double.IsNaN(percent) || double.IsNaN(width)) return 0d;
        return Math.Max(0, width * Math.Clamp(percent, 0, 100) / 100.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null → true (mit Parameter „invert“: null → false). Für IsEnabled-Bindungen.</summary>
public sealed class IsNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value is null) != (parameter is "invert");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Bytezahl → lesbare Größe („12,4 GB“).</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => "–",
        long l => NexusOptimizer.Core.Storage.ByteFormat.Format(l),
        ulong u => NexusOptimizer.Core.Storage.ByteFormat.Format((double)u),
        int i => NexusOptimizer.Core.Storage.ByteFormat.Format((long)i),
        double d => double.IsNaN(d) ? "–" : NexusOptimizer.Core.Storage.ByteFormat.Format(d),
        _ => value.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
