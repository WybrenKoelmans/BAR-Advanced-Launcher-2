using System;
using System.Collections.Generic;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Finds the engine's logs across the three locations of PLAN.md §5.8, newest first.
///
/// Listing only — nothing here opens a file. That is what makes it usable on this
/// install, whose <c>data\log</c> holds 773 rotated logs totalling 378 MB.
/// </summary>
public interface IInfologCatalog
{
    /// <summary>
    /// Every log under the active install, newest write first. Empty when there is no
    /// install or the folders are missing.
    /// </summary>
    IReadOnlyList<InfologFile> Discover();

    /// <summary>The most recently written log, which is normally the run just finished.</summary>
    InfologFile? MostRecent();

    /// <summary>
    /// The log belonging to a past run.
    ///
    /// Matched on write time rather than on the path the run recorded: the engine always
    /// writes <c>&lt;write-dir&gt;\infolog.txt</c> and rotates the previous one out of the
    /// way on its next start, so a stored path stops being that run's log as soon as
    /// anything else launches.
    /// </summary>
    /// <param name="startedAt">When the process was started.</param>
    /// <param name="exitedAt">When it exited, or null when that was never recorded.</param>
    InfologFile? FindForRun(DateTimeOffset startedAt, DateTimeOffset? exitedAt);
}
