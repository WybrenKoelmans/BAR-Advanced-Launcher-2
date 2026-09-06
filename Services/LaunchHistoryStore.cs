using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Infrastructure;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class LaunchHistoryStore : ILaunchHistoryStore
{
    /// <summary>
    /// How many launches are kept. A developer relaunches dozens of times a day, so this
    /// is a few days of work — enough to find "the run where it still worked" and small
    /// enough that the file stays readable by hand.
    /// </summary>
    internal const int MaxRecords = 200;

    /// <summary>
    /// Cap on a stored script snapshot. Real start scripts are well under a kilobyte;
    /// this stops one pathological file from bloating the history.
    /// </summary>
    internal const int MaxSnapshotChars = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<LaunchHistoryStore> _logger;
    private readonly string _path;
    private readonly List<LaunchRecord> _records = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public LaunchHistoryStore(ILogger<LaunchHistoryStore> logger)
        : this(logger, AppPaths.HistoryFile)
    {
    }

    /// <summary>Test seam: lets a test persist into a disposable folder.</summary>
    public LaunchHistoryStore(ILogger<LaunchHistoryStore> logger, string path)
    {
        _logger = logger;
        _path = path;
    }

    public IReadOnlyList<LaunchRecord> Records
    {
        get
        {
            lock (_records)
            {
                return _records.ToArray();
            }
        }
    }

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        List<LaunchRecord> loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);

        lock (_records)
        {
            _records.Clear();

            // Ordered here rather than trusting the file: it is hand-editable, and a
            // history list out of order is worse than useless.
            _records.AddRange(loaded
                .Where(record => record is { Id.Length: > 0 })
                .OrderByDescending(record => record.StartedAt)
                .Take(MaxRecords));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Record(LaunchRecord record)
    {
        Truncate(record);

        lock (_records)
        {
            _records.Insert(0, record);

            if (_records.Count > MaxRecords)
            {
                _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
            }
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(LaunchRecord record)
    {
        lock (_records)
        {
            int at = _records.FindIndex(existing => existing.Id == record.Id);

            if (at >= 0)
            {
                _records[at] = record;
            }
            else
            {
                // Trimmed out from under us, or updated after a reload. Re-adding keeps
                // the outcome of a long run that outlived its own place in the list.
                _records.Insert(0, record);
            }
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        bool removed;

        lock (_records)
        {
            removed = _records.RemoveAll(record => record.Id == id) > 0;
        }

        if (removed)
        {
            await WriteAsync(cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_records)
        {
            _records.Clear();
        }

        await WriteAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Truncate(LaunchRecord record)
    {
        if (record.ScriptSnapshot is { Length: > MaxSnapshotChars } snapshot)
        {
            record.ScriptSnapshot = snapshot[..MaxSnapshotChars];
        }
    }

    /// <summary>
    /// Writes behind the caller. Nothing awaits this, so the continuation must not be
    /// able to throw — a failed history write is a log line, not a crashed launch.
    /// </summary>
    private void Persist() => _ = PersistAsync();

    private async Task PersistAsync()
    {
        try
        {
            await WriteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist the launch history.");
        }
    }

    private async Task<List<LaunchRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new List<LaunchRecord>();
        }

        try
        {
            // FileShare.Delete matters as much as the rest: the file is replaced by moving
            // a temporary over it, and on Windows that move fails outright if any handle is
            // open without it. A launch recorded while the History page is loading would
            // otherwise be lost to an UnauthorizedAccessException.
            await using FileStream stream = File.Open(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return await JsonSerializer
                .DeserializeAsync<List<LaunchRecord>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? new List<LaunchRecord>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the launch history from {Path}; starting empty.", _path);
            return new List<LaunchRecord>();
        }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        // Serialised: two exits can land at once, and the last writer would otherwise
        // race the temporary file.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            string temp = _path + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer
                    .SerializeAsync(stream, Records, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            await ReplaceAsync(temp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write the launch history to {Path}.", _path);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Moves the temporary file over the real one, retrying briefly.
    ///
    /// Replacing a file on Windows needs delete access to the target, and a file written a
    /// moment ago is routinely held open for a short while by something outside this
    /// process — Defender, the search indexer, a sync client. One attempt is therefore not
    /// enough, and unlike every other write in this app nobody is waiting on this one: the
    /// call sites are a launch and a process-exit callback, so a lost write is a launch
    /// silently missing from the history rather than an error anyone sees.
    ///
    /// Observed here as a run recorded without its exit code, roughly three times in four,
    /// when a second write followed the first within milliseconds.
    /// </summary>
    private async Task ReplaceAsync(string temp, CancellationToken cancellationToken)
    {
        const int attempts = 5;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, _path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                _logger.LogDebug(
                    ex, "Attempt {Attempt} to replace {Path} failed; retrying.", attempt, _path);

                await Task.Delay(20 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
