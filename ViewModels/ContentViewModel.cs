using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.ViewModels;

/// <summary>The maps / games / engines browser (PLAN.md §7).</summary>
public sealed partial class ContentViewModel : ObservableObject
{
    private readonly IInstallationContext _installation;
    private readonly IArchiveCatalog _catalog;
    private readonly IShellService _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ContentViewModel> _logger;

    /// <summary>Install the bound lists were built for, so a repeat load is a no-op.</summary>
    private string? _loadedForInstallPath;

    /// <summary>The load currently running, shared by overlapping callers.</summary>
    private Task? _loading;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>Set when the index came from filenames because no cache file exists.</summary>
    [ObservableProperty]
    public partial string? DegradedMessage { get; set; }

    /// <summary>Set when archives exist on disk that the cache has not indexed.</summary>
    [ObservableProperty]
    public partial string? NotIndexedMessage { get; set; }

    [ObservableProperty]
    public partial string MapFilter { get; set; }

    [ObservableProperty]
    public partial MapArchive? SelectedMap { get; set; }

    [ObservableProperty]
    public partial GameArchive? SelectedGame { get; set; }

    public ObservableCollection<MapArchive> Maps { get; } = new();

    public ObservableCollection<GameArchive> Games { get; } = new();

    public ObservableCollection<MenuArchive> Menus { get; } = new();

    public ObservableCollection<EngineBuild> Engines { get; } = new();

    public ContentViewModel(
        IInstallationContext installation,
        IArchiveCatalog catalog,
        IShellService shell,
        IUiDispatcher dispatcher,
        ILogger<ContentViewModel> logger)
    {
        _installation = installation;
        _catalog = catalog;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;

        MapFilter = string.Empty;

        _installation.Changed += (_, _) => _dispatcher.Post(() => _ = LoadAsync());
    }

    /// <summary>
    /// Both the page's Loaded event and IInstallationContext.Changed ask for a load, and
    /// on startup the two overlap — each would otherwise get past the
    /// <see cref="_loadedForInstallPath"/> check before the other had set it. Both
    /// callers run on the UI thread, so sharing the in-flight task needs no lock.
    /// </summary>
    [RelayCommand]
    private Task LoadAsync() =>
        _loading is { IsCompleted: false } inFlight ? inFlight : _loading = LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        await _installation.InitializeAsync();

        BarInstallation? installation = _installation.Current;
        if (installation is null)
        {
            StatusMessage = "No installation selected.";
            _loadedForInstallPath = null;
            return;
        }

        // Nothing to do when navigating back to a page already bound to this install.
        if (_loadedForInstallPath == installation.RootPath)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Reading the archive cache…";
        try
        {
            // The 10 MB parse runs off the UI thread inside the catalog; the ring stays
            // live because this method only awaits it (PLAN.md §5.4).
            await _catalog.LoadAsync(installation);
            Apply(_catalog.Index);
            _loadedForInstallPath = installation.RootPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the content catalog.");
            StatusMessage = "Could not read the archive cache. See the Log page.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        BarInstallation? installation = _installation.Current;
        if (installation is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Re-parsing the archive cache…";
        try
        {
            await _catalog.RefreshAsync(installation);
            Apply(_catalog.Index);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenMapsFolder() => OpenIfPresent(_installation.Current?.MapsPath);

    [RelayCommand]
    private void OpenGamesFolder() => OpenIfPresent(_installation.Current?.GamesPath);

    private void OpenIfPresent(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _shell.OpenFolder(path);
        }
    }

    partial void OnMapFilterChanged(string value) => ApplyMapFilter();

    private void Apply(ArchiveIndex index)
    {
        Replace(Games, index.Games.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase));
        Replace(Menus, index.Menus.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase));
        Replace(Engines, _installation.Engines);
        ApplyMapFilter();

        SelectedGame ??= Games.FirstOrDefault();

        DegradedMessage = index.IsDegraded
            ? "No ArchiveCache*.lua was found, so this list was built from filenames only. " +
              "Run the engine once to build the cache."
            : null;

        NotIndexedMessage = index.NotIndexedOnDisk.Count == 0
            ? null
            : $"{index.NotIndexedOnDisk.Count} archive(s) on disk are not in the cache and cannot be " +
              "selected yet. Run the engine once to index them.";

        StatusMessage =
            $"{index.Maps.Count} maps · {index.Games.Count} games · {index.Menus.Count} menus · " +
            $"{_installation.Engines.Count} engines";

        _logger.LogDebug("Content page bound {Summary}.", StatusMessage);
    }

    private void ApplyMapFilter()
    {
        IEnumerable<MapArchive> maps = _catalog.Index.Maps;

        if (!string.IsNullOrWhiteSpace(MapFilter))
        {
            string needle = MapFilter.Trim();
            maps = maps.Where(m =>
                m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (m.Author?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        Replace(Maps, maps.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase));

        if (SelectedMap is not null && !Maps.Contains(SelectedMap))
        {
            SelectedMap = null;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }
}
