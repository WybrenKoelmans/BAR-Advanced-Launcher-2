using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>Enumerates <c>data\engine\*</c> and probes each folder for binaries.</summary>
public interface IEngineCatalog
{
    /// <summary>
    /// Discovers every engine build under <paramref name="installation"/>, newest first.
    /// Returns an empty list rather than throwing when the folder is missing.
    /// </summary>
    Task<IReadOnlyList<EngineBuild>> DiscoverAsync(
        BarInstallation installation,
        CancellationToken cancellationToken = default);
}
