using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// JSON preferences. PLAN.md §2.5(1): the old app loaded preferences twice from its
/// constructor to work around ordering, so loading here is an explicit async step
/// and <see cref="Current"/> is only valid once <see cref="LoadAsync"/> has returned.
/// </summary>
public interface ISettingsService
{
    /// <summary>The in-memory settings. Defaults until <see cref="LoadAsync"/> completes.</summary>
    AppSettings Current { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);
}
