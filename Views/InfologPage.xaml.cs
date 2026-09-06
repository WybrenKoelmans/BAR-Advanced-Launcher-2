using System;
using System.Collections.Generic;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using BAR_Advanced_Launcher_2.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BAR_Advanced_Launcher_2.Views;

/// <summary>
/// XAML cannot name an open generic as a root element, so each page gets a closed
/// alias over <see cref="ViewModelPage{TViewModel}"/>.
/// </summary>
public abstract class InfologPageBase : ViewModelPage<InfologViewModel>;

/// <summary>
/// The scroll behaviour a live log viewer is expected to have, none of which the view
/// model can own itself since it has no notion of pixels or visible rows:
///
/// <list type="bullet">
/// <item>while scrolled to the bottom, a live tail keeps it pinned there;</item>
/// <item>while scrolled up, a live tail leaves the view alone and offers a way back down;</item>
/// <item>whenever the view is scrolled away from the bottom, for any reason, a button
/// offers to jump back there;</item>
/// <item>a filter change restores the selected line, or failing that the topmost visible
/// one, so re-filtering does not strand the reader somewhere unrelated.</item>
/// </list>
/// </summary>
public sealed partial class InfologPage : InfologPageBase
{
    /// <summary>How close to the end still counts as "at the bottom".</summary>
    private const double BottomTolerance = 4;

    /// <summary>A log handed over by another page, applied once this page has loaded.</summary>
    private InfologFile? _requested;

    /// <summary>
    /// A run just started by the Launch page, handed over instead of a file since its
    /// log may not exist on disk yet. Applied once this page has loaded.
    /// </summary>
    private DateTimeOffset? _requestedRunStartedAt;

    private ScrollViewer? _scrollViewer;

    /// <summary>
    /// Starts false until a real scroll state has been observed. <see cref="OnLinesReloaded"/>
    /// sets this true itself once a freshly opened log has been scrolled to the end.
    /// </summary>
    private bool _isAtBottom;
    private int _pendingNewLines;

    /// <summary>The line to restore after the read a filter change starts.</summary>
    private InfologLine? _anchorLine;

    private bool _anchorIsSelection;

    /// <summary>Whether the read <see cref="OnLinesReloading"/> just started is for a newly selected log rather than a filter change on the current one.</summary>
    private bool _isFreshFile;

    /// <summary>
    /// The file <see cref="Lines"/> is currently showing — not necessarily
    /// <c>ViewModel.SelectedFile</c>, which has already moved on to the new selection by
    /// the time a file switch's read starts. Used to tell "the filter changed on the same
    /// log" from "the reader picked a different log entirely", since only the former is
    /// worth restoring a scroll position for.
    /// </summary>
    private string? _currentLinesFilePath;

    public InfologPage() => InitializeComponent();

    /// <summary>
    /// The History page navigates here with the log it matched to a run, and the Launch
    /// page navigates here with the instance it just started. Taken as a navigation
    /// parameter rather than by one view model calling into another, so the pages stay
    /// independent.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        switch (e.Parameter)
        {
            case InfologFile file:
                _requested = file;
                break;
            case RunningInstance instance:
                _requestedRunStartedAt = instance.StartedAt;
                break;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LinesReloading += OnLinesReloading;
        ViewModel.LinesReloaded += OnLinesReloaded;
        ViewModel.LinesAppended += OnLinesAppended;
        ViewModel.OnActivated();

        if (_requested is { } file)
        {
            _requested = null;
            _ = ViewModel.ShowAsync(file);
            return;
        }

        if (_requestedRunStartedAt is { } startedAt)
        {
            _requestedRunStartedAt = null;
            _ = ViewModel.ShowForRunAsync(startedAt);
            return;
        }

        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Undoes <see cref="OnLoaded"/>. The view model is a singleton that outlives this
    /// page, so without this a navigation away and back would stack a second poll timer
    /// and a second copy of every handler onto the same instance.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LinesReloading -= OnLinesReloading;
        ViewModel.LinesReloaded -= OnLinesReloaded;
        ViewModel.LinesAppended -= OnLinesAppended;
        ViewModel.OnDeactivated();

        if (_scrollViewer is { } scrollViewer)
        {
            scrollViewer.ViewChanged -= OnLinesScrollViewChanged;
            _scrollViewer = null;
        }
    }

