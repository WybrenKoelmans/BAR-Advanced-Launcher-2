using System;
using System.IO;

namespace BAR_Advanced_Launcher_2.Infrastructure;

/// <summary>
/// Every location this app writes to. Centralised so no service invents its own
/// path, and so the test project can point them somewhere disposable.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "BAR Advanced Launcher 2";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Root { get; } = Path.Combine(LocalAppData, AppFolderName);

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Memoised parse of ArchiveCache*.lua — see PLAN.md §5.4.</summary>
    public static string ArchiveIndexFile => Path.Combine(Root, "index.json");

    /// <summary>Past launches, for replay — see PLAN.md §6.1.</summary>
    public static string HistoryFile => Path.Combine(Root, "history.json");

    /// <summary>
    /// The script library. The old launcher's folder is deliberately not imported or
    /// shared — the new library starts empty (PLAN.md §9 Q4).
    /// </summary>
    public static string StartScriptsFolder => Path.Combine(Root, "StartScripts");

    /// <summary>Scripts synthesised for a one-off skirmish launch, not library members.</summary>
    public static string GeneratedScriptsFolder => Path.Combine(Root, "Generated");

    public static string LogsFolder => Path.Combine(Root, "logs");

    /// <summary>Creates the folders the app assumes exist. Safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StartScriptsFolder);
        Directory.CreateDirectory(GeneratedScriptsFolder);
        Directory.CreateDirectory(LogsFolder);
    }
}
