using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>Finds and validates BAR install roots (PLAN.md §5.1).</summary>
public interface IBarInstallationLocator
{
    /// <summary>
    /// Probes every candidate in priority order and returns all outcomes, valid and
    /// not, so the UI can explain what was tried when nothing was found.
    /// </summary>
    Task<IReadOnlyList<InstallationProbe>> ProbeAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The first valid candidate, or null when none of them validated.</summary>
    Task<BarInstallation?> LocateAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates one directory, typically the one the user just picked.</summary>
    InstallationProbe Validate(string path, InstallationSource source = InstallationSource.ManualPick);
}
