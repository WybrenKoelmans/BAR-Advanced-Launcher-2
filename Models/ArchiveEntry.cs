using System;
using System.Text.Json.Serialization;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// The <c>modtype</c> discriminator in <c>ArchiveCache*.lua</c>. Verified by counting
/// the real cache on this machine: 7 games, 126 maps, 4 base, 5 menus (PLAN.md §2.3).
/// </summary>
public enum ArchiveKind
{
    /// <summary>A modtype this build does not know. Kept rather than thrown away, so a
    /// schema bump degrades instead of breaking (PLAN.md §9).</summary>
    Unknown = 0,

    Game = 1,

    Map = 3,

    BaseContent = 4,

    Menu = 5,
}

/// <summary>
/// Fields shared by every archive in the cache. <see cref="Name"/> is the one that
/// matters most: it is what a start script's <c>mapname</c> / <c>gametype</c> must
/// contain, e.g. <c>Beyond All Reason $VERSION</c> — where <c>$VERSION</c> is literal
/// for a <c>.sdd</c> checkout and must not be "fixed" (PLAN.md §2.3).
/// </summary>
public abstract record ArchiveEntry
{
    /// <summary>The engine-facing name, from <c>archivedata.name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Name without the version suffix, from <c>archivedata.name_pure</c>.</summary>
    public string? NamePure { get; init; }

    public string? Version { get; init; }

    public string? ShortName { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    /// <summary>The archive file, e.g. <c>adamantium_factory_v1.sd7</c>.</summary>
    public string? ArchiveFileName { get; init; }

    /// <summary>Folder the archive was found in, as recorded by the engine.</summary>
    public string? ArchivePath { get; init; }

    public string? Checksum { get; init; }

    [JsonIgnore]
    public abstract ArchiveKind Kind { get; }

    /// <summary>What list rows show.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(NamePure) ? Name : NamePure;

    public override string ToString() => Name;
}

/// <summary>A <c>modtype = 3</c> entry, with the terrain metadata the cache carries.</summary>
public sealed record MapArchive : ArchiveEntry
{
    [JsonIgnore]
    public override ArchiveKind Kind => ArchiveKind.Map;

    /// <summary>The .smf inside the archive, e.g. <c>maps/Adamantium_Factory_V1.smf</c>.</summary>
    public string? MapFile { get; init; }

    public double? MaxMetal { get; init; }

    public double? Gravity { get; init; }

    public double? TidalStrength { get; init; }

    public double? ExtractorRadius { get; init; }

    public double? MapHardness { get; init; }

    public bool? VoidWater { get; init; }

    public bool? VoidGround { get; init; }

    public bool? NotDeformable { get; init; }

    public bool? AutoShowMetal { get; init; }
}

/// <summary>A <c>modtype = 1</c> entry, i.e. a playable game such as BAR.</summary>
public sealed record GameArchive : ArchiveEntry
{
    [JsonIgnore]
    public override ArchiveKind Kind => ArchiveKind.Game;

    public string? Game { get; init; }

    public string? ShortGame { get; init; }

    public string? Url { get; init; }

    public string? Mutator { get; init; }

    public string[] Depends { get; init; } = Array.Empty<string>();
}

/// <summary>A <c>modtype = 5</c> entry, i.e. a Chobby build passed to <c>--menu</c>.</summary>
public sealed record MenuArchive : ArchiveEntry
{
    [JsonIgnore]
    public override ArchiveKind Kind => ArchiveKind.Menu;

    public string? Mutator { get; init; }

    public bool OnlyLocal { get; init; }

    public string[] Depends { get; init; } = Array.Empty<string>();
}

/// <summary>A <c>modtype = 4</c> entry, or any modtype this build does not model.</summary>
public sealed record OtherArchive : ArchiveEntry
{
    [JsonIgnore]
    public override ArchiveKind Kind => RawModType == 4 ? ArchiveKind.BaseContent : ArchiveKind.Unknown;

    /// <summary>The raw modtype, kept so an unrecognised value is still reportable.</summary>
    public int RawModType { get; init; }
}
