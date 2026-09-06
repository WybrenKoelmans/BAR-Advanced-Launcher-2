using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class StartScriptFactoryTests
{
    private static readonly StartScriptFactory Factory = new();
    private static readonly StartScriptSerializer Serializer = new();

    public static TheoryData<string> TemplateIds() => new()
    {
        StartScriptFactory.OneVersusAi,
        StartScriptFactory.AiVersusAi,
        StartScriptFactory.EmptySandbox,
        StartScriptFactory.MultiplayerHost,
    };

    [Fact]
    public void Templates_AreTheFourThePlanNames()
    {
        Assert.Equal(4, Factory.Templates.Count);
        Assert.All(Factory.Templates, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
        Assert.All(Factory.Templates, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    /// <summary>
    /// The point of a template is a script that runs, so the first thing to prove is that
    /// what the factory builds can be written and read back.
    /// </summary>
    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Create_ProducesAScriptThatParses(string templateId)
    {
        StartScriptModel model = Factory.Create(templateId);

        StartScriptParseResult reparsed = Serializer.Parse(Serializer.Write(model.Document));

        Assert.True(reparsed.Success, reparsed.Error?.ToString());
        Assert.NotNull(reparsed.Document.FindGame());
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Create_FillsInTheScalarsTheEngineNeeds(string templateId)
    {
        StartScriptModel model = Factory.Create(templateId);

        Assert.False(string.IsNullOrWhiteSpace(model.MapName));
        Assert.False(string.IsNullOrWhiteSpace(model.GameType));
        Assert.False(string.IsNullOrWhiteSpace(model.MyPlayerName));
        Assert.True(model.IsHost);
        Assert.NotNull(model.StartPosType);
    }

    /// <summary>
    /// Every template must be internally consistent: teams point at ally teams that
    /// exist, AIs and players point at teams that exist. A template that fails this
    /// produces a script the engine rejects with an unhelpful error.
    /// </summary>
    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Create_CrossReferencesResolve(string templateId)
    {
        StartScriptModel model = Factory.Create(templateId);

        int[] teamIndices = model.Teams.Select(t => t.Index).ToArray();
        int[] allyIndices = model.AllyTeams.Select(a => a.Index).ToArray();
        int[] playerIndices = model.Players.Select(p => p.Index).ToArray();

        Assert.NotEmpty(teamIndices);
        Assert.NotEmpty(allyIndices);
        Assert.NotEmpty(playerIndices);

        foreach (StartScriptTeam team in model.Teams)
        {
            Assert.Contains(team.AllyTeam!.Value, allyIndices);
            Assert.Contains(team.TeamLeader!.Value, playerIndices);
        }

        foreach (StartScriptPlayer player in model.Players)
        {
            Assert.Contains(player.Team!.Value, teamIndices);
        }

        foreach (StartScriptAi ai in model.Ais)
        {
            Assert.Contains(ai.Team!.Value, teamIndices);
            Assert.Contains(ai.Host!.Value, playerIndices);
        }
    }

    /// <summary>
    /// numusers counts players and AIs together — the engine is fussy about it, and the
    /// real scripts on this machine bear that out (1 player + 2 AIs = 3).
    /// </summary>
    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Create_CountsUsersAsPlayersPlusAis(string templateId)
    {
        StartScriptModel model = Factory.Create(templateId);

        Assert.Equal(model.Players.Count + model.Ais.Count, model.Game.GetInt("numusers"));
        Assert.Equal(model.Players.Count, model.Game.GetInt("numplayers"));
    }

    [Fact]
    public void OneVersusAi_PutsThePlayerAndTheAiOnOpposingAllyTeams()
    {
        StartScriptModel model = Factory.Create(StartScriptFactory.OneVersusAi);

        StartScriptPlayer player = Assert.Single(model.Players);
        StartScriptAi ai = Assert.Single(model.Ais);

        int playerAlly = model.Teams.Single(t => t.Index == player.Team).AllyTeam!.Value;
        int aiAlly = model.Teams.Single(t => t.Index == ai.Team).AllyTeam!.Value;

        Assert.NotEqual(playerAlly, aiAlly);
        Assert.NotEqual(true, player.Spectator);
    }

    [Fact]
    public void AiVersusAi_MakesThePlayerASpectatorAndFieldsTwoAis()
    {
        StartScriptModel model = Factory.Create(StartScriptFactory.AiVersusAi);

        Assert.True(Assert.Single(model.Players).Spectator);
        Assert.Equal(2, model.Ais.Count);
        Assert.NotEqual(model.Ais[0].Team, model.Ais[1].Team);
    }

    [Fact]
    public void EmptySandbox_HasNoOpponent()
    {
        StartScriptModel model = Factory.Create(StartScriptFactory.EmptySandbox);

        Assert.Empty(model.Ais);
        Assert.Single(model.Players);
        Assert.Single(model.Teams);
    }

    [Fact]
    public void MultiplayerHost_OpensARealPortForTheSecondPlayer()
    {
        StartScriptModel model = Factory.Create(StartScriptFactory.MultiplayerHost);

        Assert.Equal(2, model.Players.Count);
        Assert.NotEqual(0, model.HostPort);

        // The guest leads their own team, or they cannot be given one on connect.
        Assert.Contains(model.Teams, t => t.TeamLeader == 1);
    }

    // ---- options --------------------------------------------------------------

    [Fact]
    public void Create_UsesTheSuppliedMapGameAndPlayerName()
    {
        StartScriptModel model = Factory.Create(StartScriptFactory.OneVersusAi, new StartScriptTemplateOptions
        {
            MapName = "All That Glitters 1.2",
            GameType = "Beyond All Reason test-30519-3fbbc31",
            PlayerName = "wybre",
            AiShortName = "GOdless",
            AiVersion = "0.1",
        });

        Assert.Equal("All That Glitters 1.2", model.MapName);
        Assert.Equal("Beyond All Reason test-30519-3fbbc31", model.GameType);
        Assert.Equal("wybre", model.MyPlayerName);
        Assert.Equal("wybre", model.Players[0].Name);
        Assert.Equal("GOdless", model.Ais[0].ShortName);
        Assert.Equal("0.1", model.Ais[0].Version);

        // The naming convention the real scripts use: archive, version, ordinal.
        Assert.Equal("GOdless0.1(1)", model.Ais[0].Name);
    }

    /// <summary>PLAN.md §2.3: the default game type is a literal the engine expands itself.</summary>
    [Fact]
    public void Create_DefaultsToTheVersionLiteral()
    {
        Assert.Equal("Beyond All Reason $VERSION", Factory.Create(StartScriptFactory.OneVersusAi).GameType);
    }

    [Fact]
    public void Create_FallsBackToTheSandboxForAnUnknownTemplate()
    {
        StartScriptModel model = Factory.Create("no-such-template");

        Assert.Empty(model.Ais);
        Assert.NotNull(model.FindGameSafely());
    }

    [Fact]
    public void SuggestFileName_GivesEachTemplateItsOwnName()
    {
        string[] names = Factory.Templates.Select(t => Factory.SuggestFileName(t.Id)).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }
}

internal static class StartScriptModelTestExtensions
{
    /// <summary>Reads [game] without creating it, so a test can assert it was really built.</summary>
    public static StartScriptSection? FindGameSafely(this StartScriptModel model) => model.Document.FindGame();
}
