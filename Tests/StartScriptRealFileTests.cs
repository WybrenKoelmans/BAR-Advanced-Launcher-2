using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// PLAN.md Phase 4: "round-trip tested against the real <c>_script.txt</c> and
/// <c>bar_debug_launcher_script.txt</c> captured from this machine". These read the
/// actual files rather than fixtures, so the parser is measured against what the engine
/// and Chobby really write, not against what this app imagines they write. Each is
/// skipped where the file is absent, so the suite still passes on another machine.
///
/// Every test here is read-only. Nothing writes to either the engine's folder or the old
/// launcher's, both of which are reference material.
/// </summary>
public sealed class StartScriptRealFileTests
{
    private static readonly StartScriptSerializer Serializer = new();

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>What Chobby wrote, by way of the old launcher's library.</summary>
    private static string ChobbyCapturePath =>
        Path.Combine(LocalAppData, "BAR Advanced Launcher", "StartScripts", "_script.txt");

    /// <summary>What the engine's own debug launcher writes: four-space indented.</summary>
    private static string DebugLauncherPath =>
        Path.Combine(LocalAppData, "Programs", "Beyond-All-Reason", "bar_debug_launcher_script.txt");

    /// <summary>The script the engine last actually ran (PLAN.md §2.6).</summary>
    private static string LastRunPath =>
        Path.Combine(LocalAppData, "Programs", "Beyond-All-Reason", "data", "_script.txt");

    public static TheoryData<string> RealScripts() => new()
    {
        ChobbyCapturePath,
        DebugLauncherPath,
        LastRunPath,
    };

    [SkippableTheory]
    [MemberData(nameof(RealScripts))]
    public void RealScript_Parses(string path)
    {
        Skip.IfNot(File.Exists(path), $"{path} is not on this machine.");

        StartScriptParseResult result = Serializer.Parse(File.ReadAllText(path));

        Assert.True(result.Success, result.Error?.ToString());
        Assert.NotNull(result.Document.FindGame());
    }

    [SkippableTheory]
    [MemberData(nameof(RealScripts))]
    public void RealScript_RoundTripsWithoutLosingAnything(string path)
    {
        Skip.IfNot(File.Exists(path), $"{path} is not on this machine.");

        StartScriptParseResult first = Serializer.Parse(File.ReadAllText(path));
        Assert.True(first.Success, first.Error?.ToString());

        StartScriptParseResult second = Serializer.Parse(Serializer.Write(first.Document));
        Assert.True(second.Success, second.Error?.ToString());

        Assert.Equal(
            StartScriptSerializerTests.Flatten(first.Document),
            StartScriptSerializerTests.Flatten(second.Document));
    }

    [SkippableTheory]
    [MemberData(nameof(RealScripts))]
    public void RealScript_WriteIsIdempotentOnceNormalised(string path)
    {
        Skip.IfNot(File.Exists(path), $"{path} is not on this machine.");

        string once = Serializer.Write(Serializer.Parse(File.ReadAllText(path)).Document!);
        string twice = Serializer.Write(Serializer.Parse(once).Document!);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// The typed model has to find the same things in a real file that a reader would.
    /// This is the check that catches "allyteam0 was read as team 0".
    /// </summary>
    [SkippableFact]
    public void ChobbyCapture_ExposesItsTeamsThroughTheTypedModel()
    {
        Skip.IfNot(File.Exists(ChobbyCapturePath), $"{ChobbyCapturePath} is not on this machine.");

        var model = new StartScriptModel(Serializer.Parse(File.ReadAllText(ChobbyCapturePath)).Document!);

        Assert.NotEmpty(model.Teams);
        Assert.NotEmpty(model.AllyTeams);
        Assert.NotEmpty(model.Players);

        // An ally team must never appear as a team, whatever the file's ordering.
        Assert.DoesNotContain(model.Teams, t => t.Section.Name.StartsWith("allyteam", StringComparison.OrdinalIgnoreCase));

        // Every team points at an ally team that exists.
        foreach (StartScriptTeam team in model.Teams)
        {
            Assert.NotNull(team.AllyTeam);
            Assert.Contains(model.AllyTeams, a => a.Index == team.AllyTeam);
        }
    }

    [SkippableFact]
    public void DebugLauncherScript_KeepsTheVersionLiteralAndTheSpacedMapName()
    {
        Skip.IfNot(File.Exists(DebugLauncherPath), $"{DebugLauncherPath} is not on this machine.");

        var model = new StartScriptModel(Serializer.Parse(File.ReadAllText(DebugLauncherPath)).Document!);

        // The engine writes "Beyond All Reason $VERSION" and resolves it itself.
        Assert.Equal("Beyond All Reason $VERSION", model.GameType);

        // Map names contain spaces and dots; both must survive as one value.
        Assert.Equal("Quicksilver Remake 1.24", model.MapName);
    }
}
