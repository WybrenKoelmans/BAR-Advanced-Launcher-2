using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace BAR_Advanced_Launcher_2.Converters;

/// <summary>Collapsed when the value is null or an empty string, visible otherwise.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to collapse when the value is present instead.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool hasValue = value is not null && value is not string { Length: 0 };
        return hasValue != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