    /// <summary>
    /// A <see cref="ListView"/> does not expose its <see cref="ScrollViewer"/> directly;
    /// it only exists once the control's template has been applied, which <c>Loaded</c>
    /// on the list itself (rather than the page) guarantees.
    /// </summary>
    private void OnLinesListViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer is not null)
        {
            return;
        }

        if (FindDescendant<ScrollViewer>(LinesListView) is not { } scrollViewer)
        {
            return;
        }

        _scrollViewer = scrollViewer;
        scrollViewer.ViewChanged += OnLinesScrollViewChanged;
        UpdateIsAtBottom(scrollViewer);
    }

    private void OnLinesScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || _scrollViewer is not { } scrollViewer)
        {
            return;
        }

        UpdateIsAtBottom(scrollViewer);
    }

    private void UpdateIsAtBottom(ScrollViewer scrollViewer)
    {
        _isAtBottom = scrollViewer.ScrollableHeight <= 0
                      || scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - BottomTolerance;

        if (_isAtBottom)
        {
            HideJumpToNewest();
        }
        else
        {
            // Scrolled up for any reason — reading back, or a filter change that landed
            // partway through the file — not just while new lines are piling up below.
            ShowJumpToNewest();
        }
    }

    // ---- filter changes: restore the reader's place --------------------------------

    /// <summary>
    /// Captures where the reader is before a filter-driven read clears <see cref="Lines"/>
    /// out from under them: the selected line if there is one, otherwise the topmost line
    /// currently visible.
    /// </summary>
    private void OnLinesReloading(object? sender, EventArgs e)
    {
        _pendingNewLines = 0;
        HideJumpToNewest();

        _anchorIsSelection = false;
        _anchorLine = null;

        _isFreshFile = !string.Equals(
            _currentLinesFilePath,
            ViewModel.SelectedFile?.Path,
            StringComparison.OrdinalIgnoreCase);

        if (_isFreshFile)
        {
            // A different log entirely: nothing currently on screen is worth chasing, so
            // OnLinesReloaded opens it at the end instead, as a live/tail-style viewer does.
            return;
        }

        if (LinesListView.SelectedItem is InfologLine selected)
        {
            _anchorLine = selected;
            _anchorIsSelection = true;
            return;
        }

        _anchorLine = FirstVisibleLine();
    }

    /// <summary>
    /// Restores the reader's place once the filtered read has repopulated
    /// <see cref="Lines"/>. A freshly selected log opens at the end, the same as
    /// <c>tail -f</c>, both because that is where a running match's newest — and most
    /// likely relevant — output is, and because a live tail only sticks once the reader
    /// is already scrolled to the bottom; opening at the top would silently need a manual
    /// scroll down before new lines started following.
    /// </summary>
    private void OnLinesReloaded(object? sender, EventArgs e)
    {
        InfologLine? anchor = _anchorLine;
        bool isFreshFile = _isFreshFile;
        _anchorLine = null;
        _currentLinesFilePath = ViewModel.SelectedFile?.Path;

        if (isFreshFile)
        {
            ScrollToNewest();
            _isAtBottom = true;
            return;
        }

        if (anchor is null || ViewModel.Lines.Count == 0)
        {
            return;
        }

        InfologLine target = ClosestLine(anchor.Number);

        LinesListView.ScrollIntoView(target);

        if (_anchorIsSelection)
        {
            LinesListView.SelectedItem = target;
        }
    }

    private InfologLine? FirstVisibleLine()
    {
        if (LinesListView.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0)
        {
            return null;
        }

        IReadOnlyList<InfologLine> lines = ViewModel.Lines;
        int index = panel.FirstVisibleIndex;

        return index < lines.Count ? lines[index] : null;
    }

    /// <summary>The shown line nearest <paramref name="number"/>, which is ascending since lines are read in file order.</summary>
    private InfologLine ClosestLine(int number)
    {
        IReadOnlyList<InfologLine> lines = ViewModel.Lines;
        InfologLine? previous = null;

        foreach (InfologLine line in lines)
        {
            if (line.Number >= number)
            {
                return previous is null || (number - previous.Number) > (line.Number - number)
                    ? line
                    : previous;
            }

            previous = line;
        }

        return previous ?? lines[^1];
    }

    // ---- live tail: stick to the bottom ---------------------------------------------

    /// <summary>
    /// Follows a live tail's new lines when the reader was already at the bottom;
    /// otherwise offers a way back down rather than moving the view under them.
    /// </summary>
    private void OnLinesAppended(object? sender, LinesAppendedEventArgs e)
    {
        if (_isAtBottom)
        {
            ScrollToNewest();
            return;
        }

        _pendingNewLines += e.Count;
        ShowJumpToNewest();
    }

    /// <summary>
    /// Shows the scroll-to-bottom button, labelled with the pending line count while a
    /// live tail is the reason it is offered, or generically otherwise.
    /// </summary>
    private void ShowJumpToNewest()
    {
        JumpToNewestButton.Content = _pendingNewLines switch
        {
            0 => "↓ Scroll to bottom",
            1 => "↓ 1 new line",
            _ => $"↓ {_pendingNewLines} new lines",
        };
        JumpToNewestButton.Visibility = Visibility.Visible;
    }

    private void OnJumpToNewestClick(object sender, RoutedEventArgs e)
    {
        HideJumpToNewest();
        ScrollToNewest();
        _isAtBottom = true;
    }

    /// <summary>
    /// Jumps straight to the bottom with no scroll animation. <see cref="ListView.ScrollIntoView(object)"/>
    /// smooth-scrolls, which for a busy log means a visible little scroll animation on
    /// every single poll tick — exactly the "the log is animating" effect a live tail
    /// should avoid. <see cref="double.MaxValue"/> is clamped to the real extent by the
    /// scroll viewer, so this works even if the newly appended items haven't been
    /// measured yet — but only once <see cref="UIElement.UpdateLayout"/> forces that
    /// measurement to happen. Without it, the extent the clamp uses still excludes the
    /// items just added to <see cref="InfologViewModel.Lines"/>, so each tail undershoots
    /// the true bottom by however tall that batch was; short-lived, but it compounds tick
    /// after tick until the accumulated gap reads as "not at the bottom" any more.
    /// </summary>
    private void ScrollToNewest()
    {
        if (ViewModel.Lines.Count == 0)
        {
            return;
        }

        if (_scrollViewer is { } scrollViewer)
        {
            LinesListView.UpdateLayout();
            scrollViewer.ChangeView(null, double.MaxValue, null, disableAnimation: true);
            return;
        }

        LinesListView.ScrollIntoView(ViewModel.Lines[^1]);
    }

    private void HideJumpToNewest()
    {
        _pendingNewLines = 0;
        JumpToNewestButton.Visibility = Visibility.Collapsed;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
