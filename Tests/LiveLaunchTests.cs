using System.Diagnostics;
using BAR_Advanced_Launcher_2.Models;
using BAR_Advanced_Launcher_2.Services;
using Xunit;
using Xunit.Abstractions;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>
/// Phase 4's acceptance criterion is "a script can be created, edited, saved, and
/// launched", and the only honest way to check the last word is to start the engine. That
/// is too heavy and too machine-specific for the normal suite, so these are opt-in:
///
///   $env:BARLAUNCHER_LIVE = "1"; dotnet test -p:Platform=x64 --filter LiveLaunchTests
///
/// Without that variable they skip. They need a real BAR install and they really do open
/// the game, so they are not something CI should ever run.
/// </summary>
public sealed class LiveLaunchTests
{
    private readonly ITestOutputHelper _output;

    public LiveLaunchTests(ITestOutputHelper output) => _output = output;

    private static bool IsEnabled =>
        Environment.GetEnvironmentVariable("BARLAUNCHER_LIVE") == "1";

    /// <summary>
    /// The engine's wording when a skirmish AI is handed its team. Taken from a real
    /// infolog rather than from the source, since it is the string being matched.
    /// </summary>
    private const string TookOverControl = "took over control of team";

    /// <summary>
    /// The whole Phase 4 path: template → file → edit → save → command line → engine, then
    /// the engine's own infolog as the witness that it loaded the script this app wrote.
    /// </summary>
    [SkippableFact]
    public async Task CreatedScript_LaunchesAndTheEngineLoadsIt()
    {
        Skip.IfNot(IsEnabled, "Set BARLAUNCHER_LIVE=1 to run the live launch tests.");

        var locator = new BarInstallationLocator(
            new TestLogger<BarInstallationLocator>(),
            new SettingsService(new TestLogger<SettingsService>()));

        BarInstallation? installation = await locator.LocateAsync();
        Skip.If(installation is null, "No Beyond All Reason installation was found.");

        var catalog = new EngineCatalog(new TestLogger<EngineCatalog>());
        IReadOnlyList<EngineBuild> engines = await catalog.DiscoverAsync(installation!);

        EngineBuild? engine = engines.FirstOrDefault(e => e.Capabilities.IsComplete)
                              ?? engines.FirstOrDefault(e => e.Capabilities.HasSpring);

        Skip.If(engine is null, "No engine build with spring.exe was found.");
        _output.WriteLine($"Install: {installation!.RootPath}");
        _output.WriteLine($"Engine:  {engine!.Name}");

        // A disposable library, so a live run never touches the real script folder.
        using var temp = new TempDirectory();
        var store = new StartScriptStore(
            new TestLogger<StartScriptStore>(),
            new FakeRecycleBin(),
            Path.Combine(temp.Path, "StartScripts"));

        var serializer = new StartScriptSerializer();
        var factory = new StartScriptFactory();

        // 1. Create from a template.
        StartScriptModel model = factory.Create(StartScriptFactory.AiVersusAi);
        StartScriptFile? created = await store.CreateAsync(
            "live test script",
            serializer.Write(model.Document));

        Assert.NotNull(created);

        // 2. Edit it — through the model, then back through the serializer, which is the
        //    path the form editor will take in Phase 5.
        StartScriptModel edited = new(serializer.Parse(await store.ReadTextAsync(created!.FileName))!.Document!);
        edited.GameStartDelay = 1;

        // 3. Save.
        StartScriptFile? saved = await store.SaveTextAsync(created.FileName, serializer.Write(edited.Document));
        Assert.NotNull(saved);
        Assert.Contains("gamestartdelay=1;", await store.ReadTextAsync(created.FileName));

        // The name deliberately contains spaces: PLAN.md §2.4 is about exactly this path
        // being cut at the first one.
        Assert.Contains(" ", saved!.FullPath);

        // 4. Build the command line.
        var profile = new LaunchProfile
        {
            Id = "live-test",
            Name = "Live test",
            Mode = LaunchMode.Script,
            UseIsolation = true,
        };

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(profile, installation, engine, saved.FullPath);

        Assert.Null(error?.Message);
        _output.WriteLine($"Command: {command!.ToDisplayString()}");

        // 5. Launch it for real, the same way LaunchService does.
        string infologPath = installation.IsolatedInfologPath;
        DateTime before = File.Exists(infologPath) ? File.GetLastWriteTimeUtc(infologPath) : DateTime.MinValue;

        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath)!,
            UseShellExecute = false,
        };

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            // Long enough for the engine to read the script and write its log.
            await WaitForInfologAsync(infologPath, before, TimeSpan.FromSeconds(45));

            string infolog = ReadShared(infologPath);
            _output.WriteLine(FindLine(infolog, "StartScript") ?? "(no StartScript line)");

            // The witness: the engine names the file it loaded, spaces and all.
            Assert.Contains(saved.FullPath, infolog);
        }
        finally
        {
            if (!process!.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// A script that loads is not necessarily a script that plays. This runs the 1v1
    /// template far enough for the engine to resolve the map, the game and the AI, which
    /// is where a wrong <c>shortname</c>, <c>version</c> or team cross-reference shows up.
    /// </summary>
    [SkippableFact]
    public async Task OneVersusAiTemplate_ReachesAiInitialisation()
    {
        Skip.IfNot(IsEnabled, "Set BARLAUNCHER_LIVE=1 to run the live launch tests.");

        var locator = new BarInstallationLocator(
            new TestLogger<BarInstallationLocator>(),
            new SettingsService(new TestLogger<SettingsService>()));

        BarInstallation? installation = await locator.LocateAsync();
        Skip.If(installation is null, "No Beyond All Reason installation was found.");

        var catalog = new EngineCatalog(new TestLogger<EngineCatalog>());
        EngineBuild? engine = (await catalog.DiscoverAsync(installation!))
            .FirstOrDefault(e => e.Capabilities.IsComplete);

        Skip.If(engine is null, "No complete engine build was found.");

        using var temp = new TempDirectory();
        var store = new StartScriptStore(
            new TestLogger<StartScriptStore>(),
            new FakeRecycleBin(),
            Path.Combine(temp.Path, "StartScripts"));

        var serializer = new StartScriptSerializer();
        var factory = new StartScriptFactory();

        StartScriptFile? script = await store.CreateAsync(
            "live 1v1",
            serializer.Write(factory.Create(StartScriptFactory.OneVersusAi).Document));

        Assert.NotNull(script);

        (EngineCommandLine? command, LaunchValidationError? error) = LaunchCommandBuilder.TryBuild(
            new LaunchProfile { Id = "live-1v1", Name = "Live 1v1", Mode = LaunchMode.Script },
            installation!,
            engine!,
            script!.FullPath);

        Assert.Null(error?.Message);

        string infologPath = installation!.IsolatedInfologPath;
        DateTime before = File.Exists(infologPath) ? File.GetLastWriteTimeUtc(infologPath) : DateTime.MinValue;

        var startInfo = new ProcessStartInfo
        {
            FileName = command!.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath)!,
            UseShellExecute = false,
        };

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            // Loading a map and a game takes appreciably longer than reading the script.
            // The game then stops at the start-position screen — startpostype 2 waits for
            // a human to place — so this is as far as an unattended run can get.
            await WaitForInfologAsync(infologPath, before, TimeSpan.FromMinutes(3), TookOverControl);

            string infolog = ReadShared(infologPath);

            foreach (string marker in new[] { "using map", "using game", TookOverControl, "is now ingame" })
            {
                _output.WriteLine(FindLine(infolog, marker) ?? $"(no '{marker}' line)");
            }

            // The AI really loaded and was given a team: this is what proves the
            // template's shortname, version and team cross-references are all right.
            Assert.Contains(TookOverControl, infolog);
            Assert.Contains("Short-Name: \"BARb\", Version: \"stable\"", infolog);

            // The human player reached the game too, so the player/team wiring holds.
            Assert.Contains("is now ingame", infolog);
        }
        finally
        {
            if (!process!.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task WaitForInfologAsync(
        string path,
        DateTime before,
        TimeSpan timeout,
        string marker)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)
                && File.GetLastWriteTimeUtc(path) > before
                && ReadShared(path).Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    private static async Task WaitForInfologAsync(string path, DateTime before, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)
                && File.GetLastWriteTimeUtc(path) > before
                && ReadShared(path).Contains("StartScript", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    /// <summary>The engine holds its infolog open, so it can only be read shared.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? FindLine(string text, string contains) =>
        text.Split('\n').FirstOrDefault(l => l.Contains(contains, StringComparison.OrdinalIgnoreCase))?.Trim();
}
