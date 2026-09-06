using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class StartScriptRecovery : IStartScriptRecovery
{
    /// <summary>
    /// A start script is a few kilobytes. Anything past this is not one, and reading it
    /// into a text box would be the wrong response either way.
    /// </summary>
    private const int MaxScriptBytes = 1024 * 1024;

    private readonly ILogger<StartScriptRecovery> _logger;
    private readonly IInstallationContext _installation;
    private readonly IStartScriptStore _scripts;
    private readonly IStartScriptSerializer _serializer;

    public StartScriptRecovery(
        ILogger<StartScriptRecovery> logger,
        IInstallationContext installation,
        IStartScriptStore scripts,
        IStartScriptSerializer serializer)
    {
        _logger = logger;
        _installation = installation;
        _scripts = scripts;
        _serializer = serializer;
    }

    public async Task<RecoveredScript> RecoverAsync(
        InfologSummary? summary,
        CancellationToken cancellationToken = default)
    {
        string? problem = null;

        // The logged path first: it is the script the engine actually loaded, and it names
        // a file that still exists.
        if (summary?.StartScriptState == StartScriptResolution.Resolved && summary.StartScriptPath is { } logged)
        {
            RecoveredScript? fromLog = await ReadAsync(
                logged, RecoveredScriptOrigin.Infolog, null, cancellationToken).ConfigureAwait(false);

            if (fromLog is not null)
            {
                return fromLog;
            }

            problem = $"The log names \"{logged}\" but it could not be read.";
        }
        else if (summary?.StartScriptState == StartScriptResolution.Unresolved)
        {
            // PLAN.md §2.4. The one real example of this on this machine is a path
            // truncated at a space, and the same run ends with the engine refusing to
            // start on it — so the honest thing is to say so, not to guess.
            problem = $"The log names \"{summary.StartScriptPath}\", which does not exist. "
                      + "An unquoted path is truncated at the first space, so this is probably only part of one.";
        }

        // A --menu run logs no path at all, so Chobby's own file is the only record of
        // what was set up in the lobby.
        if (_installation.Current is { } installation)
        {
            RecoveredScript? fromChobby = await ReadAsync(
                installation.ChobbyScriptPath,
                RecoveredScriptOrigin.ChobbyScript,
                problem,
                cancellationToken).ConfigureAwait(false);

            if (fromChobby is not null)
            {
                return fromChobby;
            }
        }

        return RecoveredScript.Empty with
        {
            Problem = problem
                      ?? "This run logged no start script, and Chobby has not left a data\\_script.txt behind.",
        };
    }

    public async Task<StartScriptFile?> ImportAsync(
        RecoveredScript script,
        string desiredName,
        CancellationToken cancellationToken = default)
    {
        if (!script.HasScript)
        {
            return null;
        }

        // CreateAsync uniques the name rather than overwriting, so importing twice cannot
        // destroy the first import.
        StartScriptFile? created = await _scripts
            .CreateAsync(desiredName, script.Text!, cancellationToken)
            .ConfigureAwait(false);

        if (created is not null)
        {
            _logger.LogInformation(
                "Imported the {Origin} start script from {Source} as {Name}.",
                script.Origin,
                script.Path,
                created.FileName);
        }

        return created;
    }

    public async Task<ScriptComparison> CompareWithLibraryAsync(
        RecoveredScript script,
        string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        string? name = fileName ?? await GuessLibraryNameAsync(script, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(name))
        {
            return new ScriptComparison { Found = false };
        }

        string? stored = await _scripts.ReadTextAsync(name, cancellationToken).ConfigureAwait(false);

        return stored is null
            ? new ScriptComparison { FileName = name, Found = false }
            : new ScriptComparison
            {
                FileName = name,
                Found = true,
                Diff = TextDiff.Compare(stored, script.Text),
            };
    }

    /// <summary>
    /// Which library script a recovered one should be measured against.
    ///
    /// If the run loaded a library file, that is the answer and no guessing is needed —
    /// which is the common case for a re-run, and the one where a diff actually reveals
    /// something (the file has been edited since). Otherwise fall back to a script whose
    /// name matches the suggested import name.
    /// </summary>
    private async Task<string?> GuessLibraryNameAsync(RecoveredScript script, CancellationToken cancellationToken)
    {
        if (script.Path is { Length: > 0 } path)
        {
            string? directory = SafeDirectoryName(path);

            if (directory is not null
                && string.Equals(
                    directory.TrimEnd(Path.DirectorySeparatorChar),
                    _scripts.LibraryPath.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(path);
            }
        }

        var existing = await _scripts.ListAsync(cancellationToken).ConfigureAwait(false);
        string candidate = script.SuggestedFileName + ".txt";

        return existing.Any(file => string.Equals(file.FileName, candidate, StringComparison.OrdinalIgnoreCase))
            ? candidate
            : null;
    }

    /// <summary>
    /// Reads a candidate script and describes what it sets up. Null when the file is
    /// absent, too big to be a script, or unreadable — every one of which is a reason to
    /// fall through to the next source rather than to fail.
    /// </summary>
    private async Task<RecoveredScript?> ReadAsync(
        string path,
        RecoveredScriptOrigin origin,
        string? problem,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists || info.Length > MaxScriptBytes)
            {
                return null;
            }

            // Chobby rewrites _script.txt as the game starts, and the engine may still
            // have it open.
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            return Describe(path, origin, text, info.LastWriteTimeUtc, problem);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Could not read a candidate start script at {Path}.", path);
            return null;
        }
    }

    private RecoveredScript Describe(
        string path,
        RecoveredScriptOrigin origin,
        string text,
        DateTime lastWriteUtc,
        string? problem)
    {
        StartScriptParseResult parsed = _serializer.Parse(text);

        var recovered = new RecoveredScript
        {
            Origin = origin,
            Path = path,
            Text = text,
            LastWriteUtc = lastWriteUtc,
            Problem = problem,
            SuggestedFileName = _scripts.MakeUniqueName("recovered run"),
        };

        if (!parsed.Success)
        {
            // Still offered for import and re-run: the engine is the authority on whether
            // a script is valid, and this parser only has to be right about the parts the
            // UI describes.
            return recovered with
            {
                Problem = problem is null
                    ? $"The script could not be parsed ({parsed.Error}), so it is shown as-is."
                    : problem,
            };
        }

        var model = new StartScriptModel(parsed.Document!);

        return recovered with
        {
            MapName = model.MapName,
            GameType = model.GameType,
            PlayerCount = model.Players.Count,
            AiCount = model.Ais.Count,
            SuggestedFileName = _scripts.MakeUniqueName(SuggestName(model, origin)),
        };
    }

    /// <summary>
    /// Names an import after what it actually is — the map, and the fact that it came out
    /// of the lobby — so a library of recovered runs is still readable a week later.
    /// </summary>
    private static string SuggestName(StartScriptModel model, RecoveredScriptOrigin origin)
    {
        string prefix = origin == RecoveredScriptOrigin.ChobbyScript ? "chobby" : "recovered";

        if (model.MapName is not { Length: > 0 } map)
        {
            return prefix + " run";
        }

        // The library rejects the characters a map name can legitimately contain.
        var clean = new StringBuilder(map.Length);

        foreach (char c in map)
        {
            clean.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        return $"{prefix} {clean}".Trim();
    }

    /// <summary>
    /// <see cref="Path.GetDirectoryName(string)"/> throws on the malformed paths this
    /// feature exists to cope with.
    /// </summary>
    private static string? SafeDirectoryName(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }
}
