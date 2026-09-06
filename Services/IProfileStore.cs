using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// The launch-profile library, persisted to <c>profiles.json</c>.
///
/// PLAN.md §9 Q2: there is no separate "default start script" setting. Exactly one
/// profile is the default, and that is what the shell's primary button runs.
/// </summary>
public interface IProfileStore
{
    IReadOnlyList<LaunchProfile> Profiles { get; }

    /// <summary>The pinned profile. Never null once <see cref="LoadAsync"/> has run.</summary>
    LaunchProfile? Default { get; }

    event EventHandler? ProfilesChanged;

    /// <summary>
    /// Loads the profiles, seeding the built-in Chobby default on first run so a fresh
    /// install can launch the game with no configuration.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a profile by <see cref="LaunchProfile.Id"/>.</summary>
    Task SaveAsync(LaunchProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Removes a profile. Built-in profiles cannot be deleted.</summary>
    Task<bool> DeleteAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Pins a profile as the default, clearing the flag on every other.</summary>
    Task SetDefaultAsync(string profileId, CancellationToken cancellationToken = default);
}
