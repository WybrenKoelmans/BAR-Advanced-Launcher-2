using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// Resuming a read from a cursor, which is what lets the viewer follow a log the engine
/// is still writing without re-scanning it from the start on every poll.
/// </summary>
public sealed class InfologTailTests
{
    private static readonly InfologParser Parser = new(new TestLogger<InfologParser>());

    private static InfologFile FileAt(string path) => new()
    {
        Path = path,
        Source = InfologSource.IsolatedRun,
        Length = new FileInfo(path).Length,
        LastWriteUtc = File.GetLastWriteTimeUtc(path),
    };

    [Fact]
    public async Task ReadAsync_LeavesACursorAtTheEndOfTheFile()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");
        await File.WriteAllTextAsync(path, "[t=00:00:00.0] first\n[t=00:00:01.0] second\n");

        InfologFile file = FileAt(path);
        InfologRead read = await Parser.ReadAsync(file);

        Assert.NotNull(read.Cursor);
        Assert.Equal(2, read.Cursor!.LineNumber);
        Assert.Equal(new FileInfo(path).Length, read.Cursor.Position);
    }

    [Fact]
    public async Task TailAsync_ReturnsOnlyWhatWasWrittenSinceTheCursor()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");
        await File.WriteAllTextAsync(path, "[t=00:00:00.0] first\n");

        InfologFile file = FileAt(path);
        InfologRead read = await Parser.ReadAsync(file);

        await File.AppendAllTextAsync(path, "[t=00:00:01.0] second\n[t=00:00:02.0] third\n");

        InfologTailRead tail = await Parser.TailAsync(file, InfologFilter.All, read.Cursor!);

        Assert.False(tail.Truncated);
        Assert.Null(tail.ReadError);
        Assert.Equal(2, tail.NewLines.Count);
        Assert.Equal("second", tail.NewLines[0].Message);
        Assert.Equal("third", tail.NewLines[1].Message);
        Assert.Equal(3, tail.Cursor.LineNumber);

        // Absolute line numbers continue from the first read rather than restarting.
        Assert.Equal(2, tail.NewLines[0].Number);
        Assert.Equal(3, tail.NewLines[1].Number);
    }

    [Fact]
    public async Task TailAsync_AppliesTheFilterToTheNewLinesOnly()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");
        await File.WriteAllTextAsync(path, "[t=00:00:00.0] chatter\n");

        InfologFile file = FileAt(path);
        InfologRead read = await Parser.ReadAsync(file, new InfologFilter { MinimumSeverity = InfologSeverity.Error });

        // Nothing matched the first read: an info line under an errors-only filter.
        Assert.Empty(read.Lines);

        await File.AppendAllTextAsync(
            path,
            "[t=00:00:01.0] Error: bad\n[t=00:00:02.0] more chatter\n");

        InfologTailRead tail = await Parser.TailAsync(
            file,
            new InfologFilter { MinimumSeverity = InfologSeverity.Error },
            read.Cursor!);

        Assert.Single(tail.NewLines);
        Assert.Equal("bad", tail.NewLines[0].Message);
        Assert.Equal(1, tail.NewMatchCount);

        // Both new lines were scanned even though only one matched.
        Assert.Equal(2, tail.NewLineCount);
    }

    /// <summary>
    /// A continuation line has no timestamp of its own and inherits one from the line
    /// before it. That still has to work when the line it inherits from was read before
    /// the cursor, in an earlier poll.
    /// </summary>
    [Fact]
    public async Task TailAsync_InheritsContextAcrossTheCursorForAContinuationLine()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");
        await File.WriteAllTextAsync(path, "[t=00:00:00.0][f=-000001] [VFS] Fatal: crash started\n");

        InfologFile file = FileAt(path);
        InfologRead read = await Parser.ReadAsync(file);

        await File.AppendAllTextAsync(path, "  continuation with no timestamp\n");

        InfologTailRead tail = await Parser.TailAsync(file, InfologFilter.All, read.Cursor!);

        InfologLine continuation = Assert.Single(tail.NewLines);
        Assert.True(continuation.IsContinuation);
        Assert.Equal(InfologSeverity.Fatal, continuation.Severity);
        Assert.Equal("VFS", continuation.Section);
    }

    [Fact]
    public async Task TailAsync_ReportsNothingNewWhenTheFileHasNotGrown()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");
        await File.WriteAllTextAsync(path, "[t=00:00:00.0] first\n");

        InfologFile file = FileAt(path);
        InfologRead read = await Parser.ReadAsync(file);

        InfologTailRead tail = await Parser.TailAsync(file, InfologFilter.All, read.Cursor!);

        Assert.Empty(tail.NewLines);
        Assert.False(tail.Truncated);
        Assert.Equal(read.Cursor!.Position, tail.Cursor.Position);
    }

    /// <summary>
    /// A file shorter than the cursor was rotated or truncated out from under the
    /// viewer, which a live tail cannot recover from — only a full reload can.
    /// </summary>
    [Fact]
    public async Task TailAsync_ReportsTruncatedWhenTheFileGotShorter()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");
        await File.WriteAllTextAsync(path, "[t=00:00:00.0] a long first run of text\n");

        InfologFile file = FileAt(path);
        InfologRead read = await Parser.ReadAsync(file);

        await File.WriteAllTextAsync(path, "[t=00:00:00.0] short\n");

        InfologTailRead tail = await Parser.TailAsync(file, InfologFilter.All, read.Cursor!);

        Assert.True(tail.Truncated);
        Assert.Empty(tail.NewLines);
    }
}
