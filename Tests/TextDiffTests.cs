using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// The line diff behind "has this start script changed since the run" (PLAN.md §5.8).
/// </summary>
public sealed class TextDiffTests
{
    [Fact]
    public void Compare_ReportsIdenticalTextAsIdentical()
    {
        TextDiffResult diff = TextDiff.Compare("a\nb\nc", "a\nb\nc");

        Assert.True(diff.Identical);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
        Assert.All(diff.Lines, line => Assert.Equal(DiffKind.Unchanged, line.Kind));
    }

    /// <summary>
    /// The engine writes CRLF and the raw editor may not, so a line-ending difference must
    /// not read as an edit.
    /// </summary>
    [Fact]
    public void Compare_IgnoresLineEndingStyle()
    {
        Assert.True(TextDiff.Compare("a\r\nb\r\n", "a\nb\n").Identical);
        Assert.True(TextDiff.Compare("a\rb", "a\nb").Identical);
    }

    /// <summary>A file that ends with a newline is not different from one that does not.</summary>
    [Fact]
    public void Compare_IgnoresASingleTrailingNewline()
    {
        Assert.True(TextDiff.Compare("a\nb\n", "a\nb").Identical);

        // Two trailing newlines mean a real blank line, which is a difference.
        Assert.False(TextDiff.Compare("a\nb\n\n", "a\nb").Identical);
    }

    [Fact]
    public void Compare_FindsAnInsertion()
    {
        TextDiffResult diff = TextDiff.Compare("a\nc", "a\nb\nc");

        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Removed);

        DiffLine added = Assert.Single(diff.Lines, line => line.Kind == DiffKind.Added);
        Assert.Equal("b", added.Text);
        Assert.Equal(2, added.RightNumber);
        Assert.Null(added.LeftNumber);
    }

    [Fact]
    public void Compare_FindsADeletion()
    {
        TextDiffResult diff = TextDiff.Compare("a\nb\nc", "a\nc");

        Assert.Equal(0, diff.Added);
        Assert.Equal(1, diff.Removed);

        DiffLine removed = Assert.Single(diff.Lines, line => line.Kind == DiffKind.Removed);
        Assert.Equal("b", removed.Text);
        Assert.Equal(2, removed.LeftNumber);
        Assert.Null(removed.RightNumber);
    }

    /// <summary>
    /// The case this exists for: one value edited in a start script should show as one line
    /// out and one line in, not as a wholesale rewrite.
    /// </summary>
    [Fact]
    public void Compare_ShowsAnEditedValueAsOneLineEach()
    {
        const string before = "[game]\n{\nmapname=Quicksilver Remake 1.24;\nmyplayername=wybre;\n}";
        const string after = "[game]\n{\nmapname=Rosetta 1.4.4;\nmyplayername=wybre;\n}";

        TextDiffResult diff = TextDiff.Compare(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal("1 added, 1 removed", diff.SummaryText);
    }

    [Fact]
    public void Compare_TreatsNullAndEmptyAsNoLines()
    {
        Assert.True(TextDiff.Compare(null, null).Identical);
        Assert.True(TextDiff.Compare(null, "").Identical);
        Assert.Equal(2, TextDiff.Compare(null, "a\nb").Added);
        Assert.Equal(2, TextDiff.Compare("a\nb", null).Removed);
    }

    /// <summary>
    /// Above the cap the table stops being free, so the diff degrades to a wholesale
    /// replacement rather than allocating for something that is not a start script.
    /// </summary>
    [Fact]
    public void Compare_DegradesRatherThanAllocatingForHugeInput()
    {
        string huge = string.Join('\n', Enumerable.Range(0, TextDiff.MaxLines + 1));

        TextDiffResult diff = TextDiff.Compare(huge, "a");

        Assert.True(diff.WasTooLarge);
        Assert.Equal(1, diff.Added);
        Assert.Equal(TextDiff.MaxLines + 1, diff.Removed);
    }

    [Fact]
    public void DiffLine_MarksAddedAndRemovedLinesDistinctly()
    {
        Assert.Equal("+", new DiffLine(DiffKind.Added, "x", null, 1).Marker);
        Assert.Equal("−", new DiffLine(DiffKind.Removed, "x", 1, null).Marker);
        Assert.Equal(" ", new DiffLine(DiffKind.Unchanged, "x", 1, 1).Marker);
    }
}
