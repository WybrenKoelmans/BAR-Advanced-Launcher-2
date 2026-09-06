using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Reads an engine infolog (PLAN.md §5.8).
///
/// Two operations rather than one, because the two have very different costs. A summary
/// is one streaming pass that keeps only counters and a handful of scalars, so it is
/// affordable on a 42 MB log. A read materialises <see cref="InfologLine"/> objects and
/// is therefore always filtered and always bounded.
///
/// Every file is opened with <c>FileShare.ReadWrite</c>: the engine holds its log open
/// for the whole run, and the most interesting log is usually the one still being
/// written.
/// </summary>
public interface IInfologParser
{
    /// <summary>
    /// Establishes what a run was and how it ended. Never throws — an unreadable file
    /// comes back as a summary carrying <see cref="InfologSummary.ReadError"/>.
    /// </summary>
    Task<InfologSummary> SummariseAsync(InfologFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Materialises the lines matching <paramref name="filter"/>, up to
    /// <see cref="InfologRead.LineLimit"/>. Never throws.
    /// </summary>
    Task<InfologRead> ReadAsync(
        InfologFile file,
        InfologFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a read from <paramref name="cursor"/>, for a live viewer following a log the
    /// engine is still writing. Only the lines written since the cursor are materialised,
    /// which is what makes polling a growing file affordable — the alternative is
    /// re-scanning it from the start on every poll. Never throws.
    /// </summary>
    Task<InfologTailRead> TailAsync(
        InfologFile file,
        InfologFilter filter,
        InfologTailCursor cursor,
        CancellationToken cancellationToken = default);
}
