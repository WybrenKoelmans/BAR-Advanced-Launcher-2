using BAR_Advanced_Launcher_2.ViewModels;
using Microsoft.UI.Xaml;

namespace BAR_Advanced_Launcher_2.Views;

/// <summary>
/// XAML cannot name an open generic as a root element, so each page gets a closed
/// alias over <see cref="ViewModelPage{TViewModel}"/>.
/// </summary>
public abstract class LaunchPageBase : ViewModelPage<LaunchViewModel>;

public sealed partial class LaunchPage : LaunchPageBase
{
    public LaunchPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.LoadCommand.Execute(null);

    /// <summary>
    /// The run-option text boxes bind on every keystroke so the command preview keeps up,
    /// but they are only written back to the profile on the way out. Otherwise typing an
    /// argument would rewrite profiles.json once per character.
    /// </summary>
    private void OnRunOptionLostFocus(object sender, RoutedEventArgs e) =>
        ViewModel.CommitRunOptionsCommand.Execute(null);
}
