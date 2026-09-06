using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// Discovering the three log locations of PLAN.md §5.8, and matching a log to a past run.
/// </summary>
public sealed class InfologCatalogTests
{
    private static BarInstallation Install(TempDirectory temp) => new()
    {
        RootPath = temp.Path,
        Source = InstallationSource.ManualPick,
    };

    private static string Write(string path, string content, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Enumerate_FindsAllThreeSourcesNewestFirst()
    {
        using var temp = new TempDirectory();
        BarInstallation install = Install(temp);

        var baseTime = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        Write(install.RootInfologPath, "root", baseTime);
        Write(install.IsolatedInfologPath, "isolated", baseTime.AddHours(3));
        Write(Path.Combine(install.RotatedLogsPath, "20260901090000_infolog.txt"), "rotated", baseTime.AddHours(1));

        IReadOnlyList<InfologFile> found = InfologCatalog.Enumerate(install);

        Assert.Equal(3, found.Count);
        Assert.Equal(InfologSource.IsolatedRun, found[0].Source);
        Assert.Equal(InfologSource.Rotated, found[1].Source);
        Assert.Equal(InfologSource.InstallRoot, found[2].Source);
    }

    [Fact]
    public void Enumerate_ReturnsNothingWhenTheInstallHasNoLogsYet()
    {
        using var temp = new TempDirectory();

        Assert.Empty(InfologCatalog.Enumerate(Install(temp)));
    }

    [Fact]
    public void Enumerate_RecordsTheLengthAndSourceOfEachFile()
    {
        using var temp = new TempDirectory();
        BarInstallation install = Install(temp);
        Write(install.IsolatedInfologPath, "12345", DateTime.UtcNow);

        InfologFile file = Assert.Single(InfologCatalog.Enumerate(install));

        Assert.Equal(5, file.Length);
        Assert.Equal(InfologSource.IsolatedRun, file.Source);
    }

    /// <summary>
    /// A row must not state two different times for the same file.
    ///
    /// The timestamp in a rotated log's name disagrees with its write time — the engine has
    /// used two schemes and one of them is UTC — so the name is shown as a name and the
    /// time comes from the file system. A rotated row therefore carries the write time in
    /// its title and the file name in its subtitle, and neither repeats the other.
    /// </summary>
    [Fact]
    public void RotatedRow_ShowsTheWriteTimeOnceAndTheFileNameAsAName()
    {
        var file = new InfologFile
        {
            Path = @"C:\BAR\data\log\20260502123133_infolog.txt",
            Source = InfologSource.Rotated,
            Length = 2048,
            LastWriteUtc = new DateTime(2026, 5, 2, 12, 31, 33, DateTimeKind.Utc),
        };

        Assert.Equal(file.LastWriteText, file.DisplayName);
        Assert.Contains("20260502123133_infolog.txt", file.SubtitleText);
        Assert.DoesNotContain(file.LastWriteText, file.SubtitleText);
    }

    /// <summary>
    /// The live logs are worth picking for their role, not their time, so they are titled
    /// by role — and their subtitle then has to carry the time.
    /// </summary>
    [Fact]
    public void LiveRow_IsTitledByItsRoleAndCarriesTheTimeInItsSubtitle()
    {
        var file = new InfologFile
        {
            Path = @"C:\BAR\data\infolog.txt",
            Source = InfologSource.IsolatedRun,
            Length = 2048,
            LastWriteUtc = DateTime.UtcNow,
        };

        Assert.Contains("Current run", file.DisplayName);
        Assert.Contains(file.LastWriteText, file.SubtitleText);
    }

    // ---- matching a run to its log -------------------------------------------

    private static InfologFile Log(string name, DateTime lastWriteUtc) => new()
    {
        Path = name,
        Source = InfologSource.Rotated,
        Length = 1,
        LastWriteUtc = lastWriteUtc,
    };

    /// <summary>
    /// The engine's last write is its shutdown, so a run's log is the one whose write time
    /// lands closest to the exit.
    /// </summary>
    [Fact]
    public void Match_PicksTheLogWrittenClosestToTheExit()
    {
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset exit = start.AddMinutes(10);

        InfologFile[] files =
        {
            Log("earlier", start.AddMinutes(-30).UtcDateTime),
            Log("mine", exit.AddSeconds(-1).UtcDateTime),
            Log("later", exit.AddHours(2).UtcDateTime),
        };

        Assert.Equal("mine", InfologCatalog.Match(files, start, exit)?.Path);
    }

    [Fact]
    public void Match_IgnoresALogWrittenLongAfterTheRunEnded()
    {
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset exit = start.AddMinutes(5);

        InfologFile[] files = { Log("next-run", exit.AddMinutes(30).UtcDateTime) };

        Assert.Null(InfologCatalog.Match(files, start, exit));
    }

    /// <summary>
    /// The two timestamps come from different clocks — one from this process, one from the
    /// file system — so a log may appear to predate the launch slightly.
    /// </summary>
    [Fact]
    public void Match_ToleratesALogWrittenJustBeforeTheRecordedStart()
    {
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset exit = start.AddMinutes(1);

        InfologFile[] files = { Log("mine", start.AddSeconds(-2).UtcDateTime) };

        Assert.Equal("mine", InfologCatalog.Match(files, start, exit)?.Path);
    }

    /// <summary>
    /// With no recorded exit — the app was closed while the engine was up — the first log
    /// written after the launch is the best answer, because every later one is a run that
    /// came afterwards.
    /// </summary>
    [Fact]
    public void Match_WithNoExitTakesTheEarliestLogAfterTheStart()
    {
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        InfologFile[] files =
        {
            Log("before", start.AddHours(-1).UtcDateTime),
            Log("mine", start.AddMinutes(4).UtcDateTime),
            Log("after", start.AddMinutes(40).UtcDateTime),
        };

        Assert.Equal("mine", InfologCatalog.Match(files, start, null)?.Path);
    }

    [Fact]
    public void Match_ReturnsNullWhenThereAreNoLogsAtAll() =>
        Assert.Null(InfologCatalog.Match(Array.Empty<InfologFile>(), DateTimeOffset.Now, DateTimeOffset.Now));
}
