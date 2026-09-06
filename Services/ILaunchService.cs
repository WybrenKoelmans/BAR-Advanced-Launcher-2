using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>A running engine process this app started.</summary>
public sealed class RunningInstance
{
    public required int ProcessId { get; init; }

    public required string ProfileName { get; init; }

    public required string EngineName { get; init; }

    public required LaunchMode Mode { get; init; }

    public required EngineCommandLine Command { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Null while running, otherwise the process exit code.</summary>
    public int? ExitCode { get; set; }

    public DateTimeOffset? ExitedAt { get; set; }

    public bool IsRunning => ExitedAt is null;

    public TimeSpan Duration => (ExitedAt ?? DateTimeOffset.Now) - StartedAt;

    /// <summary>
    /// Carries the mode as well as the profile name: the Launch page lets a profile's
    /// mode be overridden for one run without saving, so the profile name alone would
    /// misdescribe what is actually running.
    /// </summary>
    public string DisplayName => $"{ProfileName} · {Mode} · {EngineName} (pid {ProcessId})";

    /// <summary>Second line in the instance list: running, or how it ended.</summary>
    public string StatusText => IsRunning
        ? $"running for {Duration:mm\\:ss}"
        : $"exited with code {ExitCode?.ToString() ?? "?"} after {Duration:mm\\:ss}";
}

/// <summary>The outcome of a launch attempt.</summary>
public sealed record LaunchResult(bool Success, RunningInstance? Instance, string? Error)
{
    public static LaunchResult Ok(RunningInstance instance) => new(true, instance, null);

    public static LaunchResult Fail(string error) => new(false, null, error);
}

/// <summary>Builds the command line, starts the engine, and tracks what it started.</summary>
public interface ILaunchService
{
    /// <summary>Instances started this session, newest first, including exited ones.</summary>
    IReadOnlyList<RunningInstance> Instances { get; }

    /// <summary>Raised when an instance starts or exits. May arrive off the UI thread.</summary>
    event EventHandler? InstancesChanged;

    /// <summary>
    /// Resolves the profile against the current install and starts the engine.
    /// </summary>
    /// <param name="engineOverride">
    /// Engine to use when the profile does not name one — normally the Launch page's
    /// current selection.
    /// </param>
    /// <param name="scriptPathOverride">
    /// Runs this exact file instead of resolving the profile's script out of the library.
    ///
    /// Needed by Phase 6: a script recovered from an infolog lives wherever the engine
    /// loaded it from — <c>data\_script.txt</c>, or a folder belonging to the old
    /// launcher — and the library resolver strips directories on purpose, so a profile
    /// alone cannot name it.
    /// </param>
    Task<LaunchResult> LaunchAsync(
        LaunchProfile profile,
        EngineBuild? engineOverride = null,
        string? scriptPathOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the command a launch would run, without starting anything. Backs the
    /// "copy full command line" button and the preview on the Launch page.
    /// </summary>
    (EngineCommandLine? Command, string? Error) Preview(
        LaunchProfile profile,
        EngineBuild? engineOverride = null,
        string? scriptPathOverride = null);

    /// <summary>Kills a tracked instance. False when it is already gone.</summary>
    bool Kill(int processId);
}
