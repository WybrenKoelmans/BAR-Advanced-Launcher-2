using System.Text;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

public sealed class StartScriptSerializerTests
{
    private static readonly StartScriptSerializer Serializer = new();

    private static StartScriptDocument Parse(string text)
    {
        StartScriptParseResult result = Serializer.Parse(text);
        Assert.True(result.Success, result.Error?.ToString());
        return result.Document;
    }

    /// <summary>
    /// The shape Chobby writes: no indentation, CRLF, sections before the scalars.
    /// </summary>
    private const string ChobbyStyle =
        "[game]\r\n" +
        "{\r\n" +
        "[allyteam0]\r\n" +
        "{\r\n" +
        "numallies=0;\r\n" +
        "}\r\n" +
        "mapname=Quicksilver Remake 1.24;\r\n" +
        "gametype=Beyond All Reason $VERSION;\r\n" +
        "}\r\n";

    [Fact]
    public void Parse_ReadsNestedSectionsAndScalars()
    {
        StartScriptDocument document = Parse(ChobbyStyle);

        StartScriptSection game = Assert.Single(document.Root.Sections);
        Assert.Equal("game", game.Name);
        Assert.Equal("Quicksilver Remake 1.24", game.GetString("mapname"));
        Assert.Equal(0, game.Section("allyteam0")!.GetInt("numallies"));
    }

    /// <summary>
    /// PLAN.md §2.3: <c>$VERSION</c> is a literal the engine resolves against an .sdd
    /// checkout. Anything that "helpfully" expanded it would break a dev launch.
    /// </summary>
    [Fact]
    public void Parse_KeepsTheVersionPlaceholderLiteral()
    {
        StartScriptDocument document = Parse(ChobbyStyle);

        Assert.Equal("Beyond All Reason $VERSION", document.Game.GetString("gametype"));
        Assert.Contains("$VERSION", Serializer.Write(document));
    }

    [Fact]
    public void Parse_KeepsAValueContainingSpaces()
    {
        StartScriptDocument document = Parse("[game]\n{\nrgbcolor=0.99609375 0.546875 0;\n}");

        Assert.Equal("0.99609375 0.546875 0", document.Game.GetString("rgbcolor"));
    }

    /// <summary>The old hand-written scripts use <c>Key = Value;</c> with spaces and caps.</summary>
    [Fact]
    public void Parse_ToleratesSpacesAroundEqualsAndMatchesKeysCaseInsensitively()
    {
        StartScriptDocument document = Parse("[Game]\n{\n\t[allyTeam0]\n\t{\n\t\tStartRectLeft = 0.125;\n\t}\n}");

        StartScriptSection ally = document.Game.Section("ALLYTEAM0")!;
        Assert.Equal(0.125f, ally.GetFloat("startrectleft"));
    }

    [Fact]
    public void Parse_AcceptsLfOnlyLineEndings()
    {
        Assert.Equal("Otago 1.43", Parse("[game]\n{\nmapname=Otago 1.43;\n}\n").Game.GetString("mapname"));
    }

    /// <summary>One of the real library files starts with a blank line.</summary>
    [Fact]
    public void Parse_IgnoresLeadingBlankLines()
    {
        Assert.NotNull(Parse("\r\n\r\n[game]\r\n{\r\nishost=1;\r\n}\r\n").FindGame());
    }

    [Fact]
    public void Parse_AcceptsAnEmptySectionBody()
    {
        StartScriptDocument document = Parse("[game]\n{\n[modoptions]\n{\n    \n}\n}");

        Assert.Empty(document.Game.Section("modoptions")!.Nodes);
    }

    [Fact]
    public void Parse_AcceptsAWholeBlockOnOneLine()
    {
        StartScriptDocument document = Parse("[game] { [team0] { side=Armada; } ishost=1; }");

        Assert.Equal("Armada", document.Game.Section("team0")!.GetString("side"));
        Assert.Equal(true, document.Game.GetBool("ishost"));
    }

    [Fact]
    public void Parse_TreatsAnEmptyFileAsAnEmptyDocument()
    {
        Assert.Empty(Parse("   ").Root.Nodes);
        Assert.True(Serializer.Parse(null).Success);
    }

    // ---- errors ---------------------------------------------------------------

