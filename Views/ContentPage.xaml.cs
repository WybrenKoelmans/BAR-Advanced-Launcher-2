using BAR_Advanced_Launcher_2.ViewModels;
using Microsoft.UI.Xaml;

namespace BAR_Advanced_Launcher_2.Views;

public abstract class ContentPageBase : ViewModelPage<ContentViewModel>;

public sealed partial class ContentPage : ContentPageBase
{
    public ContentPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.LoadCommand.Execute(null);
}
