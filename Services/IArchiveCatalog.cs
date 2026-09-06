using System;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// The maps, games and menus of the active install, memoised across runs so only the
/// first start pays for the 10 MB Lua parse (PLAN.md §5.4).
/// </summary>
public interface IArchiveCatalog
{
    /// <summary>The current index. Empty until <see cref="LoadAsync"/> completes.</summary>
    ArchiveIndex Index { get; }

    bool IsLoading { get; }

    /// <summary>Raised on the loading thread whenever <see cref="Index"/> is replaced.</summary>
    event EventHandler? IndexChanged;

    /// <summary>
    /// Returns the memoised index when the cache file is unchanged, otherwise re-parses.
    /// </summary>
    Task<ArchiveIndex> LoadAsync(BarInstallation installation, CancellationToken cancellationToken = default);

    /// <summary>Re-parses unconditionally, for the Refresh button.</summary>
    Task<ArchiveIndex> RefreshAsync(BarInstallation installation, CancellationToken cancellationToken = default);
}
