using Microsoft.UI.Xaml.Controls;

namespace BAR_Advanced_Launcher_2.Views;

/// <summary>
/// A page whose view model comes from the container rather than from a XAML-constructed
/// instance. Keeps every view model constructor-injected without a locator in markup.
/// </summary>
public abstract class ViewModelPage<TViewModel> : Page
    where TViewModel : class
{
    protected ViewModelPage()
    {
        ViewModel = App.GetService<TViewModel>();
        DataContext = ViewModel;
    }

    public TViewModel ViewModel { get; }
}
