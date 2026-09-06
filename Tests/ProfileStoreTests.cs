using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class ProfileStoreTests
{
    private static ProfileStore CreateStore(TempDirectory temp) =>
        new(new TestLogger<ProfileStore>(), Path.Combine(temp.Path, "profiles.json"));

    // PLAN.md §5.7: a fresh install must be able to launch the game with no setup.
    [Fact]
    public async Task LoadAsync_SeedsTheBuiltInChobbyProfileOnFirstRun()
    {
        using var temp = new TempDirectory();
        ProfileStore store = CreateStore(temp);

        await store.LoadAsync();

        LaunchProfile profile = Assert.Single(store.Profiles);
        Assert.Equal("Chobby (menu)", profile.Name);
        Assert.Equal(LaunchMode.Menu, profile.Mode);
        Assert.True(profile.IsBuiltIn);
        Assert.True(profile.IsDefault);
        Assert.Same(profile, store.Default);

        // The menu name is left unset so it resolves against whatever Chobby build the
        // machine actually has.
        Assert.Null(profile.MenuName);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAProfile()
    {
        using var temp = new TempDirectory();

        ProfileStore writer = CreateStore(temp);
        await writer.LoadAsync();
        await writer.SaveAsync(new LaunchProfile
        {
            Id = "custom",
            Name = "Headless autohost",
            Mode = LaunchMode.Headless,
            ScriptFileName = "host.txt",
            EngineName = "recoil_2026.07.04",
            ExtraArguments = "--nocolor",
            UseIsolation = false,
            UseSafeMode = true,
            OnlyLocal = true,
            WindowMode = EngineWindowMode.Windowed,
            WriteDirectoryOverride = @"C:ardata2",
            ConfigFilePath = @"C:arhost.cfg",
        });

        ProfileStore reader = CreateStore(temp);
        await reader.LoadAsync();

        LaunchProfile loaded = reader.Profiles.Single(p => p.Id == "custom");
        Assert.Equal("Headless autohost", loaded.Name);
        Assert.Equal(LaunchMode.Headless, loaded.Mode);
        Assert.Equal("host.txt", loaded.ScriptFileName);
        Assert.Equal("recoil_2026.07.04", loaded.EngineName);
        Assert.Equal("--nocolor", loaded.ExtraArguments);
        Assert.False(loaded.UseIsolation);
        Assert.True(loaded.UseSafeMode);
        Assert.True(loaded.OnlyLocal);
        Assert.Equal(EngineWindowMode.Windowed, loaded.WindowMode);
        Assert.Equal(@"C:ardata2", loaded.WriteDirectoryOverride);
        Assert.Equal(@"C:arhost.cfg", loaded.ConfigFilePath);
    }

    /// <summary>profiles.json is meant to be hand-editable, so modes are stored by name.</summary>
    [Fact]
    public async Task SaveAsync_WritesTheModeAsAName()
    {
        using var temp = new TempDirectory();
        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();
        await store.SaveAsync(new LaunchProfile { Id = "custom", Mode = LaunchMode.Dedicated });

        string json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "profiles.json"));

        Assert.Contains("\"Dedicated\"", json);
    }

    [Fact]
    public async Task SetDefaultAsync_MovesThePinFromTheBuiltInProfile()
    {
        using var temp = new TempDirectory();
        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();
        await store.SaveAsync(new LaunchProfile { Id = "custom", Name = "Mine" });

        await store.SetDefaultAsync("custom");

        Assert.Equal("custom", store.Default!.Id);
        Assert.Single(store.Profiles, p => p.IsDefault);
    }

    [Fact]
    public async Task DeleteAsync_RefusesToRemoveTheBuiltInProfile()
    {
        using var temp = new TempDirectory();
        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();

        Assert.False(await store.DeleteAsync("builtin-chobby"));
        Assert.Single(store.Profiles);
    }

    [Fact]
    public async Task DeleteAsync_RemovesACustomProfileAndRepinsTheDefault()
    {
        using var temp = new TempDirectory();
        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();
        await store.SaveAsync(new LaunchProfile { Id = "custom", Name = "Mine" });
        await store.SetDefaultAsync("custom");

        Assert.True(await store.DeleteAsync("custom"));

        Assert.Single(store.Profiles);
        Assert.Equal("builtin-chobby", store.Default!.Id);
    }

    /// <summary>The file is hand-editable, so it can arrive with no default or several.</summary>
    [Fact]
    public async Task LoadAsync_NormalisesAFileWithTwoDefaults()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "profiles.json");
        await File.WriteAllTextAsync(path, """
            [
              { "Id": "a", "Name": "A", "Mode": "Menu", "IsDefault": true },
              { "Id": "b", "Name": "B", "Mode": "Script", "IsDefault": true }
            ]
            """);

        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();

        Assert.Single(store.Profiles, p => p.IsDefault);
    }

    [Fact]
    public async Task LoadAsync_NormalisesAFileWithNoDefault()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "profiles.json");
        await File.WriteAllTextAsync(path, """
            [ { "Id": "a", "Name": "A", "Mode": "Menu", "IsDefault": false } ]
            """);

        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();

        Assert.Equal("a", store.Default!.Id);
        Assert.True(store.Default.IsDefault);
    }

    [Fact]
    public async Task LoadAsync_FallsBackToTheBuiltInSetOnCorruptJson()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "profiles.json"), "{ not an array");

        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();

        Assert.Equal("builtin-chobby", Assert.Single(store.Profiles).Id);
    }

    /// <summary>The window mode is an enum too, and profiles.json stays hand-editable.</summary>
    [Fact]
    public async Task SaveAsync_WritesTheWindowModeAsAName()
    {
        using var temp = new TempDirectory();
        ProfileStore store = CreateStore(temp);
        await store.LoadAsync();
        await store.SaveAsync(new LaunchProfile { Id = "custom", WindowMode = EngineWindowMode.Fullscreen });

        string json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "profiles.json"));

        Assert.Contains("\"Fullscreen\"", json);
    }
}
