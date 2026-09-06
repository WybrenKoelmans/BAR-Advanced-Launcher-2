using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using BAR_Advanced_Launcher_2.Services;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// One past launch (PLAN.md §6.1). Persisted to <c>history.json</c>, so a run stays
/// replayable across sessions.
///
/// It carries the <see cref="Profile"/> that produced it rather than only the command
/// line, because a replay should go back through the same validation as a fresh launch —
/// the install may have moved, the engine may be gone. The command line is kept as well,
/// but only for display and for the copy button: rebuilding a process from a joined
/// string is exactly the quoting bug of PLAN.md §2.4.
/// </summary>
public sealed class LaunchRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? ExitedAt { get; set; }

    /// <summary>Null while it was still running when the app closed.</summary>
    public int? ExitCode { get; set; }

    public string ProfileName { get; set; } = string.Empty;

    public string EngineName { get; set; } = string.Empty;

    public LaunchMode Mode { get; set; }

    public string ExecutablePath { get; set; } = string.Empty;

    public List<string> Arguments { get; set; } = new();

    /// <summary>Full path of the start script the run used, when it used one.</summary>
    public string? ScriptPath { get; set; }

    /// <summary>
    /// The script's text as it was at launch time.
    ///
    /// Not for re-running — a replay reads the file, because that is what the engine does
    /// and pretending otherwise would run something the file no longer says. It is here so
    /// the History page can tell that the file has changed since the run and show what
    /// changed, which is the difference between "this replay reproduces the run" and "this
    /// replay runs something else under the same name".
    /// </summary>
    public string? ScriptSnapshot { get; set; }

    /// <summary>
    /// Where the engine wrote its log for this run, i.e. <c>&lt;write-dir&gt;\infolog.txt</c>.
    ///
    /// Goes stale the moment the next run overwrites it, which is why the History page
    /// resolves a record to a log by time window rather than trusting this path.
    /// </summary>
    public string? InfologPath { get; set; }

    /// <summary>The profile as launched, for replay.</summary>
    public LaunchProfile Profile { get; set; } = new();

    [JsonIgnore]
    public TimeSpan? Duration => ExitedAt - StartedAt;

    [JsonIgnore]
    public bool IsRunning => ExitedAt is null;

    [JsonIgnore]
    public bool Failed => ExitCode is not (null or 0);

    [JsonIgnore]
    public string StartedText => StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    [JsonIgnore]
    public string TitleText => $"{ProfileName} · {Mode}";

    /// <summary>
    /// Second line in the history list. A record with no exit is reported as unknown
    /// rather than as running: the app may simply have been closed first, and claiming a
    /// process from last week is still up would be worse than admitting we do not know.
    /// </summary>
    [JsonIgnore]
    public string StatusText
    {
        get
        {
            string outcome = ExitCode switch
            {
                null => "outcome unknown",
                0 => "exited cleanly",
                { } code => $"exited with code {code}",
            };

            string duration = Duration is { } elapsed
                ? $" after {elapsed:mm\\:ss}"
                : string.Empty;

            return $"{EngineName} · {outcome}{duration}";
        }
    }

    /// <summary>
    /// What a list row is called when nothing else names it — which includes the
    /// accessibility tree, where the default would be the type name.
    /// </summary>
    public override string ToString() => $"{TitleText} · {StartedText}";

    /// <summary>
    /// The command line for display and for the copy button.
    ///
    /// Rendered through the launch service's own <see cref="EngineCommandLine"/> rather
    /// than by a local quoting routine, so a replayed command reads exactly as the Launch
    /// page's preview of the same run. Two quoting implementations would eventually
    /// disagree, and a command line that disagrees with itself is worse than none — this
    /// string ends up in bug reports.
    /// </summary>
    [JsonIgnore]
    public string CommandLineText => new EngineCommandLine(ExecutablePath, Arguments).ToDisplayString();
}
