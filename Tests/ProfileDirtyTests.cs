using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.ViewModels;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// The Launch page's dirty marker. Profile edits are held on the page until Save, so this
/// comparison decides whether Save and Reload are available — and, just as important,
/// whether an untouched profile is left alone.
/// </summary>
public sealed class ProfileDirtyTests
{
    private static LaunchProfile Stored() => new()
    {
        Id = "custom",
        Name = "Headless autohost",
        Mode = LaunchMode.Headless,
        ScriptFileName = "host.txt",
        MenuName = "BYAR Chobby test-4622-01d2b92",
        EngineName = "recoil_2026.07.04",
        ExtraArguments = "--nocolor",
        UseIsolation = false,
        UseSafeMode = true,
        OnlyLocal = true,
        UseInstallEnvironment = false,
        WindowMode = EngineWindowMode.Windowed,
        WriteDirectoryOverride = @"C:\bar\data2",
        ConfigFilePath = @"C:\bar\host.cfg",
    };

    [Fact]
    public void DescribesSameRun_IsTrueForAnUntouchedProfile()
    {
        LaunchProfile stored = Stored();

        Assert.True(LaunchViewModel.DescribesSameRun(stored, stored.Clone()));
    }

    /// <summary>
    /// Pinning a profile as the default rewrites <c>IsDefault</c>. If that counted as an
    /// edit, "Set as default" would leave the page looking unsaved.
    /// </summary>
    [Fact]
    public void DescribesSameRun_IgnoresIdentityFields()
    {
        LaunchProfile stored = Stored();
        LaunchProfile candidate = stored.Clone();

        candidate.Id = "different";
        candidate.Name = "Renamed";
        candidate.IsDefault = !stored.IsDefault;
        candidate.IsBuiltIn = !stored.IsBuiltIn;

        Assert.True(LaunchViewModel.DescribesSameRun(stored, candidate));
    }

    [Theory]
    [InlineData(nameof(LaunchProfile.Mode))]
    [InlineData(nameof(LaunchProfile.MenuName))]
    [InlineData(nameof(LaunchProfile.ScriptFileName))]
    [InlineData(nameof(LaunchProfile.EngineName))]
    [InlineData(nameof(LaunchProfile.ExtraArguments))]
    [InlineData(nameof(LaunchProfile.WriteDirectoryOverride))]
    [InlineData(nameof(LaunchProfile.ConfigFilePath))]
    [InlineData(nameof(LaunchProfile.UseIsolation))]
    [InlineData(nameof(LaunchProfile.UseSafeMode))]
    [InlineData(nameof(LaunchProfile.OnlyLocal))]
    [InlineData(nameof(LaunchProfile.UseInstallEnvironment))]
    [InlineData(nameof(LaunchProfile.WindowMode))]
    public void DescribesSameRun_IsFalseWhenAnEditableFieldChanges(string field)
    {
        LaunchProfile stored = Stored();
        LaunchProfile candidate = stored.Clone();

        switch (field)
        {
            case nameof(LaunchProfile.Mode):
                candidate.Mode = LaunchMode.Dedicated;
                break;
            case nameof(LaunchProfile.MenuName):
                candidate.MenuName = "BYAR Chobby test-4623";
                break;
            case nameof(LaunchProfile.ScriptFileName):
                candidate.ScriptFileName = "other.txt";
                break;
            case nameof(LaunchProfile.EngineName):
                candidate.EngineName = "development";
                break;
            case nameof(LaunchProfile.ExtraArguments):
                candidate.ExtraArguments = null;
                break;
            case nameof(LaunchProfile.WriteDirectoryOverride):
                candidate.WriteDirectoryOverride = @"C:\bar\data3";
                break;
            case nameof(LaunchProfile.ConfigFilePath):
                candidate.ConfigFilePath = null;
                break;
            case nameof(LaunchProfile.UseIsolation):
                candidate.UseIsolation = !stored.UseIsolation;
                break;
            case nameof(LaunchProfile.UseSafeMode):
                candidate.UseSafeMode = !stored.UseSafeMode;
                break;
            case nameof(LaunchProfile.OnlyLocal):
                candidate.OnlyLocal = !stored.OnlyLocal;
                break;
            case nameof(LaunchProfile.UseInstallEnvironment):
                candidate.UseInstallEnvironment = !stored.UseInstallEnvironment;
                break;
            case nameof(LaunchProfile.WindowMode):
                candidate.WindowMode = EngineWindowMode.Fullscreen;
                break;
            default:
                Assert.Fail("Unhandled field " + field);
                break;
        }

        Assert.False(LaunchViewModel.DescribesSameRun(stored, candidate));
    }

    /// <summary>
    /// Windows does not distinguish these, so re-picking the same folder through the
    /// browse button — which can hand back a different casing — must not look like an edit.
    /// </summary>
    [Fact]
    public void DescribesSameRun_TreatsPathsAndScriptNamesAsCaseInsensitive()
    {
        LaunchProfile stored = Stored();
        LaunchProfile candidate = stored.Clone();

        candidate.WriteDirectoryOverride = @"C:\BAR\Data2";
        candidate.ConfigFilePath = @"C:\BAR\HOST.CFG";
        candidate.ScriptFileName = "HOST.TXT";
        candidate.EngineName = "RECOIL_2026.07.04";

        Assert.True(LaunchViewModel.DescribesSameRun(stored, candidate));
    }

    /// <summary>
    /// Extra arguments reach the engine verbatim and can carry case-sensitive values, so
    /// unlike the paths above they are compared exactly.
    /// </summary>
    [Fact]
    public void DescribesSameRun_TreatsExtraArgumentsAsCaseSensitive()
    {
        LaunchProfile stored = Stored();
        LaunchProfile candidate = stored.Clone();

        candidate.ExtraArguments = "--NoColor";

        Assert.False(LaunchViewModel.DescribesSameRun(stored, candidate));
    }

    /// <summary>
    /// Blank boxes are stored as null. If null and "" compared unequal, an untouched
    /// profile with an empty box would show as edited the moment the page loaded it.
    /// </summary>
    [Fact]
    public void DescribesSameRun_TreatsUnsetOptionalFieldsConsistently()
    {
        LaunchProfile stored = new() { Id = "plain", Name = "Plain" };

        Assert.True(LaunchViewModel.DescribesSameRun(stored, stored.Clone()));
    }
}
