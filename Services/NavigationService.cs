using System;
using System.Collections.Generic;
using BAR_Advanced_Launcher_2.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class NavigationService : INavigationService
{
    private static readonly IReadOnlyDictionary<string, Type> Pages = new Dictionary<string, Type>
    {
        [PageKeys.Launch] = typeof(LaunchPage),
        [PageKeys.Scripts] = typeof(ScriptsPage),
        [PageKeys.Content] = typeof(ContentPage),
        [PageKeys.History] = typeof(HistoryPage),
        [PageKeys.Infolog] = typeof(InfologPage),
        [PageKeys.Log] = typeof(LogPage),
        [PageKeys.Settings] = typeof(SettingsPage),
    };

    private readonly ILogger<NavigationService> _logger;
    private Frame? _frame;

    public NavigationService(ILogger<NavigationService> logger) => _logger = logger;

    public string? CurrentPageKey { get; private set; }

    public event EventHandler<string>? Navigated;

    /// <summary>Called once by the shell when its <c>Frame</c> is loaded.</summary>
    public void Attach(Frame frame) => _frame = frame;

    public bool NavigateTo(string pageKey, object? parameter = null)
    {
        if (_frame is null)
        {
            _logger.LogWarning("Navigation to '{PageKey}' requested before the frame was attached.", pageKey);
            return false;
        }

        if (!Pages.TryGetValue(pageKey, out Type? pageType))
        {
            _logger.LogError("Unknown page key '{PageKey}'.", pageKey);
            return false;
        }

        if (CurrentPageKey == pageKey && parameter is null)
        {
            return true;
        }

        if (!_frame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo()))
        {
            _logger.LogError("Frame refused navigation to '{PageKey}'.", pageKey);
            return false;
        }

        CurrentPageKey = pageKey;
        Navigated?.Invoke(this, pageKey);
        return true;
    }
}
