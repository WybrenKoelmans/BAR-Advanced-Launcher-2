using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>Where a recovered start script came from.</summary>
public enum RecoveredScriptOrigin
{
    /// <summary>Nothing to recover.</summary>
    None,

    /// <summary>The path from the log's <c>[StartScript]</c> line.</summary>
    Infolog,

    /// <summary>
    /// <c>data\_script.txt</c>, which Chobby rewrites every time it starts a skirmish.
    ///
    /// This is the interesting case: a <c>--menu</c> run logs no start-script line at all,
    /// so a match set up in the lobby can only be recovered from here. It is what turns
    /// Chobby into a script authoring tool (PLAN.md §5.8).
    /// </summary>
    ChobbyScript,
}

/// <summary>
/// A start script recovered from the last run, with the facts needed to decide whether
/// it is worth keeping.
/// </summary>
public sealed record RecoveredScript
{
    public static readonly RecoveredScript Empty = new();

    public RecoveredScriptOrigin Origin { get; init; }

    public string? Path { get; init; }

    public string? Text { get; init; }

    public DateTime? LastWriteUtc { get; init; }

    public string? MapName { get; init; }

    public string? GameType { get; init; }

    public int PlayerCount { get; init; }

    public int AiCount { get; init; }

    /// <summary>
    /// Why the obvious source could not be used, when it could not. Set for an
    /// unresolved path (PLAN.md §2.4) and for a script that would not parse.
    /// </summary>
    public string? Problem { get; init; }

    /// <summary>The name Import proposes, already free in the library.</summary>
    public string SuggestedFileName { get; init; } = "recovered run";

    public bool HasScript => Origin != RecoveredScriptOrigin.None && !string.IsNullOrWhiteSpace(Text);

    public string OriginText => Origin switch
    {
        RecoveredScriptOrigin.Infolog => "from the log's StartScript line",
        RecoveredScriptOrigin.ChobbyScript => "from data\\_script.txt, written by Chobby",
        _ => "nothing to recover",
    };

    /// <summary>What the script sets up, for the card above the Re-run button.</summary>
    public string DetailText
    {
        get
        {
            if (!HasScript)
            {
                return Problem ?? "No start script was found for this run.";
            }

            var parts = new System.Collections.Generic.List<string>(4);

            if (MapName is { Length: > 0 })
            {
                parts.Add(MapName);
            }

            if (GameType is { Length: > 0 })
            {
                parts.Add(GameType);
            }

            // Only mentioned when there are any. A Chobby skirmish script sets numusers in
            // the [game] block and declares no [PLAYERn] sections at all, so "0 players"
            // would be true of the sections and misleading about the match.
            if (PlayerCount > 0)
            {
                parts.Add(PlayerCount == 1 ? "1 player" : $"{PlayerCount} players");
            }

            if (AiCount > 0)
            {
                parts.Add(AiCount == 1 ? "1 AI" : $"{AiCount} AIs");
            }

            if (LastWriteUtc is { } written)
            {
                parts.Add("written " + written.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A recovered script measured against the library copy of the same name.</summary>
public sealed record ScriptComparison
{
    public string? FileName { get; init; }

    /// <summary>False when the library has nothing to compare against.</summary>
    public bool Found { get; init; }

    public TextDiffResult Diff { get; init; } = new();

    public string HeaderText => !Found
        ? FileName is { Length: > 0 }
            ? $"The library has no script called \"{FileName}\" to compare against."
            : "There is no obvious library script to compare against — import it first."
        : $"{FileName}: {Diff.SummaryText}";
}

/// <summary>
/// Recovers the start script of a past run and gets it into the library (PLAN.md §5.8).
///
/// Two sources, not one: a script run logs its path, and a menu run does not log
/// anything but leaves <c>data\_script.txt</c> behind.
/// </summary>
public interface IStartScriptRecovery
{
    /// <summary>
    /// Recovers the best available script for <paramref name="summary"/>, falling back to
    /// Chobby's own file. Never throws.
    /// </summary>
    Task<RecoveredScript> RecoverAsync(InfologSummary? summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a recovered script into the library under a free name derived from
    /// <paramref name="desiredName"/>. Null when there was nothing to import.
    /// </summary>
    Task<StartScriptFile?> ImportAsync(
        RecoveredScript script,
        string desiredName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Diffs a recovered script against a library script.
    /// </summary>
    /// <param name="fileName">
    /// The library script to compare with. Null picks one: the library file the log itself
    /// named, if it was one, otherwise the script matching the suggested import name.
    /// </param>
    Task<ScriptComparison> CompareWithLibraryAsync(
        RecoveredScript script,
        string? fileName = null,
        CancellationToken cancellationToken = default);
}
