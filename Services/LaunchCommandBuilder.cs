using System;
using System.Collections.Generic;
using System.IO;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>Why a profile could not be turned into a command line.</summary>
public sealed record LaunchValidationError(string Message)
{
    public override string ToString() => Message;
}

/// <summary>
/// Turns a profile plus the resolved install and engine into a command line.
/// Deliberately pure and static: the argument shape is the part most worth testing,
/// and it must be checkable without starting a process.
/// </summary>
public static class LaunchCommandBuilder
{
    /// <summary>
    /// Builds the command, or returns the first reason it cannot be built.
    /// </summary>
    /// <param name="scriptPath">
    /// Full path of the script for script-based modes. Ignored for
    /// <see cref="LaunchMode.Menu"/>.
    /// </param>
    public static (EngineCommandLine? Command, LaunchValidationError? Error) TryBuild(
        LaunchProfile profile,
        BarInstallation installation,
        EngineBuild engine,
        string? scriptPath = null)
    {
        string executable = Path.Combine(engine.Path, profile.RequiredExecutable);

        if (!HasRequiredBinary(profile.Mode, engine))
        {
            // PLAN.md §2.2: a local `development` build ships only spring.exe, so this
            // is a normal state to be in, not a corrupt install.
            return (null, new LaunchValidationError(
                $"Engine '{engine.Name}' has no {profile.RequiredExecutable}. " +
                "Pick a different engine build for this mode."));
        }

        var arguments = new List<string>();

        // --write-dir sets where infolog.txt, springsettings.cfg and chobby_config.json
        // are written (SUMMARY.md §2).
        arguments.Add("--write-dir");
        arguments.Add(ResolveWriteDirectory(profile, installation));

        // --config is the engine's "exclusive configuration file": it replaces the
        // springsettings.cfg the write-dir would otherwise supply.
        if (!string.IsNullOrWhiteSpace(profile.ConfigFilePath))
        {
            arguments.Add("--config");
            arguments.Add(profile.ConfigFilePath);
        }

        if (profile.UseIsolation)
        {
            arguments.Add("--isolation");
        }

        if (profile.OnlyLocal && SupportsClientFlags(profile.Mode))
        {
            arguments.Add("--only-local");
        }

        if (profile.UseSafeMode && SupportsClientFlags(profile.Mode))
        {
            arguments.Add("--safemode");
        }

        switch (profile.WindowMode)
        {
            case EngineWindowMode.Windowed when SupportsClientFlags(profile.Mode):
                arguments.Add("--window");
                break;

            case EngineWindowMode.Fullscreen when SupportsClientFlags(profile.Mode):
                arguments.Add("--fullscreen");
                break;
        }

        switch (profile.Mode)
        {
            case LaunchMode.Menu:
                if (string.IsNullOrWhiteSpace(profile.MenuName))
                {
                    return (null, new LaunchValidationError(
                        "No menu selected. Pick a Chobby build on the Launch page."));
                }

                arguments.Add("--menu");
                arguments.Add(profile.MenuName);
                break;

            case LaunchMode.Script:
            case LaunchMode.Skirmish:
            case LaunchMode.Headless:
            case LaunchMode.Dedicated:
                if (string.IsNullOrWhiteSpace(scriptPath))
                {
                    return (null, new LaunchValidationError("No start script selected."));
                }

                if (!File.Exists(scriptPath))
                {
                    return (null, new LaunchValidationError($"The start script '{scriptPath}' does not exist."));
                }

                // The script path is a bare positional argument, kept as its own token.
                arguments.Add(scriptPath);
                break;

            default:
                return (null, new LaunchValidationError($"Launch mode '{profile.Mode}' is not supported."));
        }

        arguments.AddRange(CommandLineTokenizer.Split(profile.ExtraArguments));

        return (new EngineCommandLine(executable, arguments), null);
    }

    /// <summary>
    /// Whether the mode's binary registers the client-side switches
    /// (<c>--window</c>, <c>--fullscreen</c>, <c>--safemode</c>, <c>--only-local</c>).
    ///
    /// spring-dedicated.exe builds a reduced option table and has none of them — its
    /// strings carry only <c>config</c>, <c>isolation</c> and <c>menu</c> — and the
    /// engine refuses to start on an option it does not know. So these are dropped for
    /// that mode rather than passed and hoped for. spring-headless.exe does register
    /// them, which is why only Dedicated is excluded.
    /// </summary>
    private static bool SupportsClientFlags(LaunchMode mode) => mode is not LaunchMode.Dedicated;

    /// <summary>
    /// Where the run writes: the profile's override, or the install's data folder.
    ///
    /// Not just the <c>--write-dir</c> argument — Chobby reads several files relative to
    /// it, so anything that has to inspect or adjust those needs the same answer this
    /// gives the command line.
    /// </summary>
    public static string ResolveWriteDirectory(LaunchProfile profile, BarInstallation installation) =>
        string.IsNullOrWhiteSpace(profile.WriteDirectoryOverride)
            ? installation.DataPath
            : profile.WriteDirectoryOverride;

    /// <summary>Whether the engine has the binary this mode runs.</summary>
    public static bool HasRequiredBinary(LaunchMode mode, EngineBuild engine) => mode switch
    {
        LaunchMode.Headless => engine.Capabilities.HasHeadless,
        LaunchMode.Dedicated => engine.Capabilities.HasDedicated,
        _ => engine.Capabilities.HasSpring,
    };
}
