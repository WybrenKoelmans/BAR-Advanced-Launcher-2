using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// Chobby hands every download to the launcher socket named in sl-connection.json and has
/// no fallback while it believes one is listening — api_spring_launcher only stands the
/// connector down when the file's host and port are *missing*, not when connecting fails.
/// A file left by the official launcher therefore sends our runs' downloads to a dead
/// port. Observed in a real infolog: "[spring-launcher] Connecting to 127.0.0.1:65176",
/// the exact port in a file written half an hour earlier by the official launcher.
/// </summary>
public sealed class LauncherLinkTests
{
    private const string RealLinkFile =
        """{"_sl_address":"127.0.0.1","_sl_port":65176,"_sl_write_path":"C:\bar\data","_sl_launcher_version":"1.2988.0"}""";

    [Fact]
    public void DisableLauncherLink_MovesTheFileAsideAndKeepsItsContent()
    {
        using var temp = new TempDirectory();
        string link = Path.Combine(temp.Path, LaunchService.LauncherLinkFileName);
        File.WriteAllText(link, RealLinkFile);

        Assert.True(LaunchService.DisableLauncherLink(temp.Path, new TestLogger<LaunchService>()));

        Assert.False(File.Exists(link));

        // Renamed, not deleted: the change is reversible by hand.
        string disabled = Path.Combine(temp.Path, LaunchService.DisabledLauncherLinkFileName);
        Assert.Equal(RealLinkFile, File.ReadAllText(disabled));
    }

    /// <summary>The normal case once it has been moved once, and for a fresh install.</summary>
    [Fact]
    public void DisableLauncherLink_DoesNothingWhenThereIsNoLinkFile()
    {
        using var temp = new TempDirectory();

        Assert.False(LaunchService.DisableLauncherLink(temp.Path, new TestLogger<LaunchService>()));
    }

    /// <summary>
    /// The official launcher rewrites the file every start, so ours has to be able to
    /// move a new one aside over the top of the last one it kept.
    /// </summary>
    [Fact]
    public void DisableLauncherLink_OverwritesAnEarlierDisabledFile()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, LaunchService.DisabledLauncherLinkFileName), "stale");
        File.WriteAllText(Path.Combine(temp.Path, LaunchService.LauncherLinkFileName), RealLinkFile);

        Assert.True(LaunchService.DisableLauncherLink(temp.Path, new TestLogger<LaunchService>()));

        Assert.Equal(
            RealLinkFile,
            File.ReadAllText(Path.Combine(temp.Path, LaunchService.DisabledLauncherLinkFileName)));
    }

    /// <summary>A missing directory must not take the launch down with it.</summary>
    [Fact]
    public void DisableLauncherLink_SurvivesAWriteDirectoryThatDoesNotExist() =>
        Assert.False(LaunchService.DisableLauncherLink(
            Path.Combine(Path.GetTempPath(), "bar-launcher-no-such-dir"),
            new TestLogger<LaunchService>()));

    /// <summary>
    /// Chobby reads the link file relative to the write directory, so an overridden
    /// write-dir has to be the one that gets cleaned.
    /// </summary>
    [Fact]
    public void ResolveWriteDirectory_PrefersTheOverride()
    {
        var installation = new BarInstallation { RootPath = @"C:\bar", Source = InstallationSource.ManualPick };

        Assert.Equal(
            installation.DataPath,
            LaunchCommandBuilder.ResolveWriteDirectory(new LaunchProfile(), installation));

        Assert.Equal(
            @"C:\sandbox",
            LaunchCommandBuilder.ResolveWriteDirectory(
                new LaunchProfile { WriteDirectoryOverride = @"C:\sandbox" }, installation));
    }
}
