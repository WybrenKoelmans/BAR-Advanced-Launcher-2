using System.Text;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// The facts of PLAN.md §5.8, extracted in one streaming pass. The fixtures below are
/// trimmed copies of real logs from this install, so a marker that changes upstream fails
/// here rather than silently reporting nothing.
/// </summary>
public sealed class InfologSummaryTests
{
    private static readonly InfologParser Parser = new(new TestLogger<InfologParser>());

    /// <summary>A successful isolated run, with every header line the engine writes.</summary>
    private const string GoodRun = """
        [t=00:00:00.021042] [DataDirLocater::FindWriteableDataDir] using writeable data-directory "C:/BAR/data/"
        [t=00:00:00.021049] Using writeable configuration source: "C:/BAR/data/springsettings.cfg"
        [t=00:00:00.028719]   Spring Engine Version: 2026.07.01-61-g680e33a verify-ed25519
        [t=00:00:00.028775]       Build Environment: msvc++ version 1951
        [t=00:00:00.251098] [DataDirLocater::Check] Isolation Mode!
        [t=00:00:00.251175] [DataDirLocater::FilterUsableDataDirs] using read-write data directory: C:/BAR/data/
        [t=00:00:00.251234] [DataDirLocater::FilterUsableDataDirs] using read-only data directory: C:/BAR/data/engine/recoil_2026.07.04/
        [t=00:00:02.813230][f=-000001] Error: [SetConfigInt] key "AdvSky" is deprecated
        [t=00:00:07.975827][f=-000001] Warning: something to look at
        [t=00:00:08.352410][f=-000001] infologVersionTags:engine=2026.07.01-61-g680e33a verify-ed25519,game=Beyond All Reason $VERSION,lobby=BYAR Chobby test-4627-9d085d9,map=Quicksilver Remake 1.24
        [t=00:01:12.100000][f=0001284] [Game] playing
        [t=00:01:22.365167] [SpringApp::Kill][8]
        [t=00:01:23.048706] [SpringApp::Kill][9]
        """;

    private static async Task<InfologSummary> SummariseAsync(TempDirectory temp, string content, string name = "infolog.txt")
    {
        string path = Path.Combine(temp.Path, name);
        await File.WriteAllTextAsync(path, content);

        var file = new InfologFile
        {
            Path = path,
            Source = InfologSource.IsolatedRun,
            Length = new FileInfo(path).Length,
            LastWriteUtc = File.GetLastWriteTimeUtc(path),
        };

        return await Parser.SummariseAsync(file);
    }

    [Fact]
    public async Task Summarise_ReadsTheEngineAndBuildBanner()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(temp, GoodRun);

