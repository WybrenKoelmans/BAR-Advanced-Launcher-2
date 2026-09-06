using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class ArchiveCatalogTests
{
    private const string MinimalCache = """
        local archiveCache = {
            internalver = 22,
            archives = {
                {
                    name = "quicksilver.sd7",
                    path = "C:/bar/data/maps/",
                    archivedata = { modtype = 3, name = "Quicksilver Remake 1.24", name_pure = "Quicksilver Remake" },
                },
                {
                    name = "BAR.sdd",
                    path = "C:/bar/data/games/",
                    archivedata = { modtype = 1, name = "Beyond All Reason $VERSION" },
                },
            },
        }
        return archiveCache
        """;

    private static (ArchiveCatalog Catalog, CountingParser Parser) CreateCatalog(TempDirectory temp)
    {
        var parser = new CountingParser(new ArchiveCacheParser(new TestLogger<ArchiveCacheParser>()));
        var catalog = new ArchiveCatalog(
            new TestLogger<ArchiveCatalog>(),
            parser,
            Path.Combine(temp.Path, "index.json"));

        return (catalog, parser);
    }

    private static BarInstallation CreateInstall(TempDirectory temp, bool withCache = true)
    {
        temp.File("data", "engine", "recoil_2026.07.04", EngineBuild.SpringExe);

        if (withCache)
        {
            File.WriteAllText(Path.Combine(temp.Dir("data", "cache"), "ArchiveCache22.lua"), MinimalCache);
        }

        return new BarInstallation { RootPath = temp.Path, Source = InstallationSource.ManualPick };
    }

    [Fact]
    public async Task LoadAsync_ParsesTheCacheAndBucketsIt()
    {
        using var temp = new TempDirectory();
        (ArchiveCatalog catalog, _) = CreateCatalog(temp);

        ArchiveIndex index = await catalog.LoadAsync(CreateInstall(temp));

        Assert.Single(index.Maps);
        Assert.Single(index.Games);
        Assert.False(index.IsDegraded);
        Assert.Equal("Quicksilver Remake 1.24", index.Maps[0].Name);
    }

    // PLAN.md §5.4: "second start is instant" — the memoised index must be reused.
    [Fact]
    public async Task LoadAsync_ReusesTheMemoisedIndexWhenTheCacheIsUnchanged()
    {
        using var temp = new TempDirectory();
        BarInstallation install = CreateInstall(temp);

        (ArchiveCatalog first, CountingParser firstParser) = CreateCatalog(temp);
        await first.LoadAsync(install);
        Assert.Equal(1, firstParser.ParseCount);

        // A fresh catalog over the same index.json stands in for a second app start.
        (ArchiveCatalog second, CountingParser secondParser) = CreateCatalog(temp);
        ArchiveIndex index = await second.LoadAsync(install);

        Assert.Equal(0, secondParser.ParseCount);
        Assert.Single(index.Maps);
        Assert.Equal("Quicksilver Remake 1.24", index.Maps[0].Name);
    }

    [Fact]
    public async Task LoadAsync_ReparsesWhenTheCacheFileChanges()
    {
        using var temp = new TempDirectory();
        BarInstallation install = CreateInstall(temp);

        (ArchiveCatalog first, _) = CreateCatalog(temp);
        await first.LoadAsync(install);

        // The engine rewrites the cache after a content download.
        string cacheFile = Path.Combine(temp.Path, "data", "cache", "ArchiveCache22.lua");
        await File.WriteAllTextAsync(cacheFile, MinimalCache + "\n-- changed");
        File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddMinutes(1));

        (ArchiveCatalog second, CountingParser secondParser) = CreateCatalog(temp);
        await second.LoadAsync(install);

        Assert.Equal(1, secondParser.ParseCount);
    }

    [Fact]
    public async Task RefreshAsync_ReparsesEvenWhenTheCacheIsUnchanged()
    {
        using var temp = new TempDirectory();
        BarInstallation install = CreateInstall(temp);

        (ArchiveCatalog catalog, CountingParser parser) = CreateCatalog(temp);
        await catalog.LoadAsync(install);
        await catalog.RefreshAsync(install);

        Assert.Equal(2, parser.ParseCount);
    }

    // PLAN.md §5.4: with no cache at all the app stays usable, clearly marked.
    [Fact]
    public async Task LoadAsync_FallsBackToFilenamesWhenThereIsNoCache()
    {
        using var temp = new TempDirectory();
        BarInstallation install = CreateInstall(temp, withCache: false);
        temp.File("data", "maps", "quicksilver.sd7");
        temp.File("data", "maps", "quicksilver.sd7.md5.gz");
        temp.Dir("data", "games", "BAR.sdd");

        (ArchiveCatalog catalog, _) = CreateCatalog(temp);
        ArchiveIndex index = await catalog.LoadAsync(install);

        Assert.True(index.IsDegraded);
        Assert.Equal("quicksilver", Assert.Single(index.Maps).Name);
        Assert.Equal("BAR", Assert.Single(index.Games).Name);
    }

    // The .md5.gz sidecars are half the file count in data\maps and must not be listed.
    [Fact]
    public async Task LoadAsync_IgnoresMd5Sidecars()
    {
        using var temp = new TempDirectory();
        BarInstallation install = CreateInstall(temp);
        temp.File("data", "maps", "quicksilver.sd7");
        temp.File("data", "maps", "quicksilver.sd7.md5.gz");

        (ArchiveCatalog catalog, _) = CreateCatalog(temp);
        ArchiveIndex index = await catalog.LoadAsync(install);

        Assert.Empty(index.NotIndexedOnDisk);
    }

    [Fact]
    public async Task LoadAsync_FlagsArchivesOnDiskThatTheCacheDoesNotKnow()
    {
        using var temp = new TempDirectory();
        BarInstallation install = CreateInstall(temp);
        temp.File("data", "maps", "quicksilver.sd7");
        temp.File("data", "maps", "brand_new_map.sd7");

        (ArchiveCatalog catalog, _) = CreateCatalog(temp);
        ArchiveIndex index = await catalog.LoadAsync(install);

        Assert.Equal("brand_new_map.sd7", Path.GetFileName(Assert.Single(index.NotIndexedOnDisk)));
    }

    [Fact]
    public async Task LoadAsync_DoesNotMemoiseAFailedParse()
    {
        using var temp = new TempDirectory();
        BarInstallation install = CreateInstall(temp);
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "data", "cache", "ArchiveCache22.lua"),
            "not lua at all {{{");

        (ArchiveCatalog catalog, _) = CreateCatalog(temp);
        ArchiveIndex index = await catalog.LoadAsync(install);

        Assert.Equal(0, index.TotalCount);
        Assert.False(File.Exists(Path.Combine(temp.Path, "index.json")));
    }

    [Fact]
    public async Task LoadAsync_RaisesIndexChangedOnlyAfterIndexIsPublished()
    {
        using var temp = new TempDirectory();
        (ArchiveCatalog catalog, _) = CreateCatalog(temp);

        int observed = -1;
        catalog.IndexChanged += (_, _) => observed = catalog.Index.TotalCount;

        await catalog.LoadAsync(CreateInstall(temp));

        Assert.Equal(2, observed);
    }

    /// <summary>Counts parses so a test can prove the memoised index was used.</summary>
    private sealed class CountingParser : IArchiveCacheParser
    {
        private readonly IArchiveCacheParser _inner;

        public CountingParser(IArchiveCacheParser inner) => _inner = inner;

        public int ParseCount { get; private set; }

        public ArchiveCacheStamp? FindNewestCache(string cacheFolder) => _inner.FindNewestCache(cacheFolder);

        public Task<ArchiveIndex> ParseAsync(ArchiveCacheStamp stamp, CancellationToken cancellationToken = default)
        {
            ParseCount++;
            return _inner.ParseAsync(stamp, cancellationToken);
        }
    }
}
