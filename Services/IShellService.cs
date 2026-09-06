using System.Threading.Tasks;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Explorer and default-editor integration (PLAN.md §5.2). Behind an interface so
/// view models never touch <c>Process</c> directly.
/// </summary>
public interface IShellService
{
    /// <summary>Opens a folder in Explorer. False when it does not exist or would not open.</summary>
    bool OpenFolder(string path);

    /// <summary>Opens a file with its registered handler, selecting it in Explorer if it is not associated.</summary>
    bool OpenFile(string path);

    /// <summary>Opens Explorer with the file selected rather than opening the file itself.</summary>
    bool RevealInExplorer(string path);

    /// <summary>Puts text on the clipboard, e.g. a full command line for a bug report.</summary>
    Task<bool> SetClipboardTextAsync(string text);
}
