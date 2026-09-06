using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class LaunchService : ILaunchService
{
    private readonly ILogger<LaunchService> _logger;
    private readonly IInstallationContext _installation;
    private readonly IArchiveCatalog _catalog;
    private readonly IStartScriptStore _scripts;
    private readonly IEngineEnvironment _environment;

    private readonly object _gate = new();
    private readonly List<RunningInstance> _instances = new();

    public LaunchService(
        ILogger<LaunchService> logger,
        IInstallationContext installation,
        IArchiveCatalog catalog,
        IStartScriptStore scripts,
        IEngineEnvironment environment)
    {
        _logger = logger;
        _installation = installation;
        _catalog = catalog;
        _scripts = scripts;
        _environment = environment;
    }

    public IReadOnlyList<RunningInstance> Instances
    {
        get
        {
            lock (_gate)
            {
                return _instances.ToArray();
            }
        }
    }

    public event EventHandler? InstancesChanged;

    public (EngineCommandLine? Command, string? Error) Preview(
        LaunchProfile profile,
        EngineBuild? engineOverride = null)
    {
        (EngineCommandLine? command, string? error) = Resolve(profile, engineOverride);
        return (command, error);
    }

    public Task<LaunchResult> LaunchAsync(
        LaunchProfile profile,
        EngineBuild? engineOverride = null,
        CancellationToken cancellationToken = default)
    {
        (EngineCommandLine? command, string? error) = Resolve(profile, engineOverride);

        if (command is null)
        {
            _logger.LogWarning("Cannot launch '{Profile}': {Error}", profile.Name, error);
            return Task.FromResult(LaunchResult.Fail(error ?? "Unknown error."));
        }

        EngineBuild? engine = ResolveEngine(profile, engineOverride);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,

            // The engine resolves some relative paths against its own folder.
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath) ?? string.Empty,

            // Required for ArgumentList to be used at all, and it is ArgumentList that
            // escapes each token (PLAN.md §2.4).
            UseShellExecute = false,
        };

        ApplyEnvironment(startInfo, profile);

        // Chobby delegates its downloads to a launcher socket when it thinks one is
        // there, with no fallback, so a stale link file has to go before it starts.
        if (profile.Mode == LaunchMode.Menu && _installation.Current is { } installationForLink)
        {
            DisableLauncherLink(
                LaunchCommandBuilder.ResolveWriteDirectory(profile, installationForLink), _logger);
        }

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogError(ex, "Could not start {Executable}.", command.ExecutablePath);
            return Task.FromResult(LaunchResult.Fail($"Could not start the engine: {ex.Message}"));
        }

        var instance = new RunningInstance
        {
            ProcessId = process.Id,
            ProfileName = profile.Name,
            EngineName = engine?.Name ?? "(unknown)",
            Mode = profile.Mode,
            Command = command,
            StartedAt = DateTimeOffset.Now,
        };

        lock (_gate)
        {
            _instances.Insert(0, instance);
        }

        TrackExit(process, instance);

        _logger.LogInformation(
            "Launched '{Profile}' as pid {Pid}: {CommandLine}",
            profile.Name,
            instance.ProcessId,
            command.ToDisplayString());

        InstancesChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(LaunchResult.Ok(instance));
    }

    public bool Kill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            _logger.LogInformation("Killed pid {Pid}.", processId);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // ArgumentException means it already exited, which is not a failure worth
            // showing the user as an error.
            _logger.LogDebug(ex, "Could not kill pid {Pid}.", processId);
            return false;
        }
    }

    /// <summary>Records the exit code so a crash can be surfaced (PLAN.md §6.7).</summary>
    private void TrackExit(Process process, RunningInstance instance)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                instance.ExitedAt = DateTimeOffset.Now;

                try
                {
                    instance.ExitCode = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    instance.ExitCode = null;
                }

                if (instance.ExitCode is not (null or 0))
                {
                    _logger.LogWarning(
                        "'{Profile}' (pid {Pid}) exited with code {Code} after {Duration}.",
                        instance.ProfileName,
                        instance.ProcessId,
                        instance.ExitCode,
                        instance.Duration);
                }
                else
                {
                    _logger.LogInformation(
                        "'{Profile}' (pid {Pid}) exited after {Duration}.",
                        instance.ProfileName,
                        instance.ProcessId,
                        instance.Duration);
                }

                InstancesChanged?.Invoke(this, EventArgs.Empty);
                process.Dispose();
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process exited before the handler was attached.
            _logger.LogDebug(ex, "Could not watch pid {Pid} for exit.", instance.ProcessId);
            process.Dispose();
        }
    }

    /// <summary>
    /// Resolves a profile against the current install: engine, menu name and script
    /// path, then hands off to the pure argument builder.
    /// </summary>
    private (EngineCommandLine? Command, string? Error) Resolve(LaunchProfile profile, EngineBuild? engineOverride)
    {
        BarInstallation? installation = _installation.Current;
        if (installation is null)
        {
            return (null, "No Beyond All Reason installation is selected.");
        }

        EngineBuild? engine = ResolveEngine(profile, engineOverride);
        if (engine is null)
        {
            return (null, profile.EngineName is null
                ? "No engine build is selected."
                : $"Engine '{profile.EngineName}' was not found in this installation.");
        }

        LaunchProfile resolved = profile.Clone();

        if (resolved.Mode == LaunchMode.Menu && string.IsNullOrWhiteSpace(resolved.MenuName))
        {
            // The built-in Chobby profile deliberately ships without a menu name, so a
            // fresh install picks whatever Chobby build this machine actually has.
            MenuArchive? menu = _catalog.Index.Menus.FirstOrDefault();
            if (menu is null)
            {
                return (null, "No Chobby menu was found in the archive cache. Open the Content page to build it.");
            }

            resolved.MenuName = menu.Name;
        }

        string? scriptPath = null;
        if (resolved.Mode is LaunchMode.Script or LaunchMode.Headless or LaunchMode.Dedicated)
        {
            if (string.IsNullOrWhiteSpace(resolved.ScriptFileName))
            {
                return (null, "This profile has no start script selected.");
            }

            scriptPath = _scripts.ResolvePath(resolved.ScriptFileName);
        }
        else if (resolved.Mode == LaunchMode.Skirmish)
        {
            return (null, "Skirmish mode is not implemented yet; use a saved script for now.");
        }

        (EngineCommandLine? command, LaunchValidationError? error) =
            LaunchCommandBuilder.TryBuild(resolved, installation, engine, scriptPath);

        return (command, error?.Message);
    }

    /// <summary>
    /// Copies the install's launcher environment onto the run.
    ///
    /// pr-downloader is inside the engine and takes its repository settings from the
    /// environment only, so this is the difference between in-game downloads working and
    /// silently reaching the wrong CDN. Existing variables are not disturbed: only the
    /// names the install's config actually lists are set.
    /// </summary>
    private void ApplyEnvironment(ProcessStartInfo startInfo, LaunchProfile profile) =>
        ApplyEnvironment(startInfo, profile, _installation.Current, _environment);

    /// <inheritdoc cref="ApplyEnvironment(ProcessStartInfo, LaunchProfile)" />
    internal static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        LaunchProfile profile,
        BarInstallation? installation,
        IEngineEnvironment environment)
    {
        if (!profile.UseInstallEnvironment || installation is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> variable in environment.Resolve(installation))
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }
    }

    /// <summary>
    /// The file the official launcher drops in its write directory so Chobby can find its
    /// local control socket.
    /// </summary>
    internal const string LauncherLinkFileName = "sl-connection.json";

    /// <summary>Where a link file is moved to, so the change is reversible.</summary>
    internal const string DisabledLauncherLinkFileName = "sl-connection.json.disabled";

    /// <summary>
    /// Moves a stale <c>sl-connection.json</c> aside before starting Chobby.
    ///
    /// Chobby does not download anything itself when it believes a launcher is listening:
    /// <c>api_download_handler</c> hands every request to <c>WG.WrapperLoopback</c>, which
    /// sends it over that socket. The connector only stands itself down when the file's
    /// host and port are *missing* — a file naming a launcher that is no longer running
    /// leaves it enabled, so every download is posted to a dead socket and simply never
    /// starts. With the file gone Chobby falls back to <c>VFS.DownloadArchive</c>, the
    /// engine's own pr-downloader, which works because the run carries the install's
    /// PRD_* variables.
    ///
    /// Only for menu launches: nothing else reads this file. Renamed rather than deleted,
    /// and the official launcher rewrites it on its next start either way.
    /// </summary>
    internal static bool DisableLauncherLink(string writeDirectory, ILogger logger)
    {
        string link = Path.Combine(writeDirectory, LauncherLinkFileName);

        if (!File.Exists(link))
        {
            return false;
        }

        try
        {
            File.Move(link, Path.Combine(writeDirectory, DisabledLauncherLinkFileName), overwrite: true);

            logger.LogInformation(
                "Moved {File} aside so Chobby downloads through the engine rather than a launcher socket "
                + "that is not listening. The official launcher rewrites it on its next start.",
                link);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Downloads will not work this run, but the launch itself is fine.
            logger.LogWarning(ex, "Could not move {File} aside; Chobby downloads may not start.", link);
            return false;
        }
    }

    private EngineBuild? ResolveEngine(LaunchProfile profile, EngineBuild? engineOverride)
    {
        if (!string.IsNullOrWhiteSpace(profile.EngineName))
        {
            return _installation.Engines.FirstOrDefault(
                e => string.Equals(e.Name, profile.EngineName, StringComparison.OrdinalIgnoreCase));
        }

        return engineOverride ?? _installation.Engines.FirstOrDefault(e => e.Capabilities.IsComplete)
                              ?? _installation.Engines.FirstOrDefault(e => e.Capabilities.HasSpring);
    }
}
