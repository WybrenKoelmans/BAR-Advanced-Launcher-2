using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class EngineCatalogTests
{
    [Theory]
    [InlineData("recoil_2026.07.04", "2026-07-04")]
    [InlineData("recoil_2025.06.11", "2025-06-11")]
    [InlineData("development", null)]
    [InlineData("recoil_not-a-date", null)]
    [InlineData("trailing_", null)]
    [InlineData("nounderscore", null)]
    public void ParseNamedDate_ReadsTheDateOnlyFromADatedBuildName(string name, string? expected)
    {
        DateOnly? actual = EngineCatalog.ParseNamedDate(name);

        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), actual);
    }

    [Fact]
    public async Task DiscoverAsync_RecordsPerEngineCapabilities()
    {
        using var temp = new TempDirectory();

        // A local dev build: spring.exe and unitsync.dll only, as on a real machine.
        temp.File("data", "engine", "development", EngineBuild.SpringExe);
        temp.File("data", "engine", "development", EngineBuild.UnitsyncDll);

        // A stock release with every binary.
        foreach (string exe in new[]
                 {
                     EngineBuild.SpringExe,
                     EngineBuild.HeadlessExe,
                     EngineBuild.DedicatedExe,
                     EngineBuild.PrDownloaderExe,
                     EngineBuild.UnitsyncDll,
                 })
        {
            temp.File("data", "engine", "recoil_2026.07.04", exe);
        }

        var catalog = new EngineCatalog(new TestLogger<EngineCatalog>());
        var installation = new BarInstallation
        {
            RootPath = temp.Path,
            Source = InstallationSource.ManualPick,
        };

        IReadOnlyList<EngineBuild> engines = await catalog.DiscoverAsync(installation);

        Assert.Equal(2, engines.Count);

        EngineBuild development = engines.Single(e => e.Name == "development");
        Assert.True(development.Capabilities.HasSpring);
        Assert.True(development.Capabilities.HasUnitsync);
        Assert.False(development.Capabilities.HasHeadless);
        Assert.False(development.Capabilities.HasDedicated);
        Assert.False(development.Capabilities.HasPrDownloader);
        Assert.False(development.Capabilities.IsComplete);
        Assert.Contains(EngineBuild.HeadlessExe, development.Capabilities.MissingSummary);

        EngineBuild release = engines.Single(e => e.Name == "recoil_2026.07.04");
        Assert.True(release.Capabilities.IsComplete);
        Assert.Equal(string.Empty, release.Capabilities.MissingSummary);
        Assert.Equal(new DateOnly(2026, 7, 4), release.NamedDate);
    }

    [Fact]
    public async Task DiscoverAsync_SkipsAFolderWithNoEngineBinaries()
    {
        using var temp = new TempDirectory();
        temp.File("data", "engine", "real", EngineBuild.SpringExe);
        temp.File("data", "engine", "leftovers", "notes.txt");

        var catalog = new EngineCatalog(new TestLogger<EngineCatalog>());
        IReadOnlyList<EngineBuild> engines = await catalog.DiscoverAsync(new BarInstallation
        {
            RootPath = temp.Path,
            Source = InstallationSource.ManualPick,
        });

        Assert.Equal("real", Assert.Single(engines).Name);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsEmptyRatherThanThrowingWhenTheFolderIsMissing()
    {
        using var temp = new TempDirectory();

        var catalog = new EngineCatalog(new TestLogger<EngineCatalog>());
        IReadOnlyList<EngineBuild> engines = await catalog.DiscoverAsync(new BarInstallation
        {
            RootPath = temp.Path,
            Source = InstallationSource.ManualPick,
        });

        Assert.Empty(engines);
    }

    [Fact]
    public async Task DiscoverAsync_OrdersDatedBuildsNewestFirst()
    {
        using var temp = new TempDirectory();
        foreach (string name in new[] { "recoil_2025.06.11", "recoil_2026.07.04", "recoil_2026.06.12" })
        {
            temp.File("data", "engine", name, EngineBuild.SpringExe);
        }

        var catalog = new EngineCatalog(new TestLogger<EngineCatalog>());
        IReadOnlyList<EngineBuild> engines = await catalog.DiscoverAsync(new BarInstallation
        {
            RootPath = temp.Path,
            Source = InstallationSource.ManualPick,
        });

        Assert.Equal(
            new[] { "recoil_2026.07.04", "recoil_2026.06.12", "recoil_2025.06.11" },
            engines.Select(e => e.Name).ToArray());
    }
}
