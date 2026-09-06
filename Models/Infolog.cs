using System;
using System.Collections.Generic;
using System.Globalization;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>Which of the three log locations a file came from (PLAN.md §5.8).</summary>
public enum InfologSource
{
    /// <summary><c>data\infolog.txt</c> — the last isolated run, i.e. what this launcher produces.</summary>
    IsolatedRun,

    /// <summary><c>&lt;root&gt;\infolog.txt</c> — the last non-isolated run, i.e. the Electron launcher's.</summary>
    InstallRoot,

    /// <summary><c>data\log\*_infolog.txt</c> — rotated history.</summary>
    Rotated,
}

/// <summary>
/// One infolog on disk, built from a directory listing alone — nothing here required
/// opening the file.
///
/// That matters: this machine's <c>data\log</c> holds 773 files totalling 378 MB, the
/// largest of them 42 MB, so the picker has to be buildable without reading any of them.
/// </summary>
public sealed record InfologFile
{
    public required string Path { get; init; }

    public required InfologSource Source { get; init; }

    public required long Length { get; init; }

    /// <summary>
    /// Last write time — when the engine stopped writing, so in practice when the run
    /// ended. Both the ordering key and what the UI shows.
    ///
    /// The timestamp in a rotated file's *name* is deliberately not used for either. The
    /// engine has used two naming schemes and they disagree about the zone:
    /// <c>2026-05-02_15-18-20-753_infolog.txt</c> is a minute after its own last write in
    /// local time, while <c>20260502123133_infolog.txt</c> sits three hours behind its
    /// last write, i.e. UTC. Ordering on that would interleave the two eras wrongly, and
    /// showing it beside the write time puts two different times on one row. The file name
    /// is shown verbatim instead, which is what correlates a row with Explorer anyway.
    /// </summary>
    public required DateTime LastWriteUtc { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string SourceText => Source switch
    {
        InfologSource.IsolatedRun => "isolated run",
        InfologSource.InstallRoot => "install root",
        _ => "rotated",
    };

    /// <summary>
    /// What the picker shows.
    ///
    /// The two live logs are identified by their role, because that is what makes them
    /// worth picking — one is the run that just happened, the other is whatever the
    /// official launcher last did. A rotated log has no role, only a time.
    /// </summary>
    public string DisplayName => Source switch
    {
        InfologSource.IsolatedRun => "Current run (data\\infolog.txt)",
        InfologSource.InstallRoot => "Official launcher (infolog.txt)",
        _ => LastWriteText,
    };

    public string LastWriteText => LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string SizeText => Length switch
    {
        < 1024 => $"{Length} B",
        < 1024 * 1024 => $"{Length / 1024.0:0.#} KB",
        _ => $"{Length / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>
    /// Second line in the picker: whatever the title did not already say, plus the size.
    ///
    /// A rotated log's title is its write time, so its subtitle carries the file name; a
    /// live log's title is its role, so its subtitle carries the time. Neither row states
    /// the same fact twice.
    /// </summary>
    public string SubtitleText => Source == InfologSource.Rotated
        ? $"{SourceText} · {FileName} · {SizeText}"
        : $"{SourceText} · {LastWriteText} · {SizeText}";

    public override string ToString() => Path;
}

/// <summary>
/// How bad a log line is. The engine writes the severity into the message itself
/// (<c>Error:</c>, <c>Warning:</c>, <c>Fatal:</c>) rather than into a field of its own,
/// so this is recovered by the parser.
/// </summary>
public enum InfologSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Fatal = 3,
}

/// <summary>
/// One line of an infolog, split into the parts the engine actually writes:
///
/// <code>[t=00:00:01.004996][f=-000001] [StartScript] Loading StartScript from: C:\...\sandbox.txt</code>
///
/// Both bracketed prefixes are optional — early lines have no frame counter, and a
/// multi-line message (the crash block, the config dump) continues with neither.
/// </summary>
public sealed record InfologLine
{
    /// <summary>1-based line number in the file, so the UI can point at it.</summary>
    public required int Number { get; init; }

    /// <summary>The whole original line, for copying out verbatim.</summary>
    public required string Raw { get; init; }

    /// <summary>The message with the timestamp, frame and section prefixes removed.</summary>
    public required string Message { get; init; }

    /// <summary>Offset from process start, from <c>[t=…]</c>. Inherited on a continuation.</summary>
    public TimeSpan? Time { get; init; }

    /// <summary>
    /// Simulation frame from <c>[f=…]</c>. Negative (-1) before the game starts, which is
    /// how loading is told apart from play.
    /// </summary>
    public int? Frame { get; init; }

    /// <summary>The <c>[Section]</c> tag, e.g. <c>VFS</c>, <c>StartScript</c>, <c>Sound</c>.</summary>
    public string? Section { get; init; }

    public InfologSeverity Severity { get; init; }