        Assert.Equal("2026.07.01-61-g680e33a verify-ed25519", summary.EngineVersion);
        Assert.Equal("msvc++ version 1951", summary.BuildEnvironment);
    }

    [Fact]
    public async Task Summarise_ReadsTheWriteDirectoryAndConfigSourceWithoutTheirQuotes()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(temp, GoodRun);

        Assert.Equal("C:/BAR/data/", summary.WriteDirectory);
        Assert.Equal("C:/BAR/data/springsettings.cfg", summary.ConfigSource);
    }

    /// <summary>
    /// The log never names the binary it is running, so the engine folder has to come out
    /// of the read-only data directory it mounted.
    /// </summary>
    [Fact]
    public async Task Summarise_RecoversTheEngineFolderFromTheMountedDataDirectory()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(temp, GoodRun);

        Assert.Equal("recoil_2026.07.04", summary.EngineFolder);
        Assert.True(summary.IsIsolated);
    }

    [Fact]
    public async Task Summarise_SplitsTheVersionTagLine()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(temp, GoodRun);

        Assert.Equal("2026.07.01-61-g680e33a verify-ed25519", summary.TagEngine);
        Assert.Equal("Beyond All Reason $VERSION", summary.TagGame);
        Assert.Equal("BYAR Chobby test-4627-9d085d9", summary.TagLobby);
        Assert.Equal("Quicksilver Remake 1.24", summary.TagMap);
    }

    /// <summary>
    /// The tag values are free text. Splitting on commas would let a map name take the
    /// next field with it, so the line is sliced between the known keys instead.
    /// </summary>
    [Fact]
    public async Task Summarise_KeepsACommaInsideATagValue()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(
            temp,
            "[t=00:00:08.3][f=-000001] infologVersionTags:engine=2026.07.01,game=BAR $VERSION,lobby=Chobby,map=All That Glitters, Remake 1.2");

        Assert.Equal("All That Glitters, Remake 1.2", summary.TagMap);
        Assert.Equal("Chobby", summary.TagLobby);
        Assert.Equal("2026.07.01", summary.TagEngine);
    }

    [Fact]
    public async Task Summarise_CountsSeveritiesAndTracksTimeAndFrames()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(temp, GoodRun);

        Assert.Equal(1, summary.ErrorCount);
        Assert.Equal(1, summary.WarningCount);
        Assert.Equal(1284, summary.LastFrame);
        Assert.Equal(TimeSpan.Parse("00:01:23.048706"), summary.Duration);
        Assert.True(summary.ReachedShutdown);
        Assert.Equal(InfologOutcome.Completed, summary.Outcome);
    }

    [Fact]
    public async Task Summarise_ListsTheSectionsItSaw()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(temp, GoodRun);

        Assert.Contains("DataDirLocater::Check", summary.Sections);
        Assert.Contains("SpringApp::Kill", summary.Sections);
        Assert.DoesNotContain("SetConfigInt", summary.Sections);
    }

    /// <summary>
    /// A crash spreads its message over four lines, only the first of which carries a
    /// timestamp.
    /// </summary>
    [Fact]
    public async Task Summarise_ReadsAMultiLineCrashMessage()
    {
        using var temp = new TempDirectory();

        InfologSummary summary = await SummariseAsync(temp, """
            [t=00:00:27.527209][f=-000001] Fatal: [ExitSpringProcess] errorMsg="Spring has crashed:
              Access violation.

            A stacktrace has been written to:
              C:\BAR\data\infolog.txt" msgCaption="Spring: Unhandled exception" mainThread=1
            [t=00:00:27.527287][f=-000001] Error: [Watchdog::ClearTimer(id)] Invalid thread 4
            """);

        Assert.NotNull(summary.Failure);
        Assert.True(summary.Failure!.IsCrash);
        Assert.Equal("Spring has crashed:", summary.Failure.Headline);
        Assert.Contains("Access violation.", summary.Failure.Message);
        Assert.Equal("Spring: Unhandled exception", summary.Failure.Caption);
        Assert.Equal(@"C:\BAR\data\infolog.txt", summary.Failure.StacktracePath);
        Assert.Equal(1, summary.Failure.LineNumber);
        Assert.Equal(InfologOutcome.Failed, summary.Outcome);
    }

    /// <summary>
    /// A refused start puts the whole message on one line, and is not a crash — the
    /// distinction decides whether the report says "crashed" or "stopped with an error".
    /// </summary>
    [Fact]
    public async Task Summarise_ReadsASingleLineFailureAndDoesNotCallItACrash()
    {
        using var temp = new TempDirectory();

        InfologSummary summary = await SummariseAsync(
            temp,
            @"[t=00:00:01.2][f=-000001] Fatal: [ExitSpringProcess] errorMsg=""Dependent archive """" (resolved to """") not found"" msgCaption=""Spring: Fatal Error"" mainThread=1");

        Assert.NotNull(summary.Failure);
        Assert.False(summary.Failure!.IsCrash);
        Assert.Equal("Spring: Fatal Error", summary.Failure.Caption);
        Assert.Null(summary.Failure.StacktracePath);
    }

    /// <summary>
    /// The "from: All" case of PLAN.md §2.4, which is the whole reason the resolution state
    /// exists: an unquoted path truncated at the first space is logged as though it were
    /// whole. The same run ends with the engine refusing to start on it.
    /// </summary>
    [Fact]
    public async Task Summarise_MarksAStartScriptPathThatDoesNotExistAsUnresolved()
    {
        using var temp = new TempDirectory();

        InfologSummary summary = await SummariseAsync(temp, """
            [t=00:00:01.004996][f=-000001] [StartScript] Loading StartScript from: All
            [t=00:00:01.100000][f=-000001] Fatal: [ExitSpringProcess] errorMsg="Setup-script does not exist in given location: All" msgCaption="Spring: Fatal Error"
            """);

        Assert.Equal("All", summary.StartScriptPath);
        Assert.Equal(StartScriptResolution.Unresolved, summary.StartScriptState);
        Assert.NotNull(summary.Failure);
    }

    [Fact]
    public async Task Summarise_MarksAStartScriptPathThatExistsAsResolved()
    {
        using var temp = new TempDirectory();
        string script = Path.Combine(temp.Path, "sandbox 2.txt");
        await File.WriteAllTextAsync(script, "[game]\n{\n}\n");

        InfologSummary summary = await SummariseAsync(
            temp,
            $"[t=00:00:01.004996][f=-000001] [StartScript] Loading StartScript from: {script}");

        Assert.Equal(script, summary.StartScriptPath);
        Assert.Equal(StartScriptResolution.Resolved, summary.StartScriptState);
    }

    /// <summary>A <c>--menu</c> run logs no start-script line at all.</summary>
    [Fact]
    public async Task Summarise_ReportsNoStartScriptForAMenuRun()
    {
        using var temp = new TempDirectory();
        InfologSummary summary = await SummariseAsync(temp, GoodRun);

        Assert.Null(summary.StartScriptPath);
        Assert.Equal(StartScriptResolution.NotLogged, summary.StartScriptState);
    }

    /// <summary>
    /// One rotated log on this machine is not valid UTF-8. A viewer that refuses to open a
    /// log is worse than one that shows a replacement character in a translated name.
    /// </summary>
    [Fact]
    public async Task Summarise_ReadsALogThatIsNotValidUtf8()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "broken_infolog.txt");

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("[t=00:00:00.0] [i18n] name="));
        bytes.Add(0xC3);
        bytes.Add(0x28);
        bytes.AddRange(Encoding.UTF8.GetBytes("\r\n[t=00:00:01.0] Error: after the bad bytes\r\n"));
        await File.WriteAllBytesAsync(path, bytes.ToArray());

        var file = new InfologFile
        {
            Path = path,
            Source = InfologSource.Rotated,
            Length = bytes.Count,
            LastWriteUtc = File.GetLastWriteTimeUtc(path),
        };

        InfologSummary summary = await Parser.SummariseAsync(file);

        Assert.Null(summary.ReadError);
        Assert.Equal(2, summary.LineCount);
        Assert.Equal(1, summary.ErrorCount);
    }

    /// <summary>The engine holds its log open for the whole run, and that is the interesting one.</summary>
    [Fact]
    public async Task Summarise_ReadsALogThatIsStillOpenForWriting()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");

        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("[t=00:00:01.0] Error: still running\r\n"));
        await writer.FlushAsync();

        var file = new InfologFile
        {
            Path = path,
            Source = InfologSource.IsolatedRun,
            Length = 0,
            LastWriteUtc = DateTime.UtcNow,
        };

        InfologSummary summary = await Parser.SummariseAsync(file);

        Assert.Null(summary.ReadError);
        Assert.Equal(1, summary.ErrorCount);

        // Nothing said it shut down, so the outcome is unknown rather than clean.
        Assert.Equal(InfologOutcome.Unknown, summary.Outcome);
    }

    [Fact]
    public async Task Summarise_ReportsAMissingFileInsteadOfThrowing()
    {
        using var temp = new TempDirectory();

        var file = new InfologFile
        {
            Path = Path.Combine(temp.Path, "not-there.txt"),
            Source = InfologSource.Rotated,
            Length = 0,
            LastWriteUtc = DateTime.UtcNow,
        };

        InfologSummary summary = await Parser.SummariseAsync(file);

        Assert.NotNull(summary.ReadError);
        Assert.Equal(0, summary.LineCount);
    }

    // ---- reading -------------------------------------------------------------

    [Fact]
    public async Task Read_AppliesTheFilterWhileStreamingAndCountsWhatItSkipped()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "infolog.txt");
        await File.WriteAllTextAsync(path, GoodRun);

        var file = new InfologFile
        {
            Path = path,
            Source = InfologSource.IsolatedRun,
            Length = new FileInfo(path).Length,
            LastWriteUtc = File.GetLastWriteTimeUtc(path),
        };

        InfologRead all = await Parser.ReadAsync(file);
        InfologRead problems = await Parser.ReadAsync(
            file, new InfologFilter { MinimumSeverity = InfologSeverity.Warning });

        Assert.Equal(13, all.LineCount);
        Assert.Equal(13, all.MatchCount);
        Assert.False(all.Truncated);

        Assert.Equal(2, problems.MatchCount);
        Assert.Equal(13, problems.LineCount);
        Assert.All(problems.Lines, line => Assert.True(line.Severity >= InfologSeverity.Warning));
    }
}
