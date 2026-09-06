using System;
using System.Collections.Generic;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Builds start scripts from templates. The shapes here are copied from scripts that are
/// known to run on this machine (the old launcher's <c>vs_ai.txt</c> and <c>ai_ai.txt</c>)
/// rather than invented from the engine documentation, including the parts that look
/// redundant: <c>numusers</c> counts players *and* AIs, and the engine is fussy about it.
/// </summary>
public sealed class StartScriptFactory : IStartScriptFactory
{
    public const string OneVersusAi = "1v1-vs-ai";
    public const string AiVersusAi = "ai-vs-ai";
    public const string EmptySandbox = "empty-sandbox";
    public const string MultiplayerHost = "multiplayer-host";

    /// <summary>
    /// The literal the engine expands against an <c>.sdd</c> checkout. It must be written
    /// through unexpanded — PLAN.md §2.3 calls this out specifically.
    /// </summary>
    private const string DefaultGameType = "Beyond All Reason $VERSION";

    private const string DefaultMapName = "Quicksilver Remake 1.24";
    private const string DefaultPlayerName = "Player";
    private const string DefaultAiShortName = "BARb";
    private const string DefaultAiVersion = "stable";

    /// <summary>The colour every real script on this machine uses for both teams.</summary>
    private const string DefaultColor = "0.99609375 0.546875 0";

    public IReadOnlyList<StartScriptTemplate> Templates { get; } = new[]
    {
        new StartScriptTemplate(
            OneVersusAi,
            "1v1 vs AI",
            "You on Armada against one AI on Cortex."),
        new StartScriptTemplate(
            AiVersusAi,
            "AI vs AI (spectate)",
            "Two AIs fight; you watch as a spectator."),
        new StartScriptTemplate(
            EmptySandbox,
            "Empty sandbox",
            "You alone on the map, with no opponent."),
        new StartScriptTemplate(
            MultiplayerHost,
            "Local multiplayer host",
            "Hosts a two-player game on this machine."),
    };

    public string SuggestFileName(string templateId) => templateId switch
    {
        OneVersusAi => "vs_ai",
        AiVersusAi => "ai_vs_ai",
        MultiplayerHost => "host",
        _ => "sandbox",
    };

    public StartScriptModel Create(string templateId, StartScriptTemplateOptions? options = null)
    {
        options ??= new StartScriptTemplateOptions();

        return templateId switch
        {
            OneVersusAi => CreateOneVersusAi(options),
            AiVersusAi => CreateAiVersusAi(options),
            MultiplayerHost => CreateMultiplayerHost(options),

            // An unknown id is a stale template in the UI, not a reason to crash a create.
            _ => CreateEmptySandbox(options),
        };
    }

    private static StartScriptModel CreateOneVersusAi(StartScriptTemplateOptions options)
    {
        var model = new StartScriptModel();
        string playerName = PlayerName(options);

        StartScriptAllyTeam left = model.AddAllyTeam();
        left.SetStartRect(0f, 0f, 0.2f, 1f);

        StartScriptAllyTeam right = model.AddAllyTeam();
        right.SetStartRect(0.8f, 0f, 1f, 1f);

        model.AddTeam(allyTeam: 0, teamLeader: 0, side: "Armada", rgbColor: DefaultColor).Handicap = 0;
        model.AddTeam(allyTeam: 1, teamLeader: 0, side: "Cortex", rgbColor: DefaultColor).Handicap = 0;

        StartScriptPlayer player = model.AddPlayer(playerName, team: 0);
        player.Rank = 0;
        player.IsFromDemo = false;

        AddAi(model, options, team: 1, ordinal: 1);

        // The AI counts as a user but not as a player.
        WriteGameScalars(model, options, playerName, numPlayers: 1, numUsers: 2);
        return model;
    }

