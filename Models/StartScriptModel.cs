using System;
using System.Collections.Generic;
using System.Linq;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// A typed view over a <see cref="StartScriptDocument"/> (PLAN.md §5.7). Every property
/// reads and writes straight through to the underlying tree, so the form editor and the
/// raw editor are two views of one thing and neither can silently discard the other's
/// work. Nothing is cached: a section removed underneath this model disappears from it.
/// </summary>
public sealed class StartScriptModel
{
    public StartScriptModel(StartScriptDocument document)
    {
        Document = document;
    }

    public StartScriptModel()
        : this(new StartScriptDocument())
    {
    }

    public StartScriptDocument Document { get; }

    /// <summary>The <c>[game]</c> block. Created on first touch if the document lacks one.</summary>
    public StartScriptSection Game => Document.Game;

    // ---- [game] scalars -------------------------------------------------------

    public string? MapName
    {
        get => Game.GetString("mapname");
        set => Game.SetString("mapname", value);
    }

    /// <summary>
    /// The game archive. Note that <c>Beyond All Reason $VERSION</c> is a literal the
    /// engine resolves against an <c>.sdd</c> checkout — it is not a placeholder this app
    /// should expand (PLAN.md §2.3).
    /// </summary>
    public string? GameType
    {
        get => Game.GetString("gametype");
        set => Game.SetString("gametype", value);
    }

    public string? MyPlayerName
    {
        get => Game.GetString("myplayername");
        set => Game.SetString("myplayername", value);
    }

    public bool? IsHost
    {
        get => Game.GetBool("ishost");
        set => Game.SetBool("ishost", value);
    }

    public string? HostIp
    {
        get => Game.GetString("hostip");
        set => Game.SetString("hostip", value);
    }

    public int? HostPort
    {
        get => Game.GetInt("hostport");
        set => Game.SetInt("hostport", value);
    }

    /// <summary>0 = fixed, 1 = random, 2 = choose in game, 3 = choose before game.</summary>
    public int? StartPosType
    {
        get => Game.GetInt("startpostype");
        set => Game.SetInt("startpostype", value);
    }

    public bool? NoHelperAis
    {
        get => Game.GetBool("nohelperais");
        set => Game.SetBool("nohelperais", value);
    }

    public int? GameStartDelay
    {
        get => Game.GetInt("gamestartdelay");
        set => Game.SetInt("gamestartdelay", value);
    }

    // ---- collections ----------------------------------------------------------

    public IReadOnlyList<StartScriptPlayer> Players =>
        Game.IndexedSections("player").Select(t => new StartScriptPlayer(t.Index, t.Section)).ToArray();

    public IReadOnlyList<StartScriptAi> Ais =>
        Game.IndexedSections("ai").Select(t => new StartScriptAi(t.Index, t.Section)).ToArray();

    /// <summary>
    /// Teams, excluding ally teams: <c>allyteam0</c> would otherwise be read as team 0
    /// under a plain prefix match.
    /// </summary>
    public IReadOnlyList<StartScriptTeam> Teams =>
        Game.IndexedSections("team").Select(t => new StartScriptTeam(t.Index, t.Section)).ToArray();

    public IReadOnlyList<StartScriptAllyTeam> AllyTeams =>
        Game.IndexedSections("allyteam").Select(t => new StartScriptAllyTeam(t.Index, t.Section)).ToArray();

    /// <summary>The <c>[modoptions]</c> block as a live key/value view.</summary>
    public StartScriptModOptions ModOptions => new(Game.GetOrAddSection("modoptions"));

    // ---- mutation -------------------------------------------------------------

    public StartScriptPlayer AddPlayer(string name, int team)
    {
        int index = Game.NextIndex("player");
        var section = new StartScriptSection("player" + index);
        Game.Nodes.Add(section);

        var player = new StartScriptPlayer(index, section) { Name = name, Team = team };
        return player;
    }

    public StartScriptAi AddAi(string name, string shortName, int team, int hostPlayer = 0)
    {
        int index = Game.NextIndex("ai");
        var section = new StartScriptSection("ai" + index);
        Game.Nodes.Add(section);

        return new StartScriptAi(index, section)
        {
            Name = name,
            ShortName = shortName,
            Team = team,
            Host = hostPlayer,
        };
    }

    public StartScriptTeam AddTeam(int allyTeam, int teamLeader, string? side = null, string? rgbColor = null)
    {
        int index = Game.NextIndex("team");
        var section = new StartScriptSection("team" + index);
        Game.Nodes.Add(section);

        return new StartScriptTeam(index, section)
        {
            AllyTeam = allyTeam,
            TeamLeader = teamLeader,
            Side = side,
            RgbColor = rgbColor,
        };
    }

    public StartScriptAllyTeam AddAllyTeam(int numAllies = 0)
    {
        // "allyteam" and "team" share a suffix, so the next free index has to be taken
        // from the ally-team family specifically.
        int index = Game.NextIndex("allyteam");
        var section = new StartScriptSection("allyteam" + index);
        Game.Nodes.Add(section);

        return new StartScriptAllyTeam(index, section) { NumAllies = numAllies };
    }

    public bool Remove(StartScriptEntity entity) => Game.Nodes.Remove(entity.Section);
}

/// <summary>Shared base for the indexed <c>[playerN]</c>-style blocks.</summary>
public abstract class StartScriptEntity
{
    protected StartScriptEntity(int index, StartScriptSection section)
    {
        Index = index;
        Section = section;
    }

