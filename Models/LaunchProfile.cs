using System;
using System.Text.Json.Serialization;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// How a profile starts the engine. Serialised by name so <c>profiles.json</c> stays
/// hand-editable, which matters for a dev tool.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LaunchMode>))]
public enum LaunchMode
{
    /// <summary>Boot into Chobby: <c>--menu &lt;menuName&gt;</c>.</summary>
    Menu = 0,

    /// <summary>Run a script from the library.</summary>
    Script = 1,

    /// <summary>Run a script synthesised from the engine/game/map selection (Phase 5).</summary>
    Skirmish = 2,

    /// <summary>A script under <c>spring-headless.exe</c>, with no GPU or audio.</summary>
    Headless = 3,

    /// <summary>A script under <c>spring-dedicated.exe</c>, i.e. host only.</summary>
    Dedicated = 4,
}

/// <summary>
/// Whether a run forces the engine's window state. The engine registers
/// <c>--window</c> and <c>--fullscreen</c> as separate switches with no "use the config"
/// value, so the absence of both is modelled here as <see cref="Default"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EngineWindowMode>))]
public enum EngineWindowMode
{
    /// <summary>Pass neither switch; springsettings.cfg decides.</summary>
    Default = 0,

    /// <summary><c>--window</c>. Windowed keeps the launcher and the log visible.</summary>
    Windowed = 1,

    /// <summary><c>--fullscreen</c>.</summary>
    Fullscreen = 2,
}

/// <summary>
/// A named, re-runnable launch: engine plus what to run plus flags.
///
/// PLAN.md §9 Q2: profiles are the mechanism, and "the default start script" is not a
/// separate concept — the profile with <see cref="IsDefault"/> is what the shell's
/// primary button runs.
/// </summary>
public sealed class LaunchProfile
{
    /// <summary>Stable identity, so renaming a profile does not orphan the default.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New profile";

    public LaunchMode Mode { get; set; } = LaunchMode.Menu;

    /// <summary>
    /// Engine folder name. Null means "whatever the Launch page has selected", which is
    /// what makes a profile usable across engine upgrades.
    /// </summary>
    public string? EngineName { get; set; }

    /// <summary>The menu archive name for <see cref="LaunchMode.Menu"/>.</summary>
    public string? MenuName { get; set; }

    /// <summary>Library script file name, without a directory, for script modes.</summary>
    public string? ScriptFileName { get; set; }

    /// <summary>Game archive name for <see cref="LaunchMode.Skirmish"/>.</summary>
    public string? GameName { get; set; }

    /// <summary>Map archive name for <see cref="LaunchMode.Skirmish"/>.</summary>
    public string? MapName { get; set; }

    /// <summary>
    /// Extra command-line arguments, as typed. Tokenised with quote handling before
    /// being passed on (PLAN.md §6.10).
    /// </summary>
    public string? ExtraArguments { get; set; }

    /// <summary>
    /// Overrides the <c>--write-dir</c> target. Null uses the install's <c>data</c>
    /// folder; a separate value is what §6.4's sandboxes will set.
    /// </summary>
    public string? WriteDirectoryOverride { get; set; }

    /// <summary>
    /// Pass <c>--isolation</c>. The engine's own description is "limit the data-dir
    /// (games &amp; maps) scanner to one directory": the run sees only the archives under
    /// the install, not the ones in the OS user profile. On by default, because a
    /// launcher that quietly picked up a stray archive would make every result suspect.
    /// </summary>
    public bool UseIsolation { get; set; } = true;

    /// <summary>
    /// Pass <c>--safemode</c>: "turns off many things that are known to cause problems".
    /// The first thing to try when a build will not start.
    /// </summary>
    public bool UseSafeMode { get; set; }

    /// <summary>
    /// Pass <c>--only-local</c>, which stops the engine opening a listening socket. Two
    /// runs at once otherwise fight over the same port, so this is what makes a second
    /// instance startable while the first is up.
    /// </summary>
    public bool OnlyLocal { get; set; }

    /// <summary>
    /// Whether to force <c>--window</c> or <c>--fullscreen</c>.
    /// <see cref="EngineWindowMode.Default"/> passes neither and leaves it to
    /// <c>springsettings.cfg</c>.
    /// </summary>
    public EngineWindowMode WindowMode { get; set; } = EngineWindowMode.Default;

    /// <summary>
    /// Pass <c>--config</c>, the engine's "exclusive configuration file". Null uses the
    /// write-dir's own <c>springsettings.cfg</c>. Pointing a run at a throwaway copy is
    /// how a setting gets tested without editing the one every other run reads.
    /// </summary>
    public string? ConfigFilePath { get; set; }

    /// <summary>
    /// Apply the install's own <c>env_variables</c> (the <c>PRD_*</c> pr-downloader
    /// settings) to the run. On by default: without them the engine's downloader falls
    /// back to <c>repos.springrts.com</c>, which carries no Beyond All Reason content,
    /// and in-game downloads silently fail. Off is for reproducing exactly that.
    /// </summary>
    public bool UseInstallEnvironment { get; set; } = true;

    /// <summary>Shipped with the app; can be edited but not deleted.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>The one the shell's primary button runs. Exactly one profile has this.</summary>
    public bool IsDefault { get; set; }

    /// <summary>The engine binary this mode needs.</summary>
    [JsonIgnore]
    public string RequiredExecutable => Mode switch
    {
        LaunchMode.Headless => EngineBuild.HeadlessExe,
        LaunchMode.Dedicated => EngineBuild.DedicatedExe,
        _ => EngineBuild.SpringExe,
    };

    public LaunchProfile Clone() => (LaunchProfile)MemberwiseClone();

    public override string ToString() => Name;

    /// <summary>
    /// The profile a fresh install starts with, so the app launches the game with no
    /// configuration at all. The menu name is resolved at launch time from the archive
    /// catalog, which is why it is left null here.
    /// </summary>
    public static LaunchProfile CreateDefaultChobbyProfile() => new()
    {
        Id = "builtin-chobby",
        Name = "Chobby (menu)",
        Mode = LaunchMode.Menu,
        IsBuiltIn = true,
        IsDefault = true,
        UseIsolation = true,
    };
}
