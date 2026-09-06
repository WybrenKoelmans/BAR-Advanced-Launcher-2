using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// Line splitting. Every input here is a verbatim line from a real infolog in this
/// install — the engine has no log format specification, so the only ground truth is what
/// it actually writes.
/// </summary>
public sealed class InfologLineTests
{
    private static InfologLine Parse(string raw, InfologLine? previous = null) =>
        InfologParser.ParseLine(previous is null ? 1 : previous.Number + 1, raw, previous);

    [Fact]
    public void Parse_SplitsTimestampFrameSectionAndMessage()
    {
        InfologLine line = Parse(
            @"[t=00:00:01.004996][f=-000001] [StartScript] Loading StartScript from: C:\scripts\sandbox.txt");

        Assert.Equal(TimeSpan.Parse("00:00:01.004996"), line.Time);
        Assert.Equal(-1, line.Frame);
        Assert.Equal("StartScript", line.Section);
        Assert.Equal(@"Loading StartScript from: C:\scripts\sandbox.txt", line.Message);
        Assert.Equal(InfologSeverity.Info, line.Severity);
        Assert.False(line.IsContinuation);
    }

    /// <summary>The earliest lines are written before the frame counter exists.</summary>
    [Fact]
    public void Parse_AcceptsALineWithNoFrameCounter()
    {
        InfologLine line = Parse("[t=00:00:00.021017] DetectCores: cpu mask ffff");

        Assert.Equal(TimeSpan.Parse("00:00:00.021017"), line.Time);
        Assert.Null(line.Frame);
        Assert.Null(line.Section);
        Assert.Equal("DetectCores: cpu mask ffff", line.Message);
    }

    /// <summary>
    /// A frame of -1 means "before the game started". It is kept as a value but not shown,
    /// because a column of -1 in a loading log is noise.
    /// </summary>
    [Fact]
    public void Parse_HidesThePreGameFrameButKeepsAPositiveOne()
    {
        Assert.Equal(string.Empty, Parse("[t=00:00:01.0][f=-000001] loading").FrameText);
        Assert.Equal("1284", Parse("[t=00:01:01.0][f=0001284] playing").FrameText);
    }

    [Theory]
    [InlineData(@"[t=00:00:02.813230][f=-000001] Error: [SetConfigInt] key ""AdvSky"" is deprecated",
        InfologSeverity.Error, null, @"[SetConfigInt] key ""AdvSky"" is deprecated")]
    [InlineData("[t=00:00:04.668354][f=-000001] [weapondefs.lua] Error: removed a weaponDef, missing model",
        InfologSeverity.Error, "weapondefs.lua", "removed a weaponDef, missing model")]
    // "Error:" is a label and is stripped; "Error in …" is part of the sentence and stays.
    [InlineData("[t=00:00:10.043249][f=-000001] Error in Initialize(): elements count cannot be <= 0",
        InfologSeverity.Error, null, "Error in Initialize(): elements count cannot be <= 0")]
    [InlineData("[t=00:00:07.9][f=-000001] Warning: something is off",
        InfologSeverity.Warning, null, "something is off")]
    // Only a tag in the leading slot is a section, so this one stays in the message. The
    // engine writes both orders, and treating a tag after the severity word as a section
    // would fill the filter with function names — [SetConfigInt] above is the same shape.
    [InlineData(@"[t=00:00:27.527209][f=-000001] Fatal: [ExitSpringProcess] errorMsg=""Abort"" msgCaption=""x""",
        InfologSeverity.Fatal, null, @"[ExitSpringProcess] errorMsg=""Abort"" msgCaption=""x""")]
    [InlineData("[t=00:00:22.361569] [VFS] [SpringVFS::DeleteArchives]",
        InfologSeverity.Info, "VFS", "[SpringVFS::DeleteArchives]")]
    public void Parse_RecoversSeverityAndSection(
        string raw,
        InfologSeverity expectedSeverity,
        string? expectedSection,
        string expectedMessage)
    {
        InfologLine line = Parse(raw);

        Assert.Equal(expectedSeverity, line.Severity);
        Assert.Equal(expectedSection, line.Section);
        Assert.Equal(expectedMessage, line.Message);
    }

    /// <summary>
    /// A Lua chunk name looks like a section tag but is not one. Taking it would fill the
    /// section filter with quoted file names.
    /// </summary>
    [Fact]
    public void Parse_DoesNotTakeAQuotedLuaChunkNameAsASection()
    {
        InfologLine line = Parse(
            @"[t=00:00:11.0][f=-000001] Error in DrawScreen(): [string ""LuaUI/Widgets/gui_chat.lua""]:2020: bad");

        Assert.Null(line.Section);
        Assert.Equal(InfologSeverity.Error, line.Severity);
    }