    /// <summary>
    /// True when the line had no <c>[t=…]</c> of its own and belongs to the entry above
    /// it. It inherits that entry's time, frame and severity so a filtered view of a
    /// crash still shows the whole block rather than just its first line.
    /// </summary>
    public bool IsContinuation { get; init; }

    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);

    public string TimeText => Time is { } time
        ? time.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>Blank before the game starts: a frame of -1 is noise, not information.</summary>
    public string FrameText => Frame is { } frame && frame >= 0
        ? frame.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>
    /// Severity as a word, shown alongside the colour rather than instead of it — error
    /// and fatal share a colour, and colour alone is not something to depend on.
    /// </summary>
    public string SeverityText => Severity switch
    {
        InfologSeverity.Fatal => "fatal",
        InfologSeverity.Error => "error",
        InfologSeverity.Warning => "warn",
        _ => string.Empty,
    };

    public string SectionText => Section ?? string.Empty;

    public override string ToString() => Raw;
}

/// <summary>
/// The engine's own account of why it stopped, from its single <c>[ExitSpringProcess]</c>
/// line:
///
/// <code>Fatal: [ExitSpringProcess] errorMsg="Setup-script does not exist in given location: All" msgCaption="…"</code>
///
/// That example is the "<c>from: All</c>" bug of PLAN.md §2.4 seen from the other end: an
/// unquoted script path truncated at the first space, and this is the line that says the
/// run died of it. The same shape covers access violations, aborts, out-of-memory, and a
/// missing dependent archive.
/// </summary>
public sealed record InfologFailure
{
    public required string Message { get; init; }

    public string? Caption { get; init; }

    /// <summary>Line number of the failure, so the viewer can point at it.</summary>
    public required int LineNumber { get; init; }

    /// <summary>Where the engine says it wrote a stack trace, when it says so.</summary>
    public string? StacktracePath { get; init; }

    /// <summary>True for an access violation or abort, as opposed to a refused start.</summary>
    public bool IsCrash => Message.Contains("has crashed", StringComparison.OrdinalIgnoreCase)
                           || Message.Contains("abnormal termination", StringComparison.OrdinalIgnoreCase);

    /// <summary>First line of the message, which is the part worth putting in a header.</summary>
    public string Headline
    {
        get
        {
            int newline = Message.IndexOf('\n');
            return (newline < 0 ? Message : Message[..newline]).Trim();
        }
    }
}

/// <summary>Whether the start-script path in a log actually points at a file.</summary>
public enum StartScriptResolution
{
    /// <summary>The log has no <c>[StartScript]</c> line, which is what a <c>--menu</c> run looks like.</summary>
    NotLogged,

    /// <summary>The logged path exists.</summary>
    Resolved,

    /// <summary>
    /// The logged path does not exist. PLAN.md §2.4: a truncated path is logged as if it
    /// were real, so this must never be presented as a script.
    /// </summary>
    Unresolved,
}

/// <summary>How a run ended, as far as its log shows.</summary>
public enum InfologOutcome
{
    /// <summary>Not enough of the log to say — still running, or read short of the end.</summary>
    Unknown,

    /// <summary>Reached the engine's own shutdown sequence.</summary>
    Completed,

    /// <summary>Ended on an <c>[ExitSpringProcess]</c> line.</summary>
    Failed,
}

/// <summary>
/// Everything one streaming pass over an infolog can establish. Every field here was
/// checked against the real logs on this machine — see PLAN.md §5.8.
/// </summary>
public sealed record InfologSummary
{
    public required InfologFile File { get; init; }

    /// <summary>From <c>Spring Engine Version: 2026.07.01-61-g680e33a verify-ed25519</c>.</summary>
    public string? EngineVersion { get; init; }

    public string? BuildEnvironment { get; init; }

    /// <summary>From <c>[DataDirLocater::FindWriteableDataDir] using writeable data-directory "…"</c>.</summary>
    public string? WriteDirectory { get; init; }

    /// <summary>From <c>Using writeable configuration source: "…"</c>.</summary>
    public string? ConfigSource { get; init; }

    /// <summary>
    /// Engine folder name, recovered from the read-only data directory the run mounted
    /// (<c>…/data/engine/development/</c>). The log never names its own binary, so this is
    /// the only way to tell which build produced it.
    /// </summary>
    public string? EngineFolder { get; init; }

    /// <summary>From <c>[DataDirLocater::Check] Isolation Mode!</c>, i.e. <c>--isolation</c> was passed.</summary>
    public bool IsIsolated { get; init; }

    /// <summary>
    /// From the single <c>infologVersionTags:</c> line the game writes — engine, game,
    /// lobby (the Chobby build) and map. One line that answers "what was this run".
    /// </summary>
    public string? TagEngine { get; init; }

    public string? TagGame { get; init; }

    public string? TagLobby { get; init; }

    public string? TagMap { get; init; }

    /// <summary>The path from the <c>[StartScript]</c> line, exactly as logged.</summary>
    public string? StartScriptPath { get; init; }

