using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class StartScriptModelTests
{
    private static StartScriptModel Model(string text) =>
        new(new StartScriptSerializer().Parse(text).Document!);

    /// <summary>
    /// "allyteam0" also starts with "team" once the prefix is stripped naively, which
    /// would put ally teams in the team list and break every count downstream.
    /// </summary>
    [Fact]
    public void Teams_DoesNotSwallowAllyTeams()
    {
        StartScriptModel model = Model(
            "[game]\n{\n[allyteam0]\n{\nnumallies=0;\n}\n[team0]\n{\nallyteam=0;\n}\n[allyteam1]\n{\nnumallies=0;\n}\n}");

        Assert.Single(model.Teams);
        Assert.Equal(2, model.AllyTeams.Count);
    }

    [Fact]
    public void Collections_AreOrderedByIndexNotByDocumentOrder()
    {
        // Chobby writes these in an arbitrary order; ai_ai.txt really does list ai1
        // before ai0.
        StartScriptModel model = Model("[game]\n{\n[ai1]\n{\nteam=1;\n}\n[ai0]\n{\nteam=0;\n}\n}");

        Assert.Equal(new[] { 0, 1 }, model.Ais.Select(a => a.Index));
    }

    [Fact]
    public void IndexedSections_ReadsDoubleDigitIndices()
    {
        StartScriptModel model = Model("[game]\n{\n[player10]\n{\nname=Ten;\n}\n[player2]\n{\nname=Two;\n}\n}");

        Assert.Equal(new[] { 2, 10 }, model.Players.Select(p => p.Index));
        Assert.Equal("Ten", model.Players.Last().Name);
    }

    /// <summary>A section that merely starts with the prefix is not a member of the family.</summary>
    [Fact]
    public void IndexedSections_IgnoresASectionWithNoNumber()
    {
        StartScriptModel model = Model("[game]\n{\n[teamsettings]\n{\nx=1;\n}\n[team0]\n{\nallyteam=0;\n}\n}");

        Assert.Single(model.Teams);
        Assert.Equal(0, model.Teams[0].Index);
    }

    [Fact]
    public void Scalars_ReadThroughToTheGameSection()
    {
        StartScriptModel model = Model(
            "[game]\n{\nmapname=Otago 1.43;\nishost=1;\nhostport=8452;\nnohelperais=0;\n}");

        Assert.Equal("Otago 1.43", model.MapName);
        Assert.True(model.IsHost);
        Assert.Equal(8452, model.HostPort);
        Assert.False(model.NoHelperAis);
    }

    [Fact]
    public void Scalars_WriteThroughToTheDocument()
    {
        StartScriptModel model = Model("[game]\n{\nmapname=Otago 1.43;\n}");

        model.MapName = "Quicksilver Remake 1.24";
        model.StartPosType = 2;

        Assert.Equal("Quicksilver Remake 1.24", model.Document.Game.GetString("mapname"));
        Assert.Equal("2", model.Document.Game.GetString("startpostype"));
    }

    /// <summary>A missing key reads as null rather than as a zero that looks deliberate.</summary>
    [Fact]
    public void Scalars_ReturnNullWhenAbsent()
    {
        StartScriptModel model = Model("[game]\n{\n}");

        Assert.Null(model.MapName);
        Assert.Null(model.HostPort);
        Assert.Null(model.IsHost);
    }

    [Fact]
    public void SettingAScalarToNullRemovesTheKey()
    {
        StartScriptModel model = Model("[game]\n{\nmapname=Otago 1.43;\n}");

        model.MapName = null;

        Assert.Null(model.Document.Game.FindValue("mapname"));
    }

    [Fact]
    public void GetBool_TreatsAnyNonZeroAsTrue()
    {
        StartScriptSection section = Model("[game]\n{\na=0;\nb=1;\nc=2;\n}").Game;

        Assert.False(section.GetBool("a"));
        Assert.True(section.GetBool("b"));
        Assert.True(section.GetBool("c"));
    }

    /// <summary>Floats are written invariantly: a comma here would produce an unloadable script.</summary>
    [Fact]
    public void SetFloat_WritesAnInvariantDecimalPoint()
    {
        StartScriptModel model = Model("[game]\n{\n}");

        model.AddAllyTeam().StartRectLeft = 0.72500002f;

        Assert.Contains(".", model.AllyTeams[0].Section.GetString("startrectleft"));
        Assert.DoesNotContain(",", model.AllyTeams[0].Section.GetString("startrectleft"));
    }

    // ---- mutation -------------------------------------------------------------

    [Fact]
    public void AddAllyTeam_TakesTheNextAllyTeamIndexNotTheNextTeamIndex()
    {
        StartScriptModel model = Model("[game]\n{\n[team0]\n{\n}\n[team1]\n{\n}\n[allyteam0]\n{\n}\n}");

        StartScriptAllyTeam added = model.AddAllyTeam();

        Assert.Equal(1, added.Index);
        Assert.Equal("allyteam1", added.Section.Name);
    }

    [Fact]
    public void AddPlayerAndAi_NumberSequentiallyFromWhatIsThere()
    {
        StartScriptModel model = Model("[game]\n{\n[player0]\n{\n}\n}");

        Assert.Equal(1, model.AddPlayer("Second", team: 1).Index);
        Assert.Equal(0, model.AddAi("BARbstable(1)", "BARb", team: 1).Index);
        Assert.Equal(1, model.AddAi("BARbstable(2)", "BARb", team: 2).Index);
    }

    [Fact]
    public void Remove_TakesAnEntityOutOfTheDocument()
    {
        StartScriptModel model = Model("[game]\n{\n[ai0]\n{\nshortname=BARb;\n}\n}");

        Assert.True(model.Remove(model.Ais[0]));
        Assert.Empty(model.Ais);
    }

    // ---- mod options ----------------------------------------------------------

    [Fact]
    public void ModOptions_ReadsAndWritesTheModOptionsBlock()
    {
        StartScriptModel model = Model("[game]\n{\n[modoptions]\n{\ndate_year=2026;\n}\n}");

        Assert.Equal("2026", model.ModOptions["date_year"]);

        model.ModOptions["easter_egg"] = "1";

        Assert.Equal("1", model.Document.Game.Section("modoptions")!.GetString("easter_egg"));
        Assert.Equal(2, model.ModOptions.Count);
    }

    [Fact]
    public void ModOptions_CreatesTheBlockWhenTheScriptHasNone()
    {
        StartScriptModel model = Model("[game]\n{\n}");

        model.ModOptions["numberofcommanders"] = "2";

        Assert.NotNull(model.Document.Game.Section("modoptions"));
    }

    /// <summary>Per-AI [options] must not be confused with the game-wide [modoptions].</summary>
    [Fact]
    public void AiOptions_AreSeparateFromModOptions()
    {
        StartScriptModel model = Model("[game]\n{\n[modoptions]\n{\na=1;\n}\n[ai0]\n{\nshortname=BARb;\n}\n}");

        model.Ais[0].Options["difficulty"] = "hard";

        Assert.Null(model.ModOptions["difficulty"]);
        Assert.Equal("hard", model.Ais[0].Section.Section("options")!.GetString("difficulty"));
    }
}