    /// <summary>
    /// The config dump, the log-section banner and the crash block all continue onto lines
    /// with no timestamp of their own.
    /// </summary>
    [Fact]
    public void Parse_TreatsATimestamplessLineAsAContinuationOfThePrevious()
    {
        InfologLine first = Parse("[t=00:00:00.023232][f=0000012] [Sound] Version: 1.1");
        InfologLine second = Parse("  AllowDeferredMapRendering = 1", first);

        Assert.True(second.IsContinuation);
        Assert.Equal(first.Time, second.Time);
        Assert.Equal(first.Frame, second.Frame);
        Assert.Equal(first.Section, second.Section);
        Assert.Equal("  AllowDeferredMapRendering = 1", second.Message);
    }

    /// <summary>
    /// Severity is inherited too, so filtering to errors shows a whole crash block rather
    /// than only the line that happened to carry the word "Fatal".
    /// </summary>
    [Fact]
    public void Parse_InheritsSeverityOntoAContinuation()
    {
        InfologLine fatal = Parse(@"[t=00:00:27.5][f=-000001] Fatal: [ExitSpringProcess] errorMsg=""Spring has crashed:");
        InfologLine detail = Parse("  Access violation.", fatal);

        Assert.Equal(InfologSeverity.Fatal, detail.Severity);
    }

    /// <summary>
    /// The live logs carry a UTF-8 BOM and the rotated ones do not. Left in place, it
    /// would stop the very first line of a log from parsing at all.
    /// </summary>
    [Fact]
    public void Parse_IgnoresAByteOrderMarkOnTheFirstLine()
    {
        InfologLine line = InfologParser.ParseLine(1, "\uFEFF[t=00:00:00.021017] DetectCores: cpu mask ffff", null);

        Assert.Equal(TimeSpan.Parse("00:00:00.021017"), line.Time);
        Assert.False(line.IsContinuation);
    }

    [Fact]
    public void Parse_KeepsTheRawLineExactly()
    {
        const string raw = "[t=00:00:01.0][f=-000001] [VFS] archive=x.sdd";

        Assert.Equal(raw, Parse(raw).Raw);
    }
}

/// <summary>The filter, which is applied while streaming rather than to a cached list.</summary>
public sealed class InfologFilterTests
{
    private static InfologLine Line(
        string raw,
        InfologSeverity severity = InfologSeverity.Info,
        string? section = null) =>
        new() { Number = 1, Raw = raw, Message = raw, Severity = severity, Section = section };

    [Fact]
    public void Matches_EverythingWhenTheFilterIsEmpty()
    {
        Assert.True(InfologParser.Matches(Line("anything"), InfologFilter.All));
        Assert.True(InfologFilter.All.IsEmpty);
    }

    [Fact]
    public void Matches_DropsLinesBelowTheMinimumSeverity()
    {
        var filter = new InfologFilter { MinimumSeverity = InfologSeverity.Error };

        Assert.False(InfologParser.Matches(Line("chatter"), filter));
        Assert.False(InfologParser.Matches(Line("odd", InfologSeverity.Warning), filter));
        Assert.True(InfologParser.Matches(Line("bad", InfologSeverity.Error), filter));
        Assert.True(InfologParser.Matches(Line("worse", InfologSeverity.Fatal), filter));
    }

    [Fact]
    public void Matches_ComparesTheSectionExactlyButIgnoringCase()
    {
        var filter = new InfologFilter { Section = "vfs" };

        Assert.True(InfologParser.Matches(Line("x", section: "VFS"), filter));
        Assert.False(InfologParser.Matches(Line("x", section: "Sound"), filter));
        Assert.False(InfologParser.Matches(Line("x"), filter));
    }

    /// <summary>
    /// Searched over the raw line, not the message: the severity word and the section tag
    /// are stripped out of the message, and someone typing "StartScript" means the tag.
    /// </summary>
    [Fact]
    public void Matches_SearchesTheRawLineSoATagIsFindable()
    {
        var line = new InfologLine
        {
            Number = 1,
            Raw = "[t=00:00:01.0] [StartScript] Loading StartScript from: x.txt",
            Message = "Loading StartScript from: x.txt",
            Section = "StartScript",
        };

        Assert.True(InfologParser.Matches(line, new InfologFilter { Search = "startscript" }));
        Assert.False(InfologParser.Matches(line, new InfologFilter { Search = "quicksilver" }));
    }
}
