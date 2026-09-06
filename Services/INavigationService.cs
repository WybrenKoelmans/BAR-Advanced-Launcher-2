using System;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Page switching, expressed as keys so view models never name a XAML type.
/// The shell owns the <c>Frame</c>; view models only ever say where to go.
/// </summary>
public interface INavigationService
{
    /// <summary>Key of the page currently shown, or null before the first navigation.</summary>
    string? CurrentPageKey { get; }

    event EventHandler<string>? Navigated;

    /// <summary>Navigates to <paramref name="pageKey"/>. No-op if already there.</summary>
    /// <returns>False if the key is unknown or the frame is not attached yet.</returns>
    bool NavigateTo(string pageKey, object? parameter = null);
}

/// <summary>The navigation keys, one per <c>NavigationView</c> item (PLAN.md §7).</summary>
public static class PageKeys
{
    public const string Launch = "launch";
    public const string Scripts = "scripts";
    public const string Content = "content";
    public const string History = "history";
    public const string Infolog = "infolog";
    public const string Log = "log";
    public const string Settings = "settings";
}
