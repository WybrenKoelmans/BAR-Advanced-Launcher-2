using System;
using System.IO;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>How a candidate install was found. Shown so the user can tell a remembered
/// path from an auto-detected one (PLAN.md §5.1).</summary>
public enum InstallationSource
{
    /// <summary>Recorded in settings from a previous session.</summary>
    Remembered,

    /// <summary>%LOCALAPPDATA%\Programs\Beyond-All-Reason — the default installer target.</summary>
    DefaultLocalAppData,

    /// <summary>An uninstall registry key's InstallLocation.</summary>
    Registry,

    /// <summary>%ProgramFiles%\Beyond-All-Reason.</summary>
    ProgramFiles,

    /// <summary>Derived from a running Beyond-All-Reason.exe or spring.exe.</summary>
    RunningProcess,

    /// <summary>Chosen by the user in the folder picker.</summary>
    ManualPick,
}

/// <summary>A validated BAR install root.</summary>
public sealed record BarInstallation
{
    public required string RootPath { get; init; }

    public required InstallationSource Source { get; init; }

    /// <summary>The engine data dir — the <c>--write-dir</c> target.</summary>
    public string DataPath => Path.Combine(RootPath, "data");

    public string EnginesPath => Path.Combine(DataPath, "engine");

    public string MapsPath => Path.Combine(DataPath, "maps");

    public string GamesPath => Path.Combine(DataPath, "games");

    public string CachePath => Path.Combine(DataPath, "cache");

    /// <summary>Log of the last isolated run — what this launcher produces.</summary>
    public string IsolatedInfologPath => Path.Combine(DataPath, "infolog.txt");

    /// <summary>Log of the last non-isolated run, i.e. the Electron launcher's.</summary>
    public string RootInfologPath => Path.Combine(RootPath, "infolog.txt");

    public string RotatedLogsPath => Path.Combine(DataPath, "log");

    /// <summary>The start script Chobby rewrites each time it starts a skirmish.</summary>
    public string ChobbyScriptPath => Path.Combine(DataPath, "_script.txt");

    /// <summary>
    /// The config the engine actually reads and writes. It lives beside the other
    /// generated files in <c>data</c>, not at the install root: the root copy on this
    /// machine is a zero-byte leftover, while the data one carries the real settings the
    /// official launcher writes, including <c>RapidTagResolutionOrder</c>.
    /// </summary>
    public string SpringSettingsPath => Path.Combine(DataPath, "springsettings.cfg");

    public string DisplayName => Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } leaf
        ? leaf
        : RootPath;

    public override string ToString() => RootPath;
}

/// <summary>
/// The outcome of validating one candidate directory. PLAN.md §5.1: the old service
/// returned a bare null, so a wrong path was indistinguishable from a missing one.
/// </summary>
/// <param name="Path">The candidate that was probed.</param>
/// <param name="Source">Where the candidate came from.</param>
/// <param name="Installation">The install when valid, otherwise null.</param>
/// <param name="Reason">Why it was rejected, otherwise null.</param>
public sealed record InstallationProbe(
    string Path,
    InstallationSource Source,
    BarInstallation? Installation,
    string? Reason)
{
    public bool IsValid => Installation is not null;

    public static InstallationProbe Valid(string path, InstallationSource source) =>
        new(path, source, new BarInstallation { RootPath = path, Source = source }, null);

    public static InstallationProbe Invalid(string path, InstallationSource source, string reason) =>
        new(path, source, null, reason);
}
