using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Every launch this app has made, newest first, persisted to <c>history.json</c>
/// (PLAN.md §6.1).
///
/// The two write methods are deliberately <c>void</c>. They are called from
/// <see cref="ILaunchService"/>, including from a process-exit event on a thread pool
/// thread with nobody left to await a task or observe its exception, so persistence is
/// this store's problem: it records in memory synchronously and writes the file behind
/// the caller, reporting any failure to the log rather than upwards.
/// </summary>
public interface ILaunchHistoryStore
{
    /// <summary>Newest first. A snapshot — safe to enumerate while a launch is recorded.</summary>
    IReadOnlyList<LaunchRecord> Records { get; }

    /// <summary>Raised on any change. May arrive off the UI thread.</summary>
    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a launch, trimming the oldest beyond the retention limit.</summary>
    void Record(LaunchRecord record);

    /// <summary>
    /// Persists changes to a record already added — in practice its exit code and time.
    /// A record that is no longer held is re-added rather than dropped.
    /// </summary>
    void Update(LaunchRecord record);

    Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
