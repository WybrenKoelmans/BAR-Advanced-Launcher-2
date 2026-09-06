using System.Diagnostics;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;
using Xunit.Abstractions;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class ArchiveCacheParserTests
{
    private readonly ITestOutputHelper _output;

    public ArchiveCacheParserTests(ITestOutputHelper output) => _output = output;

    private static ArchiveCacheParser CreateParser() => new(new TestLogger<ArchiveCacheParser>());

    /// <summary>A cache with one entry of each modtype seen on a real install.</summary>
    private const string SampleCache = """
        local archiveCache = {
            internalver = 22,
            archives = {
                {
                    name = "adamantium_factory_v1.sd7",
                    path = "C:/bar/data/maps/",
                    modified = "1780776370",
                    checksum = "9fc7ad65",
                    archivedata = {
                        author = "[teh]Beherith (mysterme@gmail.com)",
                        description = "64 player metal map by [teh]Beherith",
                        mapfile = "maps/Adamantium_Factory_V1.smf",
                        modtype = 3,
                        name = "AcidicQuarry 5.17",
                        name_pure = "AcidicQuarry",
                        version = "5.17",
                        maxmetal = 4.9,
                        gravity = 100.0,
                        tidalstrength = 0.0,
                        extractorradius = 24.0,
                        maphardness = 200.0,
                        voidwater = false,
                    },
                },
                {
                    name = "BAR.sdd",
                    path = "C:/bar/data/games/",
                    archivedata = {
                        modtype = 1,
                        name = "Beyond All Reason $VERSION",
                        name_pure = "Beyond All Reason",
                        version = "$VERSION",
                        shortname = "BYAR",
                        game = "Beyond All Reason",
                        shortgame = "BAR",
                        url = "https://beyondallreason.info",
                        depend = { "Spring content v1", "Map Helper v1" },
                    },
                },
                {
                    name = "0c81da57.sdp",
                    path = "C:/bar/data/packages/",
                    archivedata = {
                        modtype = 5,
                        name = "BYAR Chobby test-4622-01d2b92",
                        name_pure = "BYAR Chobby",
                        shortname = "BYAR_CHOBBY",
                        version = "test-4622-01d2b92",
                        mutator = "Official",
                        onlylocal = true,
                        depend = { "Spring content v1" },
                    },
                },
                {
                    name = "springcontent.sdz",
                    path = "C:/bar/data/base/",
                    archivedata = {
                        modtype = 4,
                        name = "Spring content v1",
                        name_pure = "Spring content v1",
                    },
                },
                {
                    name = "from-the-future.sdz",
                    path = "C:/bar/data/base/",
                    archivedata = {
                        modtype = 99,
                        name = "Something New",
                        name_pure = "Something New",
                    },
                },
            },
        }

        return archiveCache
        """;

    private static ArchiveCacheStamp WriteSample(TempDirectory temp, string content = SampleCache)
    {
        string path = Path.Combine(temp.Path, "ArchiveCache22.lua");
        File.WriteAllText(path, content);
        var info = new FileInfo(path);
        return new ArchiveCacheStamp(path, info.Length, info.LastWriteTimeUtc);
    }

    [Fact]
    public async Task ParseAsync_BucketsEntriesByModtype()
    {
        using var temp = new TempDirectory();
        ArchiveIndex index = await CreateParser().ParseAsync(WriteSample(temp));

        Assert.Single(index.Maps);
        Assert.Single(index.Games);
        Assert.Single(index.Menus);
        Assert.Equal(2, index.Other.Count);
        Assert.Equal(5, index.TotalCount);
    }

    [Fact]
    public async Task ParseAsync_CapturesMapMetadata()
    {
        using var temp = new TempDirectory();
        ArchiveIndex index = await CreateParser().ParseAsync(WriteSample(temp));

        MapArchive map = Assert.Single(index.Maps);
        Assert.Equal("AcidicQuarry 5.17", map.Name);
        Assert.Equal("AcidicQuarry", map.NamePure);
        Assert.Equal("5.17", map.Version);
        Assert.Equal("maps/Adamantium_Factory_V1.smf", map.MapFile);
        Assert.Equal(4.9, map.MaxMetal!.Value, 3);
        Assert.Equal(100.0, map.Gravity!.Value, 3);
        Assert.Equal(200.0, map.MapHardness!.Value, 3);
        Assert.Equal(24.0, map.ExtractorRadius!.Value, 3);
        Assert.False(map.VoidWater);
        Assert.Null(map.VoidGround);
        Assert.Equal("adamantium_factory_v1.sd7", map.ArchiveFileName);
        Assert.Equal(ArchiveKind.Map, map.Kind);
    }

    // PLAN.md §2.3: a .sdd checkout really does carry the literal string $VERSION and
    // the engine resolves it. Anything that "fixes" it breaks the launch.
    [Fact]
    public async Task ParseAsync_KeepsTheLiteralVersionPlaceholder()
    {
        using var temp = new TempDirectory();
        ArchiveIndex index = await CreateParser().ParseAsync(WriteSample(temp));

        GameArchive game = Assert.Single(index.Games);
        Assert.Equal("Beyond All Reason $VERSION", game.Name);
        Assert.Equal("$VERSION", game.Version);
        Assert.Equal(new[] { "Spring content v1", "Map Helper v1" }, game.Depends);
    }

    [Fact]
    public async Task ParseAsync_ReadsMenuEntries()
    {
        using var temp = new TempDirectory();
        ArchiveIndex index = await CreateParser().ParseAsync(WriteSample(temp));

        MenuArchive menu = Assert.Single(index.Menus);
        Assert.Equal("BYAR Chobby test-4622-01d2b92", menu.Name);
        Assert.True(menu.OnlyLocal);
        Assert.Equal("Official", menu.Mutator);
    }

    // PLAN.md §9: an unknown modtype must be kept as "other", not thrown.
    [Fact]
    public async Task ParseAsync_TreatsAnUnknownModtypeAsOther()
    {
        using var temp = new TempDirectory();
        ArchiveIndex index = await CreateParser().ParseAsync(WriteSample(temp));

        OtherArchive unknown = index.Other.Single(o => o.RawModType == 99);
        Assert.Equal(ArchiveKind.Unknown, unknown.Kind);

        OtherArchive baseContent = index.Other.Single(o => o.RawModType == 4);
        Assert.Equal(ArchiveKind.BaseContent, baseContent.Kind);
    }

    [Fact]
    public async Task ParseAsync_ReturnsAnEmptyIndexOnMalformedLua()
    {
        using var temp = new TempDirectory();
        ArchiveCacheStamp stamp = WriteSample(temp, "local x = { this is not lua");

        ArchiveIndex index = await CreateParser().ParseAsync(stamp);

        Assert.Equal(0, index.TotalCount);
    }

    [Fact]
    public void FindNewestCache_PicksTheMostRecentlyWrittenLuaFile()
    {
        using var temp = new TempDirectory();
        string older = temp.File("ArchiveCache22.lua");
        string newer = temp.File("ArchiveCache23.lua");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        ArchiveCacheStamp? stamp = CreateParser().FindNewestCache(temp.Path);

        Assert.Equal(newer, stamp!.Path);
    }

    [Fact]
    public void FindNewestCache_ReturnsNullWhenThereIsNoCache()
    {
        using var temp = new TempDirectory();

        Assert.Null(CreateParser().FindNewestCache(temp.Path));
        Assert.Null(CreateParser().FindNewestCache(Path.Combine(temp.Path, "missing")));
    }

    /// <summary>
    /// Runs against the real 10 MB cache when this machine has one. It is the file the
    /// parser was written for, and the counts in PLAN.md §2.3 came from counting it.
    /// </summary>
    [SkippableFact]
    public async Task ParseAsync_ReadsTheRealArchiveCache()
    {
        string cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Beyond-All-Reason", "data", "cache");

        ArchiveCacheStamp? stamp = CreateParser().FindNewestCache(cacheFolder);
        Skip.If(stamp is null, "No Beyond All Reason installation on this machine.");

        var stopwatch = Stopwatch.StartNew();
        ArchiveIndex index = await CreateParser().ParseAsync(stamp!);
        stopwatch.Stop();

        _output.WriteLine(
            $"{stamp!.Path} ({stamp.Length / 1024d / 1024d:F1} MB) parsed in {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine(
            $"games={index.Games.Count} maps={index.Maps.Count} menus={index.Menus.Count} other={index.Other.Count}");

        Assert.NotEmpty(index.Maps);
        Assert.NotEmpty(index.Games);
        Assert.NotEmpty(index.Menus);

        // Every entry must carry the name a start script references.
        Assert.All(index.Maps, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        Assert.All(index.Games, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
    }
}
