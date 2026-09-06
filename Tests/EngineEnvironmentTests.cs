using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// pr-downloader reads its repository settings from the environment only. Without them
/// it falls back to the engine's built-in default, repos.springrts.com, which carries no
/// Beyond All Reason content — verified by running the install's own pr-downloader.exe
/// both ways: springfiles.springrts.com without, repos-cdn.beyondallreason.dev with.
/// </summary>
public sealed class EngineEnvironmentTests
{
    /// <summary>Shaped like the real data\config.json, trimmed to what is read.</summary>
    private const string RealShapedConfig = """
    {
      "title": "Beyond All Reason",
      "setups": [
        {
          "package": { "id": "manual-linux", "platform": "linux" },
          "env_variables": { "PRD_RAPID_REPO_MASTER": "https://linux.example/repos.gz" }
        },
        {
          "package": { "id": "manual-win", "platform": "win32" },
          "env_variables": {
            "PRD_HTTP_SEARCH_URL": "https://files-cdn.beyondallreason.dev/find",
            "PRD_RAPID_USE_STREAMER": "false",
            "PRD_RAPID_REPO_MASTER": "https://repos-cdn.beyondallreason.dev/repos.gz"
          }
        }
      ]
    }
    """;

    [Fact]
    public void Parse_TakesTheWindowsSetupsVariables()
    {
        IReadOnlyDictionary<string, string> variables = EngineEnvironment.Parse(RealShapedConfig);

        Assert.Equal("https://repos-cdn.beyondallreason.dev/repos.gz", variables["PRD_RAPID_REPO_MASTER"]);
        Assert.Equal("https://files-cdn.beyondallreason.dev/find", variables["PRD_HTTP_SEARCH_URL"]);
        Assert.Equal("false", variables["PRD_RAPID_USE_STREAMER"]);
    }

    /// <summary>A linux setup's repo master must never reach a Windows run.</summary>
    [Fact]
    public void Parse_IgnoresOtherPlatforms()
    {
        Assert.DoesNotContain(
            EngineEnvironment.Parse(RealShapedConfig).Values,
            v => v.Contains("linux.example"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "setups": [] }""")]
    [InlineData("""{ "setups": [ { "package": { "platform": "win32" } } ] }""")]
    [InlineData("""{ "setups": "not an array" }""")]
    public void Parse_ReturnsNothingForAConfigWithNoUsableSetup(string json) =>
        Assert.Empty(EngineEnvironment.Parse(json));

    /// <summary>
    /// A missing config is a hand-assembled install, not a failure: the launch goes ahead
    /// with the inherited environment rather than being blocked.
    /// </summary>
    [Fact]
    public void Resolve_ReturnsNothingWhenTheInstallHasNoLauncherConfig()
    {
        using var temp = new TempDirectory();
        var environment = new EngineEnvironment(new TestLogger<EngineEnvironment>());

        Assert.Empty(environment.Resolve(new BarInstallation
        {
            RootPath = temp.Path,
            Source = InstallationSource.ManualPick,
        }));
    }

    [Fact]
    public void Resolve_ReadsTheConfigFromTheInstallsDataFolder()
    {
        using var temp = new TempDirectory();
        var installation = new BarInstallation { RootPath = temp.Path, Source = InstallationSource.ManualPick };

        Directory.CreateDirectory(installation.DataPath);
        File.WriteAllText(EngineEnvironment.ConfigPath(installation), RealShapedConfig);

        var environment = new EngineEnvironment(new TestLogger<EngineEnvironment>());

        Assert.Equal(
            "https://repos-cdn.beyondallreason.dev/repos.gz",
            environment.Resolve(installation)["PRD_RAPID_REPO_MASTER"]);
    }

    /// <summary>Malformed JSON degrades to "no variables", never to a failed launch.</summary>
    [Fact]
    public void Resolve_SurvivesAnUnreadableConfig()
    {
        using var temp = new TempDirectory();
        var installation = new BarInstallation { RootPath = temp.Path, Source = InstallationSource.ManualPick };

        Directory.CreateDirectory(installation.DataPath);
        File.WriteAllText(EngineEnvironment.ConfigPath(installation), "{ this is not json");

        Assert.Empty(new EngineEnvironment(new TestLogger<EngineEnvironment>()).Resolve(installation));
    }

    /// <summary>
    /// Against the real install on this machine, so a change to the official launcher's
    /// config shape shows up here rather than as downloads quietly failing. Skips
    /// wherever that install is absent.
    /// </summary>
    [SkippableFact]
    public void Parse_FindsThePrdVariablesInTheRealLauncherConfig()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Beyond-All-Reason", "data", "config.json");

        Skip.IfNot(File.Exists(path), $"No launcher config at {path}.");

        IReadOnlyDictionary<string, string> variables = EngineEnvironment.Parse(File.ReadAllText(path));

        Assert.Contains("PRD_RAPID_REPO_MASTER", variables.Keys);
        Assert.Contains("beyondallreason", variables["PRD_RAPID_REPO_MASTER"]);
    }

    /// <summary>
    /// The wiring, separately from the parsing: the run only gets these when the profile
    /// asks for them and an install is resolved, and nothing else in the environment is
    /// disturbed.
    /// </summary>
    private sealed class StubEnvironment : IEngineEnvironment
    {
        public IReadOnlyDictionary<string, string> Resolve(BarInstallation installation) =>
            new Dictionary<string, string>
            {
                ["PRD_RAPID_REPO_MASTER"] = "https://repos-cdn.beyondallreason.dev/repos.gz",
            };
    }

    private static BarInstallation SomeInstall() =>
        new() { RootPath = @"C:\bar", Source = InstallationSource.ManualPick };

    [Fact]
    public void ApplyEnvironment_SetsTheVariablesOnTheRun()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo();

        LaunchService.ApplyEnvironment(
            startInfo, new LaunchProfile(), SomeInstall(), new StubEnvironment());

        Assert.Equal(
            "https://repos-cdn.beyondallreason.dev/repos.gz",
            startInfo.Environment["PRD_RAPID_REPO_MASTER"]);
    }

    /// <summary>A fresh profile opts in, which is what makes downloads work by default.</summary>
    [Fact]
    public void ApplyEnvironment_IsOnForANewProfile() =>
        Assert.True(new LaunchProfile().UseInstallEnvironment);

    [Fact]
    public void ApplyEnvironment_RespectsTheProfileTurningItOff()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo();

        LaunchService.ApplyEnvironment(
            startInfo,
            new LaunchProfile { UseInstallEnvironment = false },
            SomeInstall(),
            new StubEnvironment());

        Assert.DoesNotContain("PRD_RAPID_REPO_MASTER", startInfo.Environment.Keys);
    }

    [Fact]
    public void ApplyEnvironment_DoesNothingWithoutAResolvedInstall()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo();

        LaunchService.ApplyEnvironment(startInfo, new LaunchProfile(), null, new StubEnvironment());

        Assert.DoesNotContain("PRD_RAPID_REPO_MASTER", startInfo.Environment.Keys);
    }

    /// <summary>
    /// The inherited environment must survive: setting PRD_* must not wipe PATH and take
    /// the engine's own DLL resolution down with it.
    /// </summary>
    [Fact]
    public void ApplyEnvironment_LeavesTheInheritedEnvironmentAlone()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo();
        int before = startInfo.Environment.Count;

        LaunchService.ApplyEnvironment(
            startInfo, new LaunchProfile(), SomeInstall(), new StubEnvironment());

        Assert.Equal(before + 1, startInfo.Environment.Count);
        Assert.Contains("PATH", startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
