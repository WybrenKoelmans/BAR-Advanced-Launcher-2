using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class InfologParser : IInfologParser
{
    /// <summary>
    /// Hard stop on a single pass. The biggest log on this machine is 484k lines, so this
    /// is not a limit anyone should reach — it is there so a corrupt or endlessly
    /// appended file cannot hang the app.
    /// </summary>
    internal const int MaxLines = 3_000_000;

    /// <summary>
    /// How many lines a multi-line <c>errorMsg="…"</c> may span before the parser gives up
    /// looking for its closing quote. The real crash block is four lines.
    /// </summary>
    private const int MaxFailureLines = 40;

    /// <summary>Distinct section tags offered to the filter, most frequent first.</summary>
    private const int MaxSections = 60;

    private readonly ILogger<InfologParser> _logger;

    public InfologParser(ILogger<InfologParser> logger) => _logger = logger;

    public Task<InfologSummary> SummariseAsync(InfologFile file, CancellationToken cancellationToken = default) =>
        Task.Run(() => Summarise(file, cancellationToken), cancellationToken);

    public Task<InfologRead> ReadAsync(
        InfologFile file,
        InfologFilter? filter = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(file, filter ?? InfologFilter.All, cancellationToken), cancellationToken);

    public Task<InfologTailRead> TailAsync(
        InfologFile file,
        InfologFilter filter,
        InfologTailCursor cursor,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Tail(file, filter, cursor, cancellationToken), cancellationToken);

    private InfologSummary Summarise(InfologFile file, CancellationToken cancellationToken)
    {
        var facts = new SummaryBuilder(file);

        try
        {
            using StreamReader reader = Open(file.Path);
            InfologLine? previous = null;
            int number = 0;

            while (reader.ReadLine() is { } raw)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++number > MaxLines)
                {
                    // Not reportable in the UI, because no real log comes near this — the
                    // biggest here is 484k lines. It exists so a corrupt or endlessly
                    // appended file cannot hang the app, and if it ever fires the log is
                    // the right place to find out.
                    _logger.LogWarning(
                        "Stopped reading {Path} after {Lines} lines; the summary is incomplete.",
                        file.Path,
                        MaxLines);
                    break;
                }

                InfologLine line = ParseLine(number, raw, previous);
                facts.Observe(line);
                previous = line;
            }

            facts.LineCount = Math.Min(number, MaxLines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not read the infolog at {Path}.", file.Path);
            return new InfologSummary { File = file, ReadError = ex.Message };
        }

        return facts.Build();
    }

    private InfologRead Read(InfologFile file, InfologFilter filter, CancellationToken cancellationToken)
    {
        ReadPass pass = ReadFrom(file.Path, filter, startPosition: 0, startLineNumber: 0, startPrevious: null, cancellationToken);

        if (pass.ReadError is { } error)
        {
            return new InfologRead { ReadError = error };
        }

        return new InfologRead
        {
            Lines = pass.Lines,
            MatchCount = pass.MatchCount,
            LineCount = pass.LineCount,
            Cursor = pass.Cursor,
        };
    }

    /// <summary>
    /// Resumes a read from a cursor a previous read or tail left off at, materialising
    /// only what has been written since. The one thing a cursor cannot survive is the file
    /// getting shorter than it — a rotation or truncation — which is reported back rather
    /// than guessed at.
    /// </summary>
    private InfologTailRead Tail(InfologFile file, InfologFilter filter, InfologTailCursor cursor, CancellationToken cancellationToken)
    {
        long length;
        try
        {
            length = new FileInfo(file.Path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new InfologTailRead { Cursor = cursor, ReadError = ex.Message };
        }

        if (length < cursor.Position)
        {
            return new InfologTailRead { Cursor = cursor, Truncated = true };
        }

        if (length == cursor.Position)
        {
            // Nothing new since last time; not worth opening the file for.
            return new InfologTailRead { Cursor = cursor };
        }

        ReadPass pass = ReadFrom(file.Path, filter, cursor.Position, cursor.LineNumber, cursor.LastLine, cancellationToken);

        if (pass.ReadError is { } error)
        {
            return new InfologTailRead { Cursor = cursor, ReadError = error };
        }

        return new InfologTailRead
        {
            NewLines = pass.Lines,
            NewMatchCount = pass.MatchCount,
            NewLineCount = pass.LineCount,
            Cursor = pass.Cursor,
        };
    }

    private readonly record struct ReadPass(
        List<InfologLine> Lines,
        int MatchCount,
        int LineCount,
        InfologTailCursor Cursor,
        string? ReadError);

    /// <summary>
    /// Streams <paramref name="path"/> from <paramref name="startPosition"/> onward,
    /// applying <paramref name="filter"/> as it goes and stopping at
    /// <see cref="InfologRead.LineLimit"/> matches materialised (though counting continues
    /// to the end, so <see cref="ReadPass.MatchCount"/> is always exact).
    ///
    /// <paramref name="startPrevious"/> carries the last line seen across a resumed read,
    /// so a continuation line right at the seam still inherits its time, frame, section and
    /// severity correctly.
    /// </summary>
    private ReadPass ReadFrom(
        string path,
        InfologFilter filter,
        long startPosition,
        int startLineNumber,
        InfologLine? startPrevious,
        CancellationToken cancellationToken)
    {
        var lines = new List<InfologLine>();
        int matches = 0;
        int number = startLineNumber;

        try
        {
            using FileStream stream = OpenStream(path, startPosition);
            using StreamReader reader = WrapReader(stream, atStart: startPosition == 0);
            InfologLine? previous = startPrevious;

            while (reader.ReadLine() is { } raw)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (number - startLineNumber >= MaxLines)
                {
                    // Not reportable in the UI, because no real log comes near this — the
                    // biggest here is 484k lines. It exists so a corrupt or endlessly
                    // appended file cannot hang the app, and if it ever fires the log is
                    // the right place to find out.
                    _logger.LogWarning(
                        "Stopped reading {Path} after {Lines} lines; the read is incomplete.",
                        path,
                        MaxLines);
                    break;
                }

                number++;
                InfologLine line = ParseLine(number, raw, previous);
                previous = line;

                if (Matches(line, filter))
                {
                    matches++;

                    if (lines.Count < InfologRead.LineLimit)
                    {
                        lines.Add(line);
                    }
                }
            }

            return new ReadPass(
                lines,
                matches,
                number - startLineNumber,
                new InfologTailCursor { Position = stream.Position, LineNumber = number, LastLine = previous },
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not read the infolog at {Path}.", path);

            return new ReadPass(
                lines,
                matches,
                number - startLineNumber,
                new InfologTailCursor { Position = startPosition, LineNumber = startLineNumber, LastLine = startPrevious },
                ex.Message);
        }
    }

    /// <summary>
    /// Opens a log for reading while the engine still has it open, seeked to
    /// <paramref name="position"/> when resuming a tail.
    /// </summary>
    private static FileStream OpenStream(string path, long position)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        if (position > 0)
        {
            stream.Seek(position, SeekOrigin.Begin);
        }

        return stream;
    }

    /// <summary>
    /// The encoding is UTF-8 without <c>throwOnInvalidBytes</c> on purpose: one rotated log
    /// on this machine is not valid UTF-8, and a viewer that refuses to open a log is worse
    /// than one that shows a replacement character in a translated unit name. BOM detection
    /// only applies at the true start of the file — the live logs have one and the rotated
    /// ones do not, and a mid-file resume has no BOM to find regardless.
    /// </summary>
    private static StreamReader WrapReader(FileStream stream, bool atStart) =>
        new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: atStart,
            bufferSize: 64 * 1024);

    /// <summary>Opens a log for a full, from-the-start read, e.g. for <see cref="Summarise"/>.</summary>
    private static StreamReader Open(string path) => WrapReader(OpenStream(path, 0), atStart: true);

    internal static bool Matches(InfologLine line, InfologFilter filter)
    {
        if (line.Severity < filter.MinimumSeverity)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Section)
            && !string.Equals(line.Section, filter.Section, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Searched over the raw line rather than the message: the severity word and the
        // section tag are stripped out of the message, and someone typing "StartScript"
        // means the tag as much as the text.
        return string.IsNullOrWhiteSpace(filter.Search)
               || line.Raw.Contains(filter.Search, StringComparison.OrdinalIgnoreCase);
    }

    // ---- line parsing -----------------------------------------------------------

    /// <summary>
    /// Splits one line into timestamp, frame, section, severity and message.
    ///
    /// A line with no <c>[t=…]</c> of its own is a continuation of
    /// <paramref name="previous"/> — the config dump, the log-section banner and the crash
    /// block all work that way — and inherits its time, frame, section and severity so
    /// that filtering to errors still shows a whole crash rather than only its first line.
    /// </summary>
    internal static InfologLine ParseLine(int number, string raw, InfologLine? previous)
    {
        // A BOM survives into the first line's text when the file has one, which would
        // otherwise stop the very first timestamp from being recognised.
        ReadOnlySpan<char> rest = number == 1 ? raw.AsSpan().TrimStart('\uFEFF') : raw.AsSpan();

        if (!TryTakeTimestamp(ref rest, out TimeSpan time))
        {
            return new InfologLine
            {
                Number = number,
                Raw = raw,
                Message = raw,
                Time = previous?.Time,
                Frame = previous?.Frame,
                Section = previous?.Section,
                Severity = previous?.Severity ?? InfologSeverity.Info,
                IsContinuation = true,
            };
        }

        int? frame = TryTakeFrame(ref rest, out int parsedFrame) ? parsedFrame : null;

        if (rest.Length > 0 && rest[0] == ' ')
        {
            rest = rest[1..];
        }

        // Section first, then severity: the engine writes "[weapondefs.lua] Error: …" in
        // that order. Only a tag in this leading slot counts, which keeps function names
        // out of the section filter — "Error: [SetConfigInt] key … is deprecated" has no
        // section, and [SetConfigInt] stays part of the message where it belongs.
        string? section = TryTakeSection(ref rest);
        InfologSeverity severity = TryTakeSeverity(ref rest);

        return new InfologLine
        {
            Number = number,
            Raw = raw,
            Message = rest.ToString(),
            Time = time,
            Frame = frame,
            Section = section,
            Severity = severity,
        };
    }

    /// <summary>Consumes <c>[t=HH:MM:SS.ffffff]</c>.</summary>
    private static bool TryTakeTimestamp(ref ReadOnlySpan<char> text, out TimeSpan time)
    {
        time = default;

        if (!text.StartsWith("[t="))
        {
            return false;
        }

        int close = text.IndexOf(']');
        if (close < 0)
        {
            return false;
        }

        ReadOnlySpan<char> value = text[3..close];

        // The engine writes six fractional digits; TimeSpan.TryParse handles up to seven.
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time))
        {
            return false;
        }

        text = text[(close + 1)..];
        return true;
    }

    /// <summary>Consumes <c>[f=-000001]</c>, which is absent on the earliest lines.</summary>
    private static bool TryTakeFrame(ref ReadOnlySpan<char> text, out int frame)
    {
        frame = 0;

        if (!text.StartsWith("[f="))
        {
            return false;
        }

        int close = text.IndexOf(']');
        if (close < 0 || !int.TryParse(text[3..close], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out frame))
        {
            return false;
        }

        text = text[(close + 1)..];
        return true;
    }

    /// <summary>
    /// Consumes a leading <c>[Section]</c>. Rejects anything with whitespace or an
    /// implausible length, so a Lua chunk name such as
    /// <c>[string "LuaUI/Widgets/gui_chat.lua"]</c> is left in the message.
    /// </summary>
    private static string? TryTakeSection(ref ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || text[0] != '[')
        {
            return null;
        }

        int close = text.IndexOf(']');
        if (close is < 2 or > 64)
        {
            return null;
        }

        ReadOnlySpan<char> tag = text[1..close];

        foreach (char c in tag)
        {
            if (char.IsWhiteSpace(c) || c is '[' or '"' or '=')
            {
                return null;
            }
        }

        text = text[(close + 1)..].TrimStart(' ');
        return tag.ToString();
    }

    private static readonly (string Word, InfologSeverity Severity)[] SeverityWords =
    {
        ("Fatal", InfologSeverity.Fatal),
        ("Error", InfologSeverity.Error),
        ("Warning", InfologSeverity.Warning),
    };

    /// <summary>
    /// Reads the leading severity word, which is how the engine records severity — there is
    /// no field for it. Both forms in the real logs are recognised: <c>Error: …</c> and the
    /// bare <c>Error in DrawScreen(): …</c>.
    ///
    /// Only the first form is stripped from the message. In <c>Error:</c> the word is a
    /// label and the message stands without it; in <c>Error in DrawScreen()</c> it is part
    /// of the sentence, and removing it would leave the message reading "in DrawScreen()".
    /// </summary>
    private static InfologSeverity TryTakeSeverity(ref ReadOnlySpan<char> text)
    {
        foreach ((string word, InfologSeverity severity) in SeverityWords)
        {
            if (!text.StartsWith(word, StringComparison.Ordinal) || text.Length == word.Length)
            {
                continue;
            }

            switch (text[word.Length])
            {
                case ':':
                    text = text[(word.Length + 1)..].TrimStart(' ');
                    return severity;

                // "Error in …", "Error during execution…", "Error executing tweakdef…".
                case ' ':
                    return severity;
            }
        }

        return InfologSeverity.Info;
    }

    // ---- fact extraction --------------------------------------------------------

    /// <summary>
    /// Accumulates the facts of PLAN.md §5.8 over a single streaming pass.
    ///
    /// Every marker below was taken from the real logs in this install rather than from
    /// documentation, and the ones that look redundant are not: <c>infologVersionTags</c>
    /// is written by the game and so is missing from a run that never got that far, while
    /// the engine version banner is written by the engine and is always there.
    /// </summary>
    private sealed class SummaryBuilder
    {
        private const string EngineVersionMarker = "Spring Engine Version: ";
        private const string BuildEnvironmentMarker = "Build Environment: ";
        private const string WriteDirMarker = "using writeable data-directory ";
        private const string ConfigSourceMarker = "Using writeable configuration source: ";
        private const string IsolationMarker = "Isolation Mode!";
        private const string ReadOnlyDirMarker = "using read-only data directory: ";
        private const string StartScriptMarker = "Loading StartScript from: ";
        private const string VersionTagsMarker = "infologVersionTags:";
        private const string ExitMarker = "[ExitSpringProcess]";
        private const string ShutdownMarker = "SpringApp::Kill";
        private const string StacktraceMarker = "A stacktrace has been written to:";

        private readonly InfologFile _file;
        private readonly Dictionary<string, int> _sections = new(StringComparer.Ordinal);

        private string? _engineVersion;
        private string? _buildEnvironment;
        private string? _writeDirectory;
        private string? _configSource;
        private string? _engineFolder;
        private string? _startScriptPath;
        private string? _tagEngine;
        private string? _tagGame;
        private string? _tagLobby;
        private string? _tagMap;
        private bool _isolated;
        private bool _shutdown;
        private InfologFailure? _failure;
        private TimeSpan? _lastTime;
        private int? _lastFrame;
        private int _errors;
        private int _warnings;

        /// <summary>
        /// Set while an <c>errorMsg="…"</c> is still waiting for its closing quote, which
        /// is how a crash block reads: four lines, only the first of which carries a
        /// timestamp. Held as state rather than by reading ahead, so the outer loop stays
        /// the only thing that consumes lines and line numbers cannot drift.
        /// </summary>
        private FailureInProgress? _pending;

        public SummaryBuilder(InfologFile file) => _file = file;

        public int LineCount { get; set; }

        public void Observe(InfologLine line)
        {
            if (line.Time is { } time)
            {
                _lastTime = time;
            }

            if (line.Frame is { } frame && (_lastFrame is null || frame > _lastFrame))
            {
                _lastFrame = frame;
            }

            // Counted per entry, not per line: a four-line crash block is one error.
            if (!line.IsContinuation)
            {
                switch (line.Severity)
                {
                    case InfologSeverity.Warning:
                        _warnings++;
                        break;
                    case InfologSeverity.Error:
                    case InfologSeverity.Fatal:
                        _errors++;
                        break;
                }

                if (line.Section is { Length: > 0 } section && _sections.Count < 4096)
                {
                    _sections[section] = _sections.GetValueOrDefault(section) + 1;
                }
            }

            // An unterminated errorMsg swallows the following lines, so it is handled
            // before anything else looks at them.
            if (_pending is not null)
            {
                ContinueFailure(line);
                return;
            }

            string message = line.Message.TrimStart();

            TakeAfter(message, EngineVersionMarker, ref _engineVersion);
            TakeAfter(message, BuildEnvironmentMarker, ref _buildEnvironment);
            TakeQuotedAfter(message, WriteDirMarker, ref _writeDirectory);
            TakeQuotedAfter(message, ConfigSourceMarker, ref _configSource);

            if (_startScriptPath is null && TakeAfter(message, StartScriptMarker) is { } scriptPath)
            {
                // Taken verbatim, trailing whitespace aside. PLAN.md §2.4: a truncated path
                // is logged as if it were whole, and "correcting" it here would hide the bug.
                _startScriptPath = scriptPath;
            }

            if (!_isolated && message.Contains(IsolationMarker, StringComparison.Ordinal))
            {
                _isolated = true;
            }

            if (_engineFolder is null && TakeAfter(message, ReadOnlyDirMarker) is { } readOnlyDir)
            {
                _engineFolder = EngineFolderFrom(readOnlyDir);
            }

            if (_tagEngine is null && TakeAfter(message, VersionTagsMarker) is { } tags)
            {
                ApplyVersionTags(tags);
            }

            if (!_shutdown && line.Section is not null && line.Section.Contains(ShutdownMarker, StringComparison.Ordinal))
            {
                _shutdown = true;
            }

            if (_failure is null && line.Raw.Contains(ExitMarker, StringComparison.Ordinal))
            {
                BeginFailure(line);
            }
        }

        public InfologSummary Build()
        {
            // A log that ends mid-message still has a failure worth reporting: the engine
            // was killed, or the file was rotated while it was being written.
            if (_pending is not null)
            {
                Complete(force: true);
            }

            return BuildCore();
        }

        private InfologSummary BuildCore() => new()
        {
            File = _file,
            EngineVersion = _engineVersion,
            BuildEnvironment = _buildEnvironment,
            WriteDirectory = _writeDirectory,
            ConfigSource = _configSource,
            EngineFolder = _engineFolder,
            IsIsolated = _isolated,
            TagEngine = _tagEngine,
            TagGame = _tagGame,
            TagLobby = _tagLobby,
            TagMap = _tagMap,
            StartScriptPath = _startScriptPath,
            StartScriptState = ResolveScript(_startScriptPath),
            Failure = _failure,
            ErrorCount = _errors,
            WarningCount = _warnings,
            LineCount = LineCount,
            Duration = _lastTime,
            LastFrame = _lastFrame,
            ReachedShutdown = _shutdown,
            Sections = _sections
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(MaxSections)
                .Select(pair => pair.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

        /// <summary>
        /// PLAN.md §2.4: a path that does not resolve is suspect, not truth. The
        /// "<c>from: All</c>" log on this machine names a file that never existed, and the
        /// same run ends with the engine refusing to start on it.
        /// </summary>
        private static StartScriptResolution ResolveScript(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return StartScriptResolution.NotLogged;
            }

            try
            {
                return File.Exists(path) ? StartScriptResolution.Resolved : StartScriptResolution.Unresolved;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
            {
                // A truncated path can also be syntactically impossible.
                return StartScriptResolution.Unresolved;
            }
        }

        /// <summary>
        /// The text after <paramref name="marker"/>, or null when the marker is absent.
        /// Trailing whitespace only is trimmed — a value can legitimately be indented.
        /// </summary>
        private static string? TakeAfter(string message, string marker)
        {
            int at = message.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            string value = message[(at + marker.Length)..].TrimEnd();
            return value.Length > 0 ? value : null;
        }

        private static void TakeAfter(string message, string marker, ref string? target)
        {
            if (target is not null)
            {
                return;
            }

            int at = message.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return;
            }

            string value = message[(at + marker.Length)..].Trim();
            if (value.Length > 0)
            {
                target = value;
            }
        }

        private static void TakeQuotedAfter(string message, string marker, ref string? target)
        {
            if (target is not null)
            {
                return;
            }

            int at = message.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return;
            }

            string value = message[(at + marker.Length)..].Trim().Trim('"');
            if (value.Length > 0)
            {
                target = value;
            }
        }

        /// <summary>
        /// Pulls the engine folder name out of a mounted read-only data directory such as
        /// <c>C:/…/data/engine/development/</c>. The log never names the binary it is
        /// running, so this is the only handle on which build wrote it.
        /// </summary>
        private static string? EngineFolderFrom(string directory)
        {
            const string marker = "/engine/";
            string normalised = directory.Replace('\\', '/');

            int at = normalised.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            string tail = normalised[(at + marker.Length)..].Trim('/');
            int slash = tail.IndexOf('/');
            string name = slash < 0 ? tail : tail[..slash];

            return name.Length > 0 ? name : null;
        }

        /// <summary>
        /// Splits <c>engine=…,game=…,lobby=…,map=…</c>.
        ///
        /// Sliced between the known keys rather than split on commas, because the values
        /// are free text: a map or game name containing a comma would otherwise take the
        /// next field with it.
        /// </summary>
        private void ApplyVersionTags(string tags)
        {
            string[] keys = { "engine=", "game=", "lobby=", "map=" };
            var found = new List<(int At, string Key)>(keys.Length);

            foreach (string key in keys)
            {
                int at = tags.IndexOf(key, StringComparison.Ordinal);
                if (at >= 0)
                {
                    found.Add((at, key));
                }
            }

            if (found.Count == 0)
            {
                return;
            }

            found.Sort((left, right) => left.At.CompareTo(right.At));

            for (int i = 0; i < found.Count; i++)
            {
                (int at, string key) = found[i];
                int start = at + key.Length;
                int end = i + 1 < found.Count ? found[i + 1].At : tags.Length;

                string value = tags[start..end].TrimEnd().TrimEnd(',').Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                switch (key)
                {
                    case "engine=":
                        _tagEngine = value;
                        break;
                    case "game=":
                        _tagGame = value;
                        break;
                    case "lobby=":
                        _tagLobby = value;
                        break;
                    case "map=":
                        _tagMap = value;
                        break;
                }
            }
        }

        /// <summary>An <c>errorMsg="…"</c> whose closing quote is on a later line.</summary>
        private sealed class FailureInProgress
        {
            public required int LineNumber { get; init; }

            public required StringBuilder Text { get; init; }

            public int ExtraLines { get; set; }
        }

        private const string MessageKey = "errorMsg=\"";
        private const string Terminator = "\" msgCaption=\"";

        /// <summary>
        /// Starts reading an <c>[ExitSpringProcess]</c> line. The whole message is on one
        /// line for a refused start ("Setup-script does not exist…", "Dependent archive …
        /// not found"), but a crash spreads it over four:
        ///
        /// <code>
        /// Fatal: [ExitSpringProcess] errorMsg="Spring has crashed:
        ///   Access violation.
        ///
        /// A stacktrace has been written to:
        ///   C:\…\infolog.txt" msgCaption="Spring: Unhandled exception" mainThread=1
        /// </code>
        /// </summary>
        private void BeginFailure(InfologLine line)
        {
            int at = line.Raw.IndexOf(MessageKey, StringComparison.Ordinal);
            if (at < 0)
            {
                return;
            }

            _pending = new FailureInProgress
            {
                LineNumber = line.Number,
                Text = new StringBuilder(line.Raw[(at + MessageKey.Length)..]),
            };

            Complete(force: false);
        }

        private void ContinueFailure(InfologLine line)
        {
            FailureInProgress pending = _pending!;

            pending.Text.Append('\n').Append(line.Raw);
            pending.ExtraLines++;

            // A log truncated mid-message, or a stray errorMsg with no closing quote, must
            // not turn the rest of the file into one giant error.
            Complete(force: pending.ExtraLines >= MaxFailureLines);
        }

        /// <summary>
        /// Finishes the pending failure once its terminator arrives, or abandons the
        /// wait when <paramref name="force"/> says the message has run on too far.
        /// </summary>
        private void Complete(bool force)
        {
            FailureInProgress pending = _pending!;
            string combined = pending.Text.ToString();
            int end = combined.IndexOf(Terminator, StringComparison.Ordinal);

            if (end < 0 && !force)
            {
                return;
            }

            string message = end < 0 ? combined.TrimEnd('"') : combined[..end];
            string? caption = null;

            if (end >= 0)
            {
                string tail = combined[(end + Terminator.Length)..];
                int captionEnd = tail.IndexOf('"');
                caption = captionEnd < 0 ? tail : tail[..captionEnd];
            }

            _failure = new InfologFailure
            {
                Message = message,
                Caption = string.IsNullOrWhiteSpace(caption) ? null : caption,
                LineNumber = pending.LineNumber,
                StacktracePath = StacktraceFrom(message),
            };

            _pending = null;
        }

        /// <summary>
        /// The path on the line after "A stacktrace has been written to:", when the engine
        /// says it wrote one. Usually the infolog itself.
        /// </summary>
        private static string? StacktraceFrom(string message)
        {
            int at = message.IndexOf(StacktraceMarker, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            string tail = message[(at + StacktraceMarker.Length)..].TrimStart('\n', '\r', ' ');
            int newline = tail.IndexOfAny(new[] { '\n', '\r' });
            string path = (newline < 0 ? tail : tail[..newline]).Trim().Trim('"');

            return path.Length > 0 ? path : null;
        }
    }
}
