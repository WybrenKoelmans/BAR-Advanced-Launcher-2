using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// The parser measured against the real logs on this machine rather than against
/// fixtures, in the same spirit as <see cref="StartScriptRealFileTests"/>: the engine has
/// no log format specification, so the only thing that can prove the markers are right is
/// the output of the engine itself. Every test is skipped where the install is absent, so
/// the suite still passes elsewhere.
///
/// All read-only. Nothing here writes to the install.
/// </summary>
public sealed class InfologRealFileTests
{
    private static readonly InfologParser Parser = new(new TestLogger<InfologParser>());

    private static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "Beyond-All-Reason");

    private static BarInstallation Install => new()
    {
        RootPath = InstallRoot,
        Source = InstallationSource.DefaultLocalAppData,
    };

    private static InfologFile? Newest()
    {
        if (!Directory.Exists(InstallRoot))
        {
            return null;
        }

        return InfologCatalog.Enumerate(Install).FirstOrDefault();
    }

    [SkippableFact]
    public void Enumerate_FindsThisInstallsLogs()
    {
        Skip.IfNot(Directory.Exists(InstallRoot), $"{InstallRoot} is not on this machine.");

        IReadOnlyList<InfologFile> found = InfologCatalog.Enumerate(Install);

        Skip.If(found.Count == 0, "This install has no logs yet.");

        // Newest first, and every entry actually exists.
        Assert.All(found, file => Assert.True(File.Exists(file.Path)));
        Assert.Equal(
            found.OrderByDescending(file => file.LastWriteUtc).Select(file => file.Path),
            found.Select(file => file.Path));
    }

    /// <summary>
    /// The header the engine always writes, whatever else the run did. If this stops being
    /// found, a marker has changed upstream.
    /// </summary>
    [SkippableFact]
    public async Task Summarise_ReadsTheEngineBannerFromTheNewestRealLog()
    {
        InfologFile? file = Newest();
        Skip.If(file is null, "No real infolog is available on this machine.");

        InfologSummary summary = await Parser.SummariseAsync(file!);

        Assert.Null(summary.ReadError);
        Assert.True(summary.LineCount > 0, "The log parsed as empty.");
        Assert.False(string.IsNullOrWhiteSpace(summary.EngineVersion), "No engine version was found.");
        Assert.False(string.IsNullOrWhiteSpace(summary.WriteDirectory), "No write directory was found.");
    }

    /// <summary>
    /// Every log in the folder, header facts aside: this is the test that catches a log the
    /// parser chokes on — a truncated one, or the one on this machine that is not valid
    /// UTF-8. Bounded to the newest few so the suite stays quick; the folder here holds 773
    /// files and 378 MB.
    /// </summary>
    [SkippableFact]
    public async Task Summarise_ReadsTheNewestRealLogsWithoutFailing()
    {
        Skip.IfNot(Directory.Exists(InstallRoot), $"{InstallRoot} is not on this machine.");

        IReadOnlyList<InfologFile> found = InfologCatalog.Enumerate(Install);
        Skip.If(found.Count == 0, "This install has no logs yet.");

        foreach (InfologFile file in found.Take(12))
        {
            InfologSummary summary = await Parser.SummariseAsync(file);

            Assert.Null(summary.ReadError);
            Assert.True(summary.LineCount > 0, $"{file.FileName} parsed as empty.");

            // A logged start-script path is either a real file or explicitly suspect, and
            // never silently presented as real (PLAN.md §2.4).
            if (summary.StartScriptPath is { Length: > 0 })
            {
                Assert.NotEqual(StartScriptResolution.NotLogged, summary.StartScriptState);
            }
            else
            {
                Assert.Equal(StartScriptResolution.NotLogged, summary.StartScriptState);
            }
        }
    }

    /// <summary>
    /// The version-tag line is the one that answers "what was this run", so it is worth
    /// proving it parses against a log the game really wrote rather than only a fixture.
    /// </summary>
    [SkippableFact]
    public async Task Summarise_SplitsTheVersionTagsOfARealRunThatReachedTheGame()
    {
        Skip.IfNot(Directory.Exists(InstallRoot), $"{InstallRoot} is not on this machine.");

        IReadOnlyList<InfologFile> found = InfologCatalog.Enumerate(Install);
        Skip.If(found.Count == 0, "This install has no logs yet.");

        foreach (InfologFile file in found.Take(12))
        {
            InfologSummary summary = await Parser.SummariseAsync(file);

            if (summary.TagEngine is null)
            {
                // A run that failed before the game loaded writes no tags. That is a real
                // state, not a parse failure.
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(summary.TagGame));
            Assert.False(string.IsNullOrWhiteSpace(summary.TagMap));

            // Not "fixed" to a version number: a .sdd source checkout really does carry
            // the literal, and the engine resolves it (PLAN.md §2.3).
            Assert.DoesNotContain(",", summary.TagMap!);
            return;
        }

        Skip.If(true, "None of the newest logs reached the game.");
    }

    /// <summary>
    /// Reading a 20 MB log has to stay affordable, because the viewer re-reads on every
    /// filter change. Generous enough not to be flaky on a cold cache.
    /// </summary>
    [SkippableFact]
    public async Task Summarise_StaysFastOnTheLargestRealLog()
    {
        Skip.IfNot(Directory.Exists(InstallRoot), $"{InstallRoot} is not on this machine.");

        InfologFile? largest = InfologCatalog.Enumerate(Install)
            .OrderByDescending(file => file.Length)
            .FirstOrDefault();

        Skip.If(largest is null, "This install has no logs yet.");
        Skip.If(largest!.Length < 1024 * 1024, "No log here is big enough to be worth timing.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        InfologSummary summary = await Parser.SummariseAsync(largest);
        clock.Stop();

        Assert.Null(summary.ReadError);

        // Proves the whole file was walked, not abandoned after the header: without this a
        // parser that stopped on the first line would look impressively fast. Expressed
        // against the file's size rather than a line count, so it means the same thing on
        // another machine — no infolog averages 4 KB a line.
        Assert.True(
            summary.LineCount > largest.Length / 4096,
            $"{largest.FileName} is {largest.SizeText} but parsed as only {summary.LineCount} lines.");

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(30),
            $"Summarising {largest.SizeText} ({summary.LineCount} lines) took {clock.Elapsed}.");
    }
}
