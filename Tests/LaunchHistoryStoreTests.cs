using System.Text.Json;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// The launch history of PLAN.md §6.1. Persistence matters here: a replay is only
/// useful if it survives the session in which the run happened.
/// </summary>
public sealed class LaunchHistoryStoreTests
{
    private static LaunchHistoryStore Store(TempDirectory temp, out string path)
    {
        path = Path.Combine(temp.Path, "history.json");
        return new LaunchHistoryStore(new TestLogger<LaunchHistoryStore>(), path);
    }

    private static LaunchRecord Record(string name, DateTimeOffset startedAt) => new()
    {
        StartedAt = startedAt,
        ProfileName = name,
        EngineName = "recoil_2026.07.04",
        Mode = LaunchMode.Script,
        ExecutablePath = @"C:\BAR\data\engine\recoil_2026.07.04\spring.exe",
        Arguments = new List<string> { "--isolation", @"C:\scripts\sandbox 2.txt" },
        ScriptPath = @"C:\scripts\sandbox 2.txt",
        Profile = new LaunchProfile { Name = name, Mode = LaunchMode.Script, ScriptFileName = "sandbox 2.txt" },
    };

    [Fact]
    public void Record_KeepsTheNewestFirst()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out _);

        var now = DateTimeOffset.Now;
        store.Record(Record("first", now.AddMinutes(-10)));
        store.Record(Record("second", now));

        Assert.Collection(
            store.Records,
            record => Assert.Equal("second", record.ProfileName),
            record => Assert.Equal("first", record.ProfileName));
    }

    [Fact]
    public async Task Record_ThenLoad_RoundTripsThroughTheFile()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out string path);

        LaunchRecord written = Record("headless autohost", DateTimeOffset.Now);
        written.ScriptSnapshot = "[game]\n{\nmapname=Rosetta 1.4.4;\n}";
        store.Record(written);

        // The write happens behind the caller, so wait for the file to appear.
        await WaitForFileAsync(path);

        LaunchHistoryStore reloaded = new(new TestLogger<LaunchHistoryStore>(), path);
        await reloaded.LoadAsync();

        LaunchRecord read = Assert.Single(reloaded.Records);
        Assert.Equal(written.Id, read.Id);
        Assert.Equal("headless autohost", read.ProfileName);
        Assert.Equal(LaunchMode.Script, read.Mode);
        Assert.Equal(written.Arguments, read.Arguments);
        Assert.Equal(written.ScriptSnapshot, read.ScriptSnapshot);
        Assert.Equal("sandbox 2.txt", read.Profile.ScriptFileName);
    }

    /// <summary>
    /// The exit code arrives from a process-exit callback long after the launch, and it is
    /// the difference between "this worked" and "this is the run that broke".
    /// </summary>
    [Fact]
    public async Task Update_PersistsTheOutcomeOfARunAlreadyRecorded()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out string path);

        LaunchRecord record = Record("crashy", DateTimeOffset.Now);
        store.Record(record);

        record.ExitedAt = record.StartedAt.AddSeconds(27);
        record.ExitCode = 255;
        store.Update(record);

        // Waiting for the file to exist is not enough here: the Record write may well have
        // created it before the Update write landed, and this test is about the second one.
        await WaitForFileAsync(path, text => text.Contains("\"ExitCode\": 255"));

        LaunchHistoryStore reloaded = new(new TestLogger<LaunchHistoryStore>(), path);
        await reloaded.LoadAsync();

        LaunchRecord read = Assert.Single(reloaded.Records);
        Assert.Equal(255, read.ExitCode);
        Assert.True(read.Failed);
        Assert.Equal(TimeSpan.FromSeconds(27), read.Duration);
        Assert.Contains("exited with code 255", read.StatusText);
    }

    /// <summary>
    /// A run can outlive its own place in the list on a busy day. Re-adding it keeps the
    /// outcome rather than dropping it.
    /// </summary>
    [Fact]
    public void Update_ReAddsARecordThatIsNoLongerHeld()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out _);

        LaunchRecord record = Record("long run", DateTimeOffset.Now);
        record.ExitCode = 0;
        store.Update(record);

        Assert.Single(store.Records);
    }

    [Fact]
    public void Record_TrimsToTheRetentionLimit()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out _);

        var now = DateTimeOffset.Now;
        for (int i = 0; i < LaunchHistoryStore.MaxRecords + 5; i++)
        {
            store.Record(Record("run " + i, now.AddSeconds(i)));
        }

        Assert.Equal(LaunchHistoryStore.MaxRecords, store.Records.Count);

        // The newest survive; the first five are gone.
        Assert.Equal("run " + (LaunchHistoryStore.MaxRecords + 4), store.Records[0].ProfileName);
    }

    [Fact]
    public void Record_TruncatesAnOversizedScriptSnapshot()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out _);

        LaunchRecord record = Record("huge", DateTimeOffset.Now);
        record.ScriptSnapshot = new string('x', LaunchHistoryStore.MaxSnapshotChars * 2);
        store.Record(record);

        Assert.Equal(LaunchHistoryStore.MaxSnapshotChars, store.Records[0].ScriptSnapshot!.Length);
    }

    [Fact]
    public async Task RemoveAsync_DropsOneRecordAndKeepsTheRest()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out _);

        LaunchRecord keep = Record("keep", DateTimeOffset.Now.AddMinutes(-1));
        LaunchRecord drop = Record("drop", DateTimeOffset.Now);
        store.Record(keep);
        store.Record(drop);

        Assert.True(await store.RemoveAsync(drop.Id));
        Assert.False(await store.RemoveAsync("no such id"));

        Assert.Equal("keep", Assert.Single(store.Records).ProfileName);
    }

    [Fact]
    public async Task ClearAsync_EmptiesTheHistoryAndTheFile()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out string path);

        store.Record(Record("gone", DateTimeOffset.Now));
        await store.ClearAsync();

        Assert.Empty(store.Records);

        LaunchHistoryStore reloaded = new(new TestLogger<LaunchHistoryStore>(), path);
        await reloaded.LoadAsync();
        Assert.Empty(reloaded.Records);
    }

    /// <summary>
    /// The file is hand-editable, which is the point of the indented JSON — so the order it
    /// comes back in cannot be trusted.
    /// </summary>
    [Fact]
    public async Task LoadAsync_SortsWhateverOrderTheFileWasIn()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out string path);

        var now = DateTimeOffset.Now;
        var records = new List<LaunchRecord>
        {
            Record("oldest", now.AddHours(-2)),
            Record("newest", now),
            Record("middle", now.AddHours(-1)),
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(records));
        await store.LoadAsync();

        Assert.Equal(new[] { "newest", "middle", "oldest" }, store.Records.Select(r => r.ProfileName));
    }

    [Fact]
    public async Task LoadAsync_StartsEmptyOnAFileThatIsNotJson()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out string path);

        await File.WriteAllTextAsync(path, "not json at all");
        await store.LoadAsync();

        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task LoadAsync_StartsEmptyWhenThereIsNoFile()
    {
        using var temp = new TempDirectory();
        LaunchHistoryStore store = Store(temp, out _);

        await store.LoadAsync();

        Assert.Empty(store.Records);
    }

    /// <summary>
    /// The command line is for display and for copying, never for starting a process — but
    /// it still has to be pasteable, so an argument with a space is quoted. Rendered
    /// through the launch service's own formatter, so this also pins that a replayed run
    /// reads the same as the Launch page's preview of it.
    /// </summary>
    [Fact]
    public void CommandLineText_QuotesOnlyWhatNeedsQuoting()
    {
        LaunchRecord record = Record("quoting", DateTimeOffset.Now);

        Assert.Equal(
            @"C:\BAR\data\engine\recoil_2026.07.04\spring.exe --isolation ""C:\scripts\sandbox 2.txt""",
            record.CommandLineText);
    }

    /// <summary>
    /// Waits for a write that happens behind the caller. Record and Update persist without
    /// returning a task on purpose — they are called from a process-exit callback with
    /// nobody left to await one — so a test has to poll for the result.
    /// </summary>
    private static async Task WaitForFileAsync(string path, Func<string, bool>? until = null)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            string? content = ReadShared(path);

            if (content is not null && (until is null || until(content)))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"{path} never reached the expected state. Content:\n{ReadShared(path) ?? "(no file)"}");
    }

    /// <summary>
    /// Reads the way the store itself does, sharing delete.
    ///
    /// Not incidental: the store replaces the file by moving a temporary over it, and on
    /// Windows that move fails if any handle is open without FileShare.Delete. A polling
    /// reader that left it out would make the very writes it is waiting for fail — which
    /// is exactly how the store's own reader was found to be doing it.
    /// </summary>
    private static string? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
