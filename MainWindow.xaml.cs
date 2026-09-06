using System.Linq;
using BAR_Advanced_Launcher_2.Services;
using BAR_Advanced_Launcher_2.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BAR_Advanced_Launcher_2;

/// <summary>The NavigationView shell (PLAN.md §7).</summary>
public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;

    public MainWindow()
    {
        InitializeComponent();

        ViewModel = App.GetService<ShellViewModel>();
        _navigation = App.GetService<NavigationService>();
        _navigation.Attach(ContentFrame);
        _navigation.Navigated += (_, key) => SyncSelection(key);

        Title = ViewModel.Title;
        ViewModel.NavigateToStartPage();
    }

    public ShellViewModel ViewModel { get; }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string pageKey })
        {
            ViewModel.NavigateCommand.Execute(pageKey);
        }
    }

    /// <summary>Keeps the pane highlight in step with navigations the pane did not start.</summary>
    private void SyncSelection(string pageKey)
    {
        NavigationViewItem? match = Nav.MenuItems
            .Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (string?)item.Tag == pageKey);

        if (match is not null && !ReferenceEquals(Nav.SelectedItem, match))
        {
            Nav.SelectedItem = match;
        }
    }
}
