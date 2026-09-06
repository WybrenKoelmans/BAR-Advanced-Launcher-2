using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class SettingsServiceTests
{
    private static SettingsService CreateService(string path) =>
        new(new TestLogger<SettingsService>(), path);

    [Fact]
    public async Task LoadAsync_ReturnsDefaultsWhenThereIsNoFile()
    {
        using var temp = new TempDirectory();
        SettingsService service = CreateService(Path.Combine(temp.Path, "settings.json"));

        AppSettings settings = await service.LoadAsync();

        Assert.Null(settings.ActiveInstallPath);
        Assert.Empty(settings.KnownInstallPaths);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "settings.json");

        SettingsService writer = CreateService(path);
        await writer.LoadAsync();
        writer.Current.RememberInstall(@"C:\bar");
        writer.Current.SelectedEngineName = "recoil_2026.07.04";
        writer.Current.SelectedProfileId = "builtin-chobby";
        await writer.SaveAsync();

        SettingsService reader = CreateService(path);
        AppSettings loaded = await reader.LoadAsync();

        Assert.Equal(@"C:\bar", loaded.ActiveInstallPath);
        Assert.Equal(new[] { @"C:\bar" }, loaded.KnownInstallPaths);
        Assert.Equal("recoil_2026.07.04", loaded.SelectedEngineName);
        Assert.Equal("builtin-chobby", loaded.SelectedProfileId);
    }

    [Fact]
    public async Task LoadAsync_FallsBackToDefaultsOnCorruptJson()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        AppSettings settings = await CreateService(path).LoadAsync();

        Assert.Null(settings.ActiveInstallPath);
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTemporaryFileBehind()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "settings.json");

        SettingsService service = CreateService(path);
        await service.LoadAsync();
        service.Current.RememberInstall(@"C:\bar");
        await service.SaveAsync();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void RememberInstall_DoesNotDuplicateAPathThatDiffersOnlyByCase()
    {
        var settings = new AppSettings();

        settings.RememberInstall(@"C:\BAR");
        settings.RememberInstall(@"c:\bar");

        Assert.Single(settings.KnownInstallPaths);
        Assert.Equal(@"c:\bar", settings.ActiveInstallPath);
    }

    [Fact]
    public void RememberInstall_KeepsASecondDistinctInstall()
    {
        var settings = new AppSettings();

        settings.RememberInstall(@"C:\bar-stable");
        settings.RememberInstall(@"C:\bar-test");

        Assert.Equal(2, settings.KnownInstallPaths.Count);
        Assert.Equal(@"C:\bar-test", settings.ActiveInstallPath);
    }
}