    private static StartScriptModel CreateAiVersusAi(StartScriptTemplateOptions options)
    {
        var model = new StartScriptModel();
        string playerName = PlayerName(options);

        StartScriptAllyTeam left = model.AddAllyTeam();
        left.SetStartRect(0f, 0f, 0.2f, 1f);

        StartScriptAllyTeam right = model.AddAllyTeam();
        right.SetStartRect(0.8f, 0f, 1f, 1f);

        // Both teams are led by player 0 — the spectator's machine hosts both AIs.
        model.AddTeam(allyTeam: 0, teamLeader: 0, side: "Armada", rgbColor: DefaultColor).Handicap = 0;
        model.AddTeam(allyTeam: 1, teamLeader: 0, side: "Cortex", rgbColor: DefaultColor).Handicap = 0;

        StartScriptPlayer player = model.AddPlayer(playerName, team: 0);
        player.Rank = 0;
        player.Spectator = true;
        player.IsFromDemo = false;

        AddAi(model, options, team: 0, ordinal: 1);
        AddAi(model, options, team: 1, ordinal: 2);

        WriteGameScalars(model, options, playerName, numPlayers: 1, numUsers: 3);
        return model;
    }

    private static StartScriptModel CreateEmptySandbox(StartScriptTemplateOptions options)
    {
        var model = new StartScriptModel();
        string playerName = PlayerName(options);

        model.AddAllyTeam();
        model.AddTeam(allyTeam: 0, teamLeader: 0, side: "Armada", rgbColor: DefaultColor).Handicap = 0;

        StartScriptPlayer player = model.AddPlayer(playerName, team: 0);
        player.Rank = 0;
        player.IsFromDemo = false;

        WriteGameScalars(model, options, playerName, numPlayers: 1, numUsers: 1);
        return model;
    }

    private static StartScriptModel CreateMultiplayerHost(StartScriptTemplateOptions options)
    {
        var model = new StartScriptModel();
        string playerName = PlayerName(options);

        StartScriptAllyTeam left = model.AddAllyTeam();
        left.SetStartRect(0f, 0f, 0.2f, 1f);

        StartScriptAllyTeam right = model.AddAllyTeam();
        right.SetStartRect(0.8f, 0f, 1f, 1f);

        model.AddTeam(allyTeam: 0, teamLeader: 0, side: "Armada", rgbColor: DefaultColor).Handicap = 0;

        // Team 1 is led by player 1, the one who connects.
        model.AddTeam(allyTeam: 1, teamLeader: 1, side: "Cortex", rgbColor: DefaultColor).Handicap = 0;

        StartScriptPlayer host = model.AddPlayer(playerName, team: 0);
        host.Rank = 0;
        host.IsFromDemo = false;

        StartScriptPlayer guest = model.AddPlayer("Player2", team: 1);
        guest.Rank = 0;
        guest.IsFromDemo = false;

        WriteGameScalars(model, options, playerName, numPlayers: 2, numUsers: 2);

        // Port 0 lets the engine choose; a host others connect to needs the real one.
        model.HostPort = 8452;
        return model;
    }

    private static void AddAi(StartScriptModel model, StartScriptTemplateOptions options, int team, int ordinal)
    {
        string shortName = string.IsNullOrWhiteSpace(options.AiShortName)
            ? DefaultAiShortName
            : options.AiShortName.Trim();

        string version = string.IsNullOrWhiteSpace(options.AiVersion)
            ? DefaultAiVersion
            : options.AiVersion.Trim();

        // "GOdless0.1(1)" is the naming the real scripts use: archive, version, ordinal.
        StartScriptAi ai = model.AddAi($"{shortName}{version}({ordinal})", shortName, team);
        ai.Version = version;
        ai.Section.SetBool("isfromdemo", false);
    }

    private static void WriteGameScalars(
        StartScriptModel model,
        StartScriptTemplateOptions options,
        string playerName,
        int numPlayers,
        int numUsers)
    {
        // An empty [modoptions] block: the engine expects the section to exist, and
        // touching it here is what creates it.
        _ = model.ModOptions;

        model.HostIp = "127.0.0.1";
        model.HostPort = 0;
        model.Game.SetInt("numplayers", numPlayers);
        model.StartPosType = 2;
        model.MapName = string.IsNullOrWhiteSpace(options.MapName) ? DefaultMapName : options.MapName.Trim();
        model.IsHost = true;
        model.Game.SetInt("numusers", numUsers);
        model.GameType = string.IsNullOrWhiteSpace(options.GameType) ? DefaultGameType : options.GameType.Trim();
        model.GameStartDelay = 0;
        model.MyPlayerName = playerName;
        model.NoHelperAis = false;
    }

    private static string PlayerName(StartScriptTemplateOptions options) =>
        string.IsNullOrWhiteSpace(options.PlayerName) ? DefaultPlayerName : options.PlayerName.Trim();
}
