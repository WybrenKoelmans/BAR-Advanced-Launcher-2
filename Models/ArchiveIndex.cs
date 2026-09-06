using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// Identifies the cache file a parse came from. The memoised index is only reused
/// when all three still match, so a content download — which rewrites the cache —
/// forces a re-parse (PLAN.md §5.4).
/// </summary>
public sealed record ArchiveCacheStamp(string Path, long Length, DateTime LastWriteUtc);

/// <summary>
/// The parsed <c>ArchiveCache*.lua</c>, and what is serialised to <c>index.json</c>.
/// Separate arrays per kind rather than one polymorphic list, so System.Text.Json
/// round-trips it without a type discriminator.
/// </summary>
public sealed class ArchiveIndex
{
    public ArchiveCacheStamp? Stamp { get; set; }

    public DateTimeOffset ParsedAtUtc { get; set; }

    public List<GameArchive> Games { get; set; } = new();

    public List<MapArchive> Maps { get; set; } = new();

    public List<MenuArchive> Menus { get; set; } = new();

    public List<OtherArchive> Other { get; set; } = new();

    /// <summary>
    /// True when there was no cache file and the lists were built from filenames on
    /// disk instead. The UI marks this state as degraded (PLAN.md §5.4).
    /// </summary>
    public bool IsDegraded { get; set; }

    /// <summary>
    /// Archives present on disk but absent from the cache — they need one engine run
    /// to be indexed.
    /// </summary>
    public List<string> NotIndexedOnDisk { get; set; } = new();

    [JsonIgnore]
    public int TotalCount => Games.Count + Maps.Count + Menus.Count + Other.Count;

    public static ArchiveIndex Empty => new();
}