    /// <summary>The number in the section name, which is how the engine cross-references.</summary>
    public int Index { get; }

    public StartScriptSection Section { get; }
}

public sealed class StartScriptPlayer : StartScriptEntity
{
    public StartScriptPlayer(int index, StartScriptSection section)
        : base(index, section)
    {
    }

    public string? Name
    {
        get => Section.GetString("name");
        set => Section.SetString("name", value);
    }

    public int? Team
    {
        get => Section.GetInt("team");
        set => Section.SetInt("team", value);
    }

    public int? Rank
    {
        get => Section.GetInt("rank");
        set => Section.SetInt("rank", value);
    }

    public bool? Spectator
    {
        get => Section.GetBool("spectator");
        set => Section.SetBool("spectator", value);
    }

    public bool? IsFromDemo
    {
        get => Section.GetBool("isfromdemo");
        set => Section.SetBool("isfromdemo", value);
    }

    public override string ToString() => Name ?? "player" + Index;
}

public sealed class StartScriptAi : StartScriptEntity
{
    public StartScriptAi(int index, StartScriptSection section)
        : base(index, section)
    {
    }

    /// <summary>Display name, e.g. <c>BARb(1)</c>.</summary>
    public string? Name
    {
        get => Section.GetString("name");
        set => Section.SetString("name", value);
    }

    /// <summary>The AI's archive name, e.g. <c>BARb</c> or <c>SimpleAI</c>.</summary>
    public string? ShortName
    {
        get => Section.GetString("shortname");
        set => Section.SetString("shortname", value);
    }

    public string? Version
    {
        get => Section.GetString("version");
        set => Section.SetString("version", value);
    }

    public int? Team
    {
        get => Section.GetInt("team");
        set => Section.SetInt("team", value);
    }

    /// <summary>Index of the player whose machine runs this AI.</summary>
    public int? Host
    {
        get => Section.GetInt("host");
        set => Section.SetInt("host", value);
    }

    /// <summary>Per-AI options, which BARb and friends read.</summary>
    public StartScriptModOptions Options => new(Section.GetOrAddSection("options"));

    public override string ToString() => Name ?? ShortName ?? "ai" + Index;
}

public sealed class StartScriptTeam : StartScriptEntity
{
    public StartScriptTeam(int index, StartScriptSection section)
        : base(index, section)
    {
    }

    public int? AllyTeam
    {
        get => Section.GetInt("allyteam");
        set => Section.SetInt("allyteam", value);
    }

    /// <summary>Index of the player who controls this team.</summary>
    public int? TeamLeader
    {
        get => Section.GetInt("teamleader");
        set => Section.SetInt("teamleader", value);
    }

    /// <summary>Faction, e.g. <c>Armada</c> or <c>Cortex</c>.</summary>
    public string? Side
    {
        get => Section.GetString("side");
        set => Section.SetString("side", value);
    }

    /// <summary>Three space-separated floats, kept as written so it survives a round trip.</summary>
    public string? RgbColor
    {
        get => Section.GetString("rgbcolor");
        set => Section.SetString("rgbcolor", value);
    }

    public float? Handicap
    {
        get => Section.GetFloat("handicap");
        set => Section.SetFloat("handicap", value);
    }

    public override string ToString() => $"team{Index} (ally {AllyTeam?.ToString() ?? "?"})";
}

public sealed class StartScriptAllyTeam : StartScriptEntity
{
    public StartScriptAllyTeam(int index, StartScriptSection section)
        : base(index, section)
    {
    }

    public int? NumAllies
    {
        get => Section.GetInt("numallies");
        set => Section.SetInt("numallies", value);
    }

    public float? StartRectLeft
    {
        get => Section.GetFloat("startrectleft");
        set => Section.SetFloat("startrectleft", value);
    }

    public float? StartRectTop
    {
        get => Section.GetFloat("startrecttop");
        set => Section.SetFloat("startrecttop", value);
    }

    public float? StartRectRight
    {
        get => Section.GetFloat("startrectright");
        set => Section.SetFloat("startrectright", value);
    }

    public float? StartRectBottom
    {
        get => Section.GetFloat("startrectbottom");
        set => Section.SetFloat("startrectbottom", value);
    }

    public void SetStartRect(float left, float top, float right, float bottom)
    {
        StartRectLeft = left;
        StartRectTop = top;
        StartRectRight = right;
        StartRectBottom = bottom;
    }

    public override string ToString() => $"allyteam{Index}";
}

/// <summary>
/// A live key/value view over a <c>[modoptions]</c> or AI <c>[options]</c> block.
/// Values stay strings: mod options are engine-defined and typing them here would mean
/// guessing at a schema this app does not have.
/// </summary>
public sealed class StartScriptModOptions
{
    public StartScriptModOptions(StartScriptSection section) => Section = section;

    public StartScriptSection Section { get; }

    public int Count => Section.Values.Count();

    public string? this[string key]
    {
        get => Section.GetString(key);
        set => Section.SetString(key, value);
    }

    public IEnumerable<KeyValuePair<string, string>> Entries =>
        Section.Values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value));

    public bool Contains(string key) => Section.FindValue(key) is not null;

    public bool Remove(string key) => Section.RemoveValue(key);

    public void Clear() => Section.Nodes.RemoveAll(n => n is StartScriptValue);
}
