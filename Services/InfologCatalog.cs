using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class InfologCatalog : IInfologCatalog
{
    /// <summary>
    /// How long after a run's recorded exit its log may still have been written.
    ///
    /// The engine's last write is its shutdown, so the gap is normally under a second;
    /// this is slack for a killed process, a clock adjustment, and the difference between
    /// the exit event firing and the file handle closing.
    /// </summary>
    internal static readonly TimeSpan MatchSlack = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A log may also predate the recorded start by this much. Only jitter, but the two
    /// timestamps come from different clocks — one from <c>DateTimeOffset.Now</c> in this
    /// process, one from the file system.
    /// </summary>
    internal static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(10);

    private readonly ILogger<InfologCatalog> _logger;
    private readonly IInstallationContext _installation;

    public InfologCatalog(ILogger<InfologCatalog> logger, IInstallationContext installation)
    {
        _logger = logger;
        _installation = installation;
    }

    public IReadOnlyList<InfologFile> Discover()
    {
        if (_installation.Current is not { } installation)
        {
            return Array.Empty<InfologFile>();
        }

        return Enumerate(installation, _logger);
    }

    public InfologFile? MostRecent() => Discover().FirstOrDefault();

    public InfologFile? FindForRun(DateTimeOffset startedAt, DateTimeOffset? exitedAt) =>
        Match(Discover(), startedAt, exitedAt);

    /// <summary>
    /// Lists the two live logs and every rotated one, newest write first.
    ///
    /// Ordered on <see cref="InfologFile.LastWriteUtc"/> and not on the timestamp in the
    /// file name: the engine has used two naming schemes that disagree about the time
    /// zone, so a name-ordered list interleaves the two eras wrongly. See
    /// <see cref="InfologFile.NameStamp"/>.
    /// </summary>
    internal static IReadOnlyList<InfologFile> Enumerate(BarInstallation installation, ILogger? logger = null)
    {
        var files = new List<InfologFile>();

        Add(files, installation.IsolatedInfologPath, InfologSource.IsolatedRun, logger);
        Add(files, installation.RootInfologPath, InfologSource.InstallRoot, logger);

        try
        {
            if (Directory.Exists(installation.RotatedLogsPath))
            {
                foreach (string path in Directory.EnumerateFiles(installation.RotatedLogsPath, "*infolog*.txt"))
                {
                    Add(files, path, InfologSource.Rotated, logger);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not list rotated logs in {Path}.", installation.RotatedLogsPath);
        }

        return files
            .OrderByDescending(file => file.LastWriteUtc)
            .ThenBy(file => file.Source)
            .ToArray();
    }

    private static void Add(List<InfologFile> files, string path, InfologSource source, ILogger? logger)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                return;
            }

            files.Add(new InfologFile
            {
                Path = info.FullName,
                Source = source,
                Length = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A log that vanished between listing and stat is not worth reporting as an
            // error; the folder is rewritten by the engine on every run.
            logger?.LogDebug(ex, "Could not stat {Path}.", path);
        }
    }

    /// <summary>
    /// Picks the log a run produced, by write time.
    ///
    /// With a known exit, the answer is the log whose last write sits inside the run's
    /// window and closest to its end. Without one — the app was closed while the engine
    /// was up — it is the earliest log written after the run started, because every later
    /// one belongs to a launch that came after.
    /// </summary>
    internal static InfologFile? Match(
        IReadOnlyList<InfologFile> files,
        DateTimeOffset startedAt,
        DateTimeOffset? exitedAt)
    {
        DateTime start = startedAt.UtcDateTime - StartSlack;

        if (exitedAt is not { } exited)
        {
            return files
                .Where(file => file.LastWriteUtc >= start)
                .OrderBy(file => file.LastWriteUtc)
                .FirstOrDefault();
        }

        DateTime end = exited.UtcDateTime + MatchSlack;

        return files
            .Where(file => file.LastWriteUtc >= start && file.LastWriteUtc <= end)
            .OrderBy(file => Math.Abs((file.LastWriteUtc - exited.UtcDateTime).Ticks))
            .FirstOrDefault();
    }
}