    [Fact]
    public void Parse_ReportsTheLineOfAnUnclosedSection()
    {
        StartScriptParseResult result = Serializer.Parse("[game]\n{\nishost=1;\n");

        Assert.False(result.Success);
        Assert.Contains("[game] is never closed", result.Error!.Message);
    }

    [Fact]
    public void Parse_ReportsAKeyWithNoEquals()
    {
        StartScriptParseResult result = Serializer.Parse("[game]\n{\nishost;\n}");

        Assert.False(result.Success);
        Assert.Equal(3, result.Error!.Line);
        Assert.Contains("'ishost' is not followed by an '='", result.Error.Message);
    }

    [Fact]
    public void Parse_ReportsASectionHeaderWithNoBrace()
    {
        StartScriptParseResult result = Serializer.Parse("[game]\nishost=1;\n");

        Assert.False(result.Success);
        Assert.Contains("[game] is not followed by a '{'", result.Error!.Message);
    }

    [Fact]
    public void Parse_ReportsAStrayClosingBrace()
    {
        StartScriptParseResult result = Serializer.Parse("[game]\n{\n}\n}\n");

        Assert.False(result.Success);
        Assert.Contains("never opened", result.Error!.Message);
    }

    // ---- writing --------------------------------------------------------------

    [Fact]
    public void Write_IndentsNestedSectionsAndUsesCrlf()
    {
        string written = Serializer.Write(Parse("[game]\n{\n[team0]\n{\nside=Armada;\n}\n}"));

        Assert.Equal(
            "[game]\r\n{\r\n    [team0]\r\n    {\r\n        side=Armada;\r\n    }\r\n}\r\n",
            written);
    }

    [Fact]
    public void Write_PreservesDocumentOrder()
    {
        string written = Serializer.Write(Parse("[game]\n{\nb=2;\na=1;\n}"));

        Assert.True(written.IndexOf("b=2;", StringComparison.Ordinal)
                    < written.IndexOf("a=1;", StringComparison.Ordinal));
    }

    /// <summary>
    /// A comment in a hand-edited script must survive a save, or the app silently
    /// deletes the user's notes.
    /// </summary>
    [Fact]
    public void Write_KeepsComments()
    {
        string written = Serializer.Write(Parse("[game]\n{\n// the map we always test on\nmapname=Otago 1.43;\n}"));

        Assert.Contains("// the map we always test on", written);
    }

    // ---- round trip -----------------------------------------------------------

    /// <summary>
    /// PLAN.md §5.7: parse → model → write must reproduce a semantically identical file.
    /// Writing normalises indentation, so identity is checked on the second pass: once
    /// normalised, further round trips must not drift at all.
    /// </summary>
    [Theory]
    [InlineData(ChobbyStyle)]
    [InlineData("[game] { [team0] { side=Armada; } ishost=1; }")]
    [InlineData("[Game]\n{\n\t[allyTeam0]\n\t{\n\t\tStartRectLeft = 0.125;\n\t}\n}")]
    public void RoundTrip_IsStableAfterTheFirstNormalisation(string original)
    {
        string once = Serializer.Write(Parse(original));
        string twice = Serializer.Write(Parse(once));

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData(ChobbyStyle)]
    [InlineData("[game] { [team0] { side=Armada; } ishost=1; }")]
    public void RoundTrip_PreservesEveryKeyAndValue(string original)
    {
        StartScriptDocument first = Parse(original);
        StartScriptDocument second = Parse(Serializer.Write(first));

        Assert.Equal(Flatten(first), Flatten(second));
    }

    /// <summary>
    /// Every scalar in the document as <c>game/team0/side=Armada</c> lines. Comparing two
    /// of these is what "semantically identical" means here — it ignores layout but
    /// catches a dropped key, a changed value or a moved section.
    /// </summary>
    internal static string Flatten(StartScriptDocument document)
    {
        var builder = new StringBuilder();
        Flatten(builder, document.Root, string.Empty);
        return builder.ToString();
    }

    private static void Flatten(StringBuilder builder, StartScriptSection section, string path)
    {
        foreach (StartScriptNode node in section.Nodes)
        {
            switch (node)
            {
                case StartScriptValue value:
                    builder.Append(path).Append(value.Key.ToLowerInvariant()).Append('=')
                        .Append(value.Value).Append('\n');
                    break;

                case StartScriptSection child:
                    Flatten(builder, child, path + child.Name.ToLowerInvariant() + "/");
                    break;
            }
        }
    }
}
