using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class LaunchCommandBuilderTests
{
    private static EngineBuild CompleteEngine(string path = @"C:\bar\data\engine\recoil_2026.07.04") => new()
    {
        Name = "recoil_2026.07.04",
        Path = path,
        Capabilities = new EngineCapabilities(true, true, true, true, true),
    };

    /// <summary>A local dev build: spring.exe only, as on a real machine.</summary>
    private static EngineBuild DevelopmentEngine() => new()
    {
        Name = "development",
        Path = @"C:\bar\data\engine\development",
        Capabilities = new EngineCapabilities(
            HasSpring: true, HasHeadless: false, HasDedicated: false, HasPrDownloader: false, HasUnitsync: true),
    };

    private static BarInstallation Install(string root = @"C:\bar") =>
        new() { RootPath = root, Source = InstallationSource.ManualPick };

    [Fact]
    public void TryBuild_MenuMode_MatchesTheArgumentShapeTheOldLauncherUsed()
    {
        var profile = new LaunchProfile
        {
            Mode = LaunchMode.Menu,
            MenuName = "BYAR Chobby test-4622-01d2b92",
            UseIsolation = true,
        };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        Assert.Null(error);
        Assert.Equal(@"C:\bar\data\engine\recoil_2026.07.04\spring.exe", command!.ExecutablePath);
        Assert.Equal(
            new[] { "--write-dir", @"C:\bar\data", "--isolation", "--menu", "BYAR Chobby test-4622-01d2b92" },
            command.Arguments);
    }

    [Fact]
    public void TryBuild_ScriptMode_PassesTheScriptAsOnePositionalToken()
    {
        using var temp = new TempDirectory();
        string script = temp.File("vs_ai.txt");

        var profile = new LaunchProfile { Mode = LaunchMode.Script, UseIsolation = true };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine(), script);

        Assert.Null(error);
        Assert.Equal(new[] { "--write-dir", @"C:\bar\data", "--isolation", script }, command!.Arguments);
    }

    /// <summary>
    /// PLAN.md §2.4: a real infolog reads "Loading StartScript from: All" because a path
    /// was concatenated unquoted and the engine stopped at the first space. The path must
    /// survive as exactly one argument.
    /// </summary>
    [Fact]
    public void TryBuild_KeepsAPathWithSpacesAsASingleArgument()
    {
        using var temp = new TempDirectory();
        string script = temp.File("All That Glitters 1.2.txt");

        var profile = new LaunchProfile { Mode = LaunchMode.Script };

        (EngineCommandLine? command, _) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine(), script);

        Assert.Contains(script, command!.Arguments);
        Assert.Equal(script, command.Arguments[^1]);
    }

    [Fact]
    public void ToDisplayString_QuotesOnlyTheTokensThatNeedIt()
    {
        var command = new EngineCommandLine(
            @"C:\Program Files\bar\spring.exe",
            new[] { "--write-dir", @"C:\bar\data", "--isolation", "--menu", "BYAR Chobby test" });

        Assert.Equal(
            "\"C:\\Program Files\\bar\\spring.exe\" --write-dir C:\\bar\\data --isolation --menu \"BYAR Chobby test\"",
            command.ToDisplayString());
    }

    [Fact]
    public void TryBuild_OmitsIsolationWhenTheProfileTurnsItOff()
    {
        var profile = new LaunchProfile { Mode = LaunchMode.Menu, MenuName = "Chobby", UseIsolation = false };

        (EngineCommandLine? command, _) = LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        Assert.DoesNotContain("--isolation", command!.Arguments);
    }

    [Fact]
    public void TryBuild_UsesTheWriteDirectoryOverrideWhenSet()
    {
        var profile = new LaunchProfile
        {
            Mode = LaunchMode.Menu,
            MenuName = "Chobby",
            WriteDirectoryOverride = @"C:\bar\data2",
        };

        (EngineCommandLine? command, _) = LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        Assert.Equal(@"C:\bar\data2", command!.Arguments[1]);
    }

    [Theory]
    [InlineData(LaunchMode.Headless, "spring-headless.exe")]
    [InlineData(LaunchMode.Dedicated, "spring-dedicated.exe")]
    public void TryBuild_PicksTheExecutableTheModeNeeds(LaunchMode mode, string expectedExe)
    {
        using var temp = new TempDirectory();
        string script = temp.File("host.txt");

        var profile = new LaunchProfile { Mode = mode };

        (EngineCommandLine? command, _) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine(), script);

        Assert.EndsWith(expectedExe, command!.ExecutablePath);
    }

    // PLAN.md §2.2 / §5.3: greying out is driven by these flags, and a build that lacks
    // the binary must fail with an explanation rather than a missing-file crash.
    [Fact]
    public void TryBuild_RefusesHeadlessOnABuildWithoutIt()
    {
        using var temp = new TempDirectory();
        string script = temp.File("host.txt");

        var profile = new LaunchProfile { Mode = LaunchMode.Headless };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, Install(), DevelopmentEngine(), script);

        Assert.Null(command);
        Assert.Contains("spring-headless.exe", error!.Message);
        Assert.Contains("development", error.Message);
    }

    [Fact]
    public void TryBuild_AllowsMenuModeOnADevelopmentBuild()
    {
        var profile = new LaunchProfile { Mode = LaunchMode.Menu, MenuName = "Chobby" };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, Install(), DevelopmentEngine());

        Assert.Null(error);
        Assert.EndsWith("spring.exe", command!.ExecutablePath);
    }

    [Fact]
    public void TryBuild_ExplainsAMissingMenuName()
    {
        var profile = new LaunchProfile { Mode = LaunchMode.Menu };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        Assert.Null(command);
        Assert.Contains("No menu selected", error!.Message);
    }

    [Fact]
    public void TryBuild_ExplainsAScriptThatDoesNotExist()
    {
        var profile = new LaunchProfile { Mode = LaunchMode.Script };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine(), @"C:\nope\missing.txt");

        Assert.Null(command);
        Assert.Contains("does not exist", error!.Message);
    }

    [Fact]
    public void TryBuild_AppendsExtraArgumentsAsSeparateTokens()
    {
        var profile = new LaunchProfile
        {
            Mode = LaunchMode.Menu,
            MenuName = "Chobby",
            ExtraArguments = "--nocolor --config \"C:\\my configs\\spring.cfg\"",
        };

        (EngineCommandLine? command, _) = LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        Assert.Equal(
            new[]
            {
                "--write-dir", @"C:\bar\data", "--isolation", "--menu", "Chobby",
                "--nocolor", "--config", @"C:\my configs\spring.cfg",
            },
            command!.Arguments);
    }

    // ---- run options ------------------------------------------------------------------

    [Fact]
    public void TryBuild_EmitsEveryRunOptionInAStableOrder()
    {
        var profile = new LaunchProfile
        {
            Mode = LaunchMode.Menu,
            MenuName = "Chobby",
            UseIsolation = true,
            UseSafeMode = true,
            OnlyLocal = true,
            WindowMode = EngineWindowMode.Windowed,
            ConfigFilePath = @"C:\bar\test springsettings.cfg",
        };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        Assert.Null(error);
        Assert.Equal(
            new[]
            {
                "--write-dir", @"C:\bar\data",
                "--config", @"C:\bar\test springsettings.cfg",
                "--isolation",
                "--only-local",
                "--safemode",
                "--window",
                "--menu", "Chobby",
            },
            command!.Arguments);
    }

    [Fact]
    public void TryBuild_KeepsAConfigPathWithSpacesAsOneToken()
    {
        var profile = new LaunchProfile
        {
            Mode = LaunchMode.Menu,
            MenuName = "Chobby",
            ConfigFilePath = @"C:\my configs\spring.cfg",
        };

        (EngineCommandLine? command, _) = LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        // PLAN.md §2.4 again: the token stays whole, and only ToDisplayString quotes it.
        Assert.Contains(@"C:\my configs\spring.cfg", command!.Arguments);
        Assert.Contains(@"""C:\my configs\spring.cfg""", command.ToDisplayString());
    }

    [Theory]
    [InlineData(EngineWindowMode.Default, null)]
    [InlineData(EngineWindowMode.Windowed, "--window")]
    [InlineData(EngineWindowMode.Fullscreen, "--fullscreen")]
    public void TryBuild_PassesTheWindowSwitchOnlyWhenOneIsChosen(EngineWindowMode mode, string? expected)
    {
        var profile = new LaunchProfile { Mode = LaunchMode.Menu, MenuName = "Chobby", WindowMode = mode };

        (EngineCommandLine? command, _) = LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        // Never both, and nothing at all for Default: the engine has no "use the config"
        // switch, so the absence of both is how that is expressed.
        string[] chosen = command!.Arguments.Where(a => a is "--window" or "--fullscreen").ToArray();

        Assert.Equal(expected is null ? Array.Empty<string>() : new[] { expected }, chosen);
    }

    /// <summary>
    /// spring-dedicated.exe registers only config, isolation and menu — its strings carry
    /// no window, fullscreen, safemode or only-local — and the engine refuses to start on
    /// an option it does not know. So the builder must drop them rather than pass them.
    /// </summary>
    [Fact]
    public void TryBuild_DropsTheClientSwitchesForDedicated()
    {
        using var temp = new TempDirectory();
        string script = temp.File("host.txt");

        var profile = new LaunchProfile
        {
            Mode = LaunchMode.Dedicated,
            UseSafeMode = true,
            OnlyLocal = true,
            WindowMode = EngineWindowMode.Fullscreen,
            ConfigFilePath = @"C:\bar\spring.cfg",
        };

        (EngineCommandLine? command, _) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine(), script);

        Assert.DoesNotContain("--safemode", command!.Arguments);
        Assert.DoesNotContain("--only-local", command.Arguments);
        Assert.DoesNotContain("--fullscreen", command.Arguments);

        // --config and --isolation it does understand, so those stay.
        Assert.Contains("--config", command.Arguments);
        Assert.Contains("--isolation", command.Arguments);
    }

    [Fact]
    public void TryBuild_KeepsTheClientSwitchesForHeadless()
    {
        using var temp = new TempDirectory();
        string script = temp.File("bot.txt");

        var profile = new LaunchProfile
        {
            Mode = LaunchMode.Headless,
            UseSafeMode = true,
            OnlyLocal = true,
        };

        (EngineCommandLine? command, _) =
            LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine(), script);

        Assert.Contains("--safemode", command!.Arguments);
        Assert.Contains("--only-local", command.Arguments);
    }

    [Fact]
    public void TryBuild_OmitsAConfigPathThatIsOnlyWhitespace()
    {
        var profile = new LaunchProfile { Mode = LaunchMode.Menu, MenuName = "Chobby", ConfigFilePath = "   " };

        (EngineCommandLine? command, _) = LaunchCommandBuilder.TryBuild(profile, Install(), CompleteEngine());

        Assert.DoesNotContain("--config", command!.Arguments);
    }
}

public sealed class CommandLineTokenizerTests
{
    [Fact]
    public void Split_ReturnsNothingForEmptyInput()
    {
        Assert.Empty(CommandLineTokenizer.Split(null));
        Assert.Empty(CommandLineTokenizer.Split("   "));
    }

    [Fact]
    public void Split_SplitsOnWhitespace()
    {
        Assert.Equal(new[] { "--a", "--b", "c" }, CommandLineTokenizer.Split("--a  --b\tc"));
    }

    [Fact]
    public void Split_KeepsAQuotedPathAsOneToken()
    {
        Assert.Equal(
            new[] { "--config", @"C:\my configs\spring.cfg" },
            CommandLineTokenizer.Split("--config \"C:\\my configs\\spring.cfg\""));
    }

    [Fact]
    public void Split_HandlesAQuotedSegmentJoinedToText()
    {
        Assert.Equal(
            new[] { @"--config=C:\my configs\spring.cfg" },
            CommandLineTokenizer.Split("--config=\"C:\\my configs\\spring.cfg\""));
    }

    [Fact]
    public void Split_KeepsAnExplicitlyEmptyArgument()
    {
        Assert.Equal(new[] { "--name", "" }, CommandLineTokenizer.Split("--name \"\""));
    }
}
