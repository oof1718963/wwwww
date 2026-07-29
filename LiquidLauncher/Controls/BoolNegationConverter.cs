using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LiquidLauncher.Controls;

/// <summary>
/// Flips a bool. Used to derive "!IsGlass" inside the SquircleGlassCard control theme,
/// since TemplateBinding doesn't support the "!" negation shorthand that plain Binding does.
/// </summary>
public class BoolNegationConverter : IValueConverter
{
    public static readonly BoolNegationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
