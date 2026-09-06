using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// The active install and its engines, shared by every page.
///
/// PLAN.md §2.5(1): the old app called <c>LoadPreferences()</c> twice from its
/// constructor because "engines loaded" and "preferences restored" raced. Here the
/// order is explicit and one-way — load settings, locate the install, discover
/// engines, restore the selection — and nothing observes a half-built state because
/// <see cref="Changed"/> is raised once at the end.
/// </summary>
public interface IInstallationContext
{
    /// <summary>The active install, or null when none was found or it failed to validate.</summary>
    BarInstallation? Current { get; }

    /// <summary>Engines under <see cref="Current"/>, newest first. Empty when there is no install.</summary>
    IReadOnlyList<EngineBuild> Engines { get; }

    /// <summary>Every candidate probed on the last locate, for explaining a failure.</summary>
    IReadOnlyList<InstallationProbe> LastProbes { get; }

    /// <summary>True while a locate or discovery is running.</summary>
    bool IsBusy { get; }

    /// <summary>Raised after <see cref="Current"/> and <see cref="Engines"/> both settle.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Runs the full startup sequence once. Safe to await from several places; only
    /// the first call does the work.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-runs detection from scratch, e.g. after an engine was installed.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches to a specific install root and remembers it. Returns the probe so the
    /// caller can show why a rejected folder was rejected.
    /// </summary>
    Task<InstallationProbe> SetInstallationAsync(
        string rootPath,
        InstallationSource source = InstallationSource.ManualPick,
        CancellationToken cancellationToken = default);
}
