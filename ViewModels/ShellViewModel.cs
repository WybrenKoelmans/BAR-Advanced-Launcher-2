using BAR_Advanced_Launcher_2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BAR_Advanced_Launcher_2.ViewModels;

/// <summary>Backs the <c>NavigationView</c> shell (PLAN.md §7).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigation;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string SelectedPageKey { get; set; }

    public ShellViewModel(INavigationService navigation)
    {
        _navigation = navigation;
        Title = "BAR Advanced Launcher 2";
        SelectedPageKey = PageKeys.Launch;
        _navigation.Navigated += (_, key) => SelectedPageKey = key;
    }

    /// <summary>Invoked by the shell's selection-changed handler with the item's tag.</summary>
    [RelayCommand]
    private void Navigate(string? pageKey)
    {
        if (!string.IsNullOrEmpty(pageKey))
        {
            _navigation.NavigateTo(pageKey);
        }
    }

    /// <summary>Puts the frame on the landing page. Called once the frame is attached.</summary>
    public void NavigateToStartPage() => _navigation.NavigateTo(PageKeys.Launch);
}
