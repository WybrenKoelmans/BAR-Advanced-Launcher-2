using System;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace BAR_Advanced_Launcher_2.Converters;

/// <summary>
/// Looks a brush up by resource key at conversion time.
///
/// The system fill brushes are theme resources, so they cannot simply be captured once:
/// resolving on each call means the log keeps reading correctly if the app is restarted
/// into the other theme, and a missing key degrades to the default text colour rather
/// than throwing inside a data template, where an exception would take the whole list
/// down.
/// </summary>
internal static class ThemeBrush
{
    internal const string Default = "TextFillColorPrimaryBrush";

    internal static Brush? Resolve(string key)
    {
        if (Application.Current?.Resources is not { } resources)
        {
            return null;
        }

        if (resources.TryGetValue(key, out object? found) && found is Brush brush)
        {
            return brush;
        }

        return resources.TryGetValue(Default, out object? fallback) ? fallback as Brush : null;
    }
}

/// <summary>Colours a log line by how bad it is.</summary>
public sealed class InfologSeverityBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is InfologSeverity severity
            ? severity switch
            {
                InfologSeverity.Fatal => "SystemFillColorCriticalBrush",
                InfologSeverity.Error => "SystemFillColorCriticalBrush",
                InfologSeverity.Warning => "SystemFillColorCautionBrush",
                _ => ThemeBrush.Default,
            }
            : ThemeBrush.Default;

        return ThemeBrush.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Colours a diff line by whether it was added or removed.</summary>
public sealed class DiffKindBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is DiffKind kind
            ? kind switch
            {
                DiffKind.Added => "SystemFillColorSuccessBrush",
                DiffKind.Removed => "SystemFillColorCriticalBrush",
                _ => "TextFillColorSecondaryBrush",
            }
            : ThemeBrush.Default;

        return ThemeBrush.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
