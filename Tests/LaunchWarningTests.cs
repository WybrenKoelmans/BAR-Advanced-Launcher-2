using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.ViewModels;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// The one combination the Launch page can produce that starts the engine and then
/// kills it: --isolation limits the archive scan to the write directory and the engine
/// folder, so a write-dir override pointing somewhere empty leaves the map unresolvable.
/// Seen for real — the engine ran for eight minutes showing
/// "Dependent archive "quicksilver remake 1.24" ... not found".
/// </summary>
public sealed class LaunchWarningTests
{
    [Fact]
    public void BuildWarning_FlagsAnIsolatedRunWhoseWriteDirectoryHasNoContent()
    {
        using var temp = new TempDirectory();

        string? warning = LaunchViewModel.BuildWarning(new LaunchProfile
        {
            UseIsolation = true,
            WriteDirectoryOverride = temp.Path,
        });

        Assert.NotNull(warning);
        Assert.Contains(temp.Path, warning);
    }

    [Fact]
    public void BuildWarning_SaysNothingWhenTheWriteDirectoryLooksLikeADataFolder()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "maps"));

        Assert.Null(LaunchViewModel.BuildWarning(new LaunchProfile
        {
            UseIsolation = true,
            WriteDirectoryOverride = temp.Path,
        }));
    }

    /// <summary>Without isolation the engine still sees the install, so it is fine.</summary>
    [Fact]
    public void BuildWarning_SaysNothingWhenIsolationIsOff()
    {
        using var temp = new TempDirectory();

        Assert.Null(LaunchViewModel.BuildWarning(new LaunchProfile
        {
            UseIsolation = false,
            WriteDirectoryOverride = temp.Path,
        }));
    }

    /// <summary>The default: no override at all, so the install's data folder is used.</summary>
    [Fact]
    public void BuildWarning_SaysNothingForTheDefaultProfile()
    {
        Assert.Null(LaunchViewModel.BuildWarning(new LaunchProfile { UseIsolation = true }));
    }
}
