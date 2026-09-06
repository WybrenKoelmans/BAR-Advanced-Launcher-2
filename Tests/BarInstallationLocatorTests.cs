using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class BarInstallationLocatorTests
{
    private static BarInstallationLocator CreateLocator(AppSettings? settings = null) =>
        new(new TestLogger<BarInstallationLocator>(), new StubSettingsService(settings ?? new AppSettings()));

    [Fact]
    public void Validate_AcceptsAFolderWithAnEngineHoldingSpring()
    {
        using var temp = new TempDirectory();
        temp.File("data", "engine", "recoil_2026.07.04", EngineBuild.SpringExe);

        InstallationProbe probe = CreateLocator().Validate(temp.Path);

        Assert.True(probe.IsValid);
        Assert.Null(probe.Reason);
        Assert.Equal(temp.Path, probe.Installation!.RootPath);
    }

    [Fact]
    public void Validate_ExposesTheDataPathsRelativeToTheRoot()
    {
        using var temp = new TempDirectory();
        temp.File("data", "engine", "recoil_2026.07.04", EngineBuild.SpringExe);

        BarInstallation installation = CreateLocator().Validate(temp.Path).Installation!;

        Assert.Equal(Path.Combine(temp.Path, "data"), installation.DataPath);
        Assert.Equal(Path.Combine(temp.Path, "data", "maps"), installation.MapsPath);
        Assert.Equal(Path.Combine(temp.Path, "data", "infolog.txt"), installation.IsolatedInfologPath);
        Assert.Equal(Path.Combine(temp.Path, "infolog.txt"), installation.RootInfologPath);
        Assert.Equal(Path.Combine(temp.Path, "data", "_script.txt"), installation.ChobbyScriptPath);
    }

    // PLAN.md §5.1: a rejection has to say why. The old service returned a bare null.
    [Fact]
    public void Validate_ExplainsAMissingFolder()
    {
        InstallationProbe probe = CreateLocator().Validate(@"C:\definitely\not\here");

        Assert.False(probe.IsValid);
        Assert.Equal("the folder does not exist", probe.Reason);
    }

    [Fact]
    public void Validate_ExplainsAFolderWithNoEngineDirectory()
    {
        using var temp = new TempDirectory();

        InstallationProbe probe = CreateLocator().Validate(temp.Path);

        Assert.False(probe.IsValid);
        Assert.Contains(@"data\engine", probe.Reason);
    }

    [Fact]
    public void Validate_ExplainsAnEmptyEngineDirectory()
    {
        using var temp = new TempDirectory();
        temp.Dir("data", "engine");

        InstallationProbe probe = CreateLocator().Validate(temp.Path);

        Assert.False(probe.IsValid);
        Assert.Contains("empty", probe.Reason);
    }

    [Fact]
    public void Validate_RejectsAnEngineFolderWithNoSpringExecutable()
    {
        using var temp = new TempDirectory();
        temp.File("data", "engine", "broken", "readme.txt");

        InstallationProbe probe = CreateLocator().Validate(temp.Path);

        Assert.False(probe.IsValid);
        Assert.Contains(EngineBuild.SpringExe, probe.Reason);
    }

    [Fact]
    public void Validate_ExplainsAnEmptyPath()
    {
        InstallationProbe probe = CreateLocator().Validate("   ");

        Assert.False(probe.IsValid);
        Assert.Equal("the path is empty", probe.Reason);
    }

    [Fact]
    public async Task ProbeAllAsync_PutsTheRememberedPathFirst()
    {
        using var temp = new TempDirectory();
        temp.File("data", "engine", "recoil_2026.07.04", EngineBuild.SpringExe);

        var settings = new AppSettings { ActiveInstallPath = temp.Path };
        BarInstallationLocator locator = CreateLocator(settings);

        IReadOnlyList<InstallationProbe> probes = await locator.ProbeAllAsync();

        Assert.Equal(InstallationSource.Remembered, probes[0].Source);
        Assert.Equal(temp.Path, probes[0].Path);
        Assert.True(probes[0].IsValid);
    }

    [Fact]
    public async Task ProbeAllAsync_ProbesEachCandidateOnce()
    {
        IReadOnlyList<InstallationProbe> probes = await CreateLocator().ProbeAllAsync();

        string[] normalized = probes
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Path)))
            .ToArray();

        Assert.Equal(normalized.Length, normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public StubSettingsService(AppSettings settings) => Current = settings;

        public AppSettings Current { get; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
