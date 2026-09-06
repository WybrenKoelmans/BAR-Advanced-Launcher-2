using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>Executes <c>ArchiveCache*.lua</c> and buckets its archives by modtype.</summary>
public interface IArchiveCacheParser
{
    /// <summary>
    /// Finds the newest <c>*.lua</c> in <paramref name="cacheFolder"/> and returns its
    /// stamp, or null when the folder holds none.
    /// </summary>
    ArchiveCacheStamp? FindNewestCache(string cacheFolder);

    /// <summary>
    /// Parses the file named by <paramref name="stamp"/>. Runs off the calling thread;
    /// the real file is 10 MB (PLAN.md §5.4).
    /// </summary>
    Task<ArchiveIndex> ParseAsync(ArchiveCacheStamp stamp, CancellationToken cancellationToken = default);
}
