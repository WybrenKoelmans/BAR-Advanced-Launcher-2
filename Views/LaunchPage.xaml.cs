using BAR_Advanced_Launcher_2.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BAR_Advanced_Launcher_2.Views;

/// <summary>
/// XAML cannot name an open generic as a root element, so each page gets a closed
/// alias over <see cref="ViewModelPage{TViewModel}"/>.
/// </summary>
public abstract class LaunchPageBase : ViewModelPage<LaunchViewModel>;

public sealed partial class LaunchPage : LaunchPageBase
{
    /// <summary>
    /// Below this content width the sidebar (installation/instances) no longer fits
    /// next to the main column, so it drops underneath instead of getting squeezed.
    /// AdaptiveTrigger does not track the Frame's actual width in this desktop host,
    /// so the two-column/stacked switch is done by hand from SizeChanged instead.
    /// </summary>
    private const double NarrowLayoutBreakpoint = 640;

    private bool? _isNarrow;

    public LaunchPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.LoadCommand.Execute(null);

    private void OnContentGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < NarrowLayoutBreakpoint;
        if (_isNarrow == narrow)
        {
            return;
        }

        _isNarrow = narrow;

        MainColumn.Width = new GridLength(narrow ? 1 : 2, GridUnitType.Star);
        SidebarColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SidebarColumn.MinWidth = narrow ? 0 : 260;
        Grid.SetColumn(SidebarStack, narrow ? 0 : 1);
        Grid.SetRow(SidebarStack, narrow ? 1 : 0);
    }

    /// <summary>
    /// The run-option text boxes bind on every keystroke so the command preview keeps up,
    /// but they are only written back to the profile on the way out. Otherwise typing an
    /// argument would rewrite profiles.json once per character.
    /// </summary>
    private void OnRunOptionLostFocus(object sender, RoutedEventArgs e) =>
        ViewModel.CommitRunOptionsCommand.Execute(null);
}