    public StartScriptResolution StartScriptState { get; init; }

    public InfologFailure? Failure { get; init; }

    public int ErrorCount { get; init; }

    public int WarningCount { get; init; }

    public int LineCount { get; init; }

    /// <summary>Last <c>[t=…]</c> seen, i.e. how long the engine was up.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Highest simulation frame, so a real match can be told from a failed load.</summary>
    public int? LastFrame { get; init; }

    /// <summary>True when the engine's shutdown sequence is present.</summary>
    public bool ReachedShutdown { get; init; }

    public InfologOutcome Outcome => Failure is not null
        ? InfologOutcome.Failed
        : ReachedShutdown
            ? InfologOutcome.Completed
            : InfologOutcome.Unknown;

    /// <summary>Section tags present in the file, for the viewer's section filter.</summary>
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();

    /// <summary>Populated instead of everything else when the file could not be read.</summary>
    public string? ReadError { get; init; }

    public string? MapName => TagMap;

    public string? GameName => TagGame;

    public string OutcomeText => Outcome switch
    {
        InfologOutcome.Completed => "exited normally",
        InfologOutcome.Failed => Failure!.IsCrash ? "crashed" : "stopped with an error",
        _ => "no shutdown recorded",
    };

    /// <summary>One-line headline for the summary card.</summary>
    public string HeadlineText
    {
        get
        {
            var parts = new List<string>(4) { OutcomeText };

            if (Duration is { } duration)
            {
                parts.Add($"{duration.TotalSeconds:0.#} s");
            }

            if (LastFrame is { } frame && frame > 0)
            {
                parts.Add($"{frame} frames");
            }

            parts.Add($"{Count(ErrorCount, "error")}, {Count(WarningCount, "warning")}");

            return string.Join(" · ", parts);

            static string Count(int value, string noun) =>
                value == 1 ? $"1 {noun}" : $"{value} {noun}s";
        }
    }
}

/// <summary>What the viewer wants out of a log. Applied while reading, not afterwards.</summary>
public sealed record InfologFilter
{
    public static readonly InfologFilter All = new();

    public InfologSeverity MinimumSeverity { get; init; } = InfologSeverity.Info;

    /// <summary>Case-insensitive substring over the whole raw line. Null matches everything.</summary>
    public string? Search { get; init; }

    /// <summary>Exact section tag, e.g. <c>VFS</c>. Null matches everything.</summary>
    public string? Section { get; init; }

    public bool IsEmpty => MinimumSeverity == InfologSeverity.Info
                           && string.IsNullOrWhiteSpace(Search)
                           && string.IsNullOrWhiteSpace(Section);
}

/// <summary>
/// A filtered window onto a log. Bounded on purpose: the largest file here is 42 MB and
/// 484k lines, so the viewer holds the first <see cref="LineLimit"/> matches and says how
/// many it left behind.
/// </summary>
public sealed record InfologRead
{
    /// <summary>How many matching lines the viewer will hold at once.</summary>
    public const int LineLimit = 20_000;

    public IReadOnlyList<InfologLine> Lines { get; init; } = Array.Empty<InfologLine>();

    /// <summary>Matches found, which can exceed <see cref="Lines"/>.</summary>
    public int MatchCount { get; init; }

    public int LineCount { get; init; }

    public bool Truncated => MatchCount > Lines.Count;

    public string? ReadError { get; init; }

    /// <summary>
    /// Where this read stopped, so a live viewer can resume from there instead of
    /// re-scanning the file from the start on every poll. Null only alongside
    /// <see cref="ReadError"/>.
    /// </summary>
    public InfologTailCursor? Cursor { get; init; }
}

/// <summary>
/// A resume point for tailing a log that is still being written: how many bytes of it
/// have been read, the absolute line number reached, and the last line seen (whether or
/// not it matched the filter) so a line with no timestamp of its own can still inherit
/// one across the boundary between two reads.
/// </summary>
public sealed record InfologTailCursor
{
    public required long Position { get; init; }

    public required int LineNumber { get; init; }

    public InfologLine? LastLine { get; init; }
}

/// <summary>
/// The result of resuming a read from an <see cref="InfologTailCursor"/>: only what was
/// written since, not the whole file.
/// </summary>
public sealed record InfologTailRead
{
    public IReadOnlyList<InfologLine> NewLines { get; init; } = Array.Empty<InfologLine>();

    /// <summary>New matches since the cursor, which can exceed <see cref="NewLines"/>.</summary>
    public int NewMatchCount { get; init; }

    public int NewLineCount { get; init; }

    public required InfologTailCursor Cursor { get; init; }

    /// <summary>
    /// True when the file is now shorter than the cursor's position — it was rotated or
    /// truncated out from under the viewer, and a full reload is the only correct answer.
    /// </summary>
    public bool Truncated { get; init; }

    public string? ReadError { get; init; }
}
