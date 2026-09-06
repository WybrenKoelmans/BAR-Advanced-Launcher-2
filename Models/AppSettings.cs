using System;
using System.Collections.Generic;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// Everything persisted to <c>settings.json</c>. Plain mutable POCO so
/// <c>System.Text.Json</c> round-trips it without converters.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Known install roots. PLAN.md §5.1: devs keep a stable and a test install, so
    /// this is a list with one marked active rather than a single path.
    /// </summary>
    public List<string> KnownInstallPaths { get; set; } = new();

    /// <summary>The active install root, or null to auto-detect on next start.</summary>
    public string? ActiveInstallPath { get; set; }

    public string? SelectedEngineName { get; set; }

    public string? SelectedMenuName { get; set; }

    public string? SelectedGameName { get; set; }

    public string? SelectedMapName { get; set; }

    /// <summary>Id of the profile last shown on the Launch page.</summary>
    public string? SelectedProfileId { get; set; }

    /// <summary>
    /// Whether the Scripts page's Launch button passes <c>--isolation</c>. Kept apart from
    /// the launch profiles because that button means "run this file", not "run my
    /// configured setup" — but persisted, so a developer who works without isolation is
    /// not re-ticking a box every session.
    /// </summary>
    public bool ScriptsUseIsolation { get; set; } = true;

    /// <summary>
    /// The name written into a generated start script's <c>myplayername</c>. Null falls
    /// back to the Windows user name, so a new script is runnable without visiting
    /// settings first.
    /// </summary>
    public string? PlayerName { get; set; }


    /// <summary>Records the active install and keeps the known list free of duplicates.</summary>
    public void RememberInstall(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        if (!KnownInstallPaths.Exists(p => string.Equals(p, rootPath, StringComparison.OrdinalIgnoreCase)))
        {
            KnownInstallPaths.Add(rootPath);
        }

        ActiveInstallPath = rootPath;
    }
}
