using BAR_Advanced_Launcher_2.Services;
using BAR_Advanced_Launcher_2.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BAR_Advanced_Launcher_2.Views;

/// <summary>
/// XAML cannot name an open generic as a root element, so each page gets a closed
/// alias over <see cref="ViewModelPage{TViewModel}"/>.
/// </summary>
public abstract class ScriptsPageBase : ViewModelPage<ScriptsViewModel>;

public sealed partial class ScriptsPage : ScriptsPageBase
{
    public ScriptsPage()
    {
        InitializeComponent();
        BuildTemplateMenu();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.LoadCommand.Execute(null);

    /// <summary>
    /// A MenuFlyout has no ItemsSource, so the template list is built once here rather
    /// than declared in markup. The set is fixed at construction, so once is enough.
    /// </summary>
    private void BuildTemplateMenu()
    {
        foreach (StartScriptTemplate template in ViewModel.Templates)
        {
            var item = new MenuFlyoutItem
            {
                Text = template.Name,
                Command = ViewModel.NewCommand,
                CommandParameter = template,
            };

            ToolTipService.SetToolTip(item, template.Description);
            TemplateMenu.Items.Add(item);
        }
    }
}
