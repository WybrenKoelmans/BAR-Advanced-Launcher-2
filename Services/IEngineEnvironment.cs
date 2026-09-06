using System.Collections.Generic;
using BAR_Advanced_Launcher_2.Models;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// The environment variables a launched engine needs on top of its command line.
///
/// pr-downloader lives inside the engine and reads its repository settings from the
/// environment, not from the command line or springsettings.cfg. Left alone it falls
/// back to the built-in default, <c>repos.springrts.com</c>, which does not carry
/// Beyond All Reason's content — so an engine started without these downloads nothing,
/// while the same engine started by the official launcher downloads fine.
///
/// The values are not hard-coded here: they come from the install's own
/// <c>data\config.json</c>, which the official launcher keeps up to date. That way a
/// changed CDN reaches this launcher without a code change.
/// </summary>
public interface IEngineEnvironment
{
    /// <summary>
    /// The variables to set for a run, or empty when the install has no launcher config
    /// (a hand-assembled install, for instance). Never null, never throws.
    /// </summary>
    IReadOnlyDictionary<string, string> Resolve(BarInstallation installation);
}
