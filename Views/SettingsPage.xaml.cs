using BAR_Advanced_Launcher_2.ViewModels;

namespace BAR_Advanced_Launcher_2.Views;

/// <summary>
/// XAML cannot name an open generic as a root element, so each page gets a closed
/// alias over <see cref="ViewModelPage{TViewModel}"/>.
/// </summary>
public abstract class SettingsPageBase : ViewModelPage<SettingsViewModel>;

public sealed partial class SettingsPage : SettingsPageBase
{
    public SettingsPage() => InitializeComponent();
}
