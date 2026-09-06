using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Infrastructure;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class ArchiveCatalog : IArchiveCatalog
{
    /// <summary>Archive extensions the engine actually loads.</summary>
    private static readonly string[] ArchiveExtensions = { ".sd7", ".sdz", ".sdd" };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ILogger<ArchiveCatalog> _logger;
    private readonly IArchiveCacheParser _parser;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArchiveCatalog(ILogger<ArchiveCatalog> logger, IArchiveCacheParser parser)
        : this(logger, parser, AppPaths.ArchiveIndexFile)
    {
    }

    /// <summary>Test seam: lets a test memoise into a disposable folder.</summary>
    public ArchiveCatalog(ILogger<ArchiveCatalog> logger, IArchiveCacheParser parser, string indexPath)
    {
        _logger = logger;
        _parser = parser;
        _indexPath = indexPath;
    }

    public ArchiveIndex Index { get; private set; } = ArchiveIndex.Empty;

    public bool IsLoading { get; private set; }

    public event EventHandler? IndexChanged;

    public Task<ArchiveIndex> LoadAsync(BarInstallation installation, CancellationToken cancellationToken = default) =>
        LoadCoreAsync(installation, forceReparse: false, cancellationToken);

    public Task<ArchiveIndex> RefreshAsync(BarInstallation installation, CancellationToken cancellationToken = default) =>
        LoadCoreAsync(installation, forceReparse: true, cancellationToken);

    private async Task<ArchiveIndex> LoadCoreAsync(
        BarInstallation installation,
        bool forceReparse,
        CancellationToken cancellationToken)
    {
        ArchiveIndex index;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsLoading = true;
        try
        {
            ArchiveCacheStamp? stamp = _parser.FindNewestCache(installation.CachePath);

            index = stamp is null
                ? BuildDegradedIndex(installation)
                : await LoadOrParseAsync(stamp, forceReparse, cancellationToken).ConfigureAwait(false);

            index.NotIndexedOnDisk = FindUnindexedArchives(installation, index);
            Index = index;
        }
        finally
        {
            IsLoading = false;
            _gate.Release();
        }

        // Raised only once the index is fully built and published, so a handler that
        // reads Index never sees the previous one.
        IndexChanged?.Invoke(this, EventArgs.Empty);
        return index;
    }

    private async Task<ArchiveIndex> LoadOrParseAsync(
        ArchiveCacheStamp stamp,
        bool forceReparse,
        CancellationToken cancellationToken)
    {
        if (!forceReparse)
        {
            ArchiveIndex? memoised = await ReadMemoisedAsync(cancellationToken).ConfigureAwait(false);

            // Reuse only when path, size and write time all still match: the engine
            // rewrites the cache after a content download (PLAN.md §5.4).
            if (memoised?.Stamp == stamp)
            {
                _logger.LogInformation(
                    "Reusing the memoised index of {Path} ({Count} archives, parsed {Parsed:u}).",
                    stamp.Path,
                    memoised.TotalCount,
                    memoised.ParsedAtUtc);

                return memoised;
            }

            if (memoised is not null)
            {
                _logger.LogInformation("The archive cache changed since the last parse; re-parsing.");
            }
        }

        ArchiveIndex parsed = await _parser.ParseAsync(stamp, cancellationToken).ConfigureAwait(false);
        await WriteMemoisedAsync(parsed, cancellationToken).ConfigureAwait(false);
        return parsed;
    }

    private async Task<ArchiveIndex?> ReadMemoisedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.Open(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer
                .DeserializeAsync<ArchiveIndex>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the memoised index at {Path}; re-parsing.", _indexPath);
            return null;
        }
    }

    private async Task WriteMemoisedAsync(ArchiveIndex index, CancellationToken cancellationToken)
    {
        if (index.TotalCount == 0)
        {
            // A failed parse must not be memoised, or the failure sticks until the
            // cache file next changes.
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);

            string temp = _indexPath + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer
                    .SerializeAsync(stream, index, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, _indexPath, overwrite: true);
            _logger.LogDebug("Memoised {Count} archives to {Path}.", index.TotalCount, _indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not memoise the index to {Path}.", _indexPath);
        }
    }

    /// <summary>
    /// Builds a name-only index from the filesystem when there is no cache at all, so
    /// the app stays usable. The UI marks this clearly (PLAN.md §5.4).
    /// </summary>
    private ArchiveIndex BuildDegradedIndex(BarInstallation installation)
    {
        _logger.LogWarning(
            "No archive cache in {Path}; falling back to filenames. Run the engine once to build it.",
            installation.CachePath);

        var index = new ArchiveIndex
        {
            IsDegraded = true,
            ParsedAtUtc = DateTimeOffset.UtcNow,
        };

        foreach (string file in EnumerateArchives(installation.MapsPath))
        {
            index.Maps.Add(new MapArchive
            {
                Name = Path.GetFileNameWithoutExtension(file),
                ArchiveFileName = Path.GetFileName(file),
                ArchivePath = installation.MapsPath,
            });
        }

        foreach (string file in EnumerateArchives(installation.GamesPath))
        {
            index.Games.Add(new GameArchive
            {
                Name = Path.GetFileNameWithoutExtension(file),
                ArchiveFileName = Path.GetFileName(file),
                ArchivePath = installation.GamesPath,
            });
        }

        return index;
    }

    /// <summary>
    /// Lists archives on disk that the cache does not mention — they need one engine
    /// run before they can be selected.
    /// </summary>
    private List<string> FindUnindexedArchives(BarInstallation installation, ArchiveIndex index)
    {
        if (index.IsDegraded)
        {
            // Everything came from disk, so nothing can be missing from it.
            return new List<string>();
        }

        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveEntry entry in index.Maps
                     .Cast<ArchiveEntry>()
                     .Concat(index.Games)
                     .Concat(index.Menus)
                     .Concat(index.Other))
        {
            if (!string.IsNullOrEmpty(entry.ArchiveFileName))
            {
                indexed.Add(entry.ArchiveFileName);
            }
        }

        var missing = new List<string>();
        foreach (string folder in new[] { installation.MapsPath, installation.GamesPath })
        {
            foreach (string file in EnumerateArchives(folder))
            {
                if (!indexed.Contains(Path.GetFileName(file)))
                {
                    missing.Add(file);
                }
            }
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation(
                "{Count} archive(s) on disk are not in the cache: {Names}.",
                missing.Count,
                string.Join(", ", missing.Select(Path.GetFileName).Take(10)));
        }

        return missing;
    }

    /// <summary>
    /// Archives in a folder. A <c>.sdd</c> game is a directory, and every <c>.sd7</c>
    /// map has a <c>.md5.gz</c> sidecar that must not be counted — those sidecars are
    /// half the file count in <c>data\maps</c> (PLAN.md §5.4).
    /// </summary>
    private IEnumerable<string> EnumerateArchives(string folder)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list {Path}.", folder);
            yield break;
        }

        foreach (string entry in entries)
        {
            if (ArchiveExtensions.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }

}
