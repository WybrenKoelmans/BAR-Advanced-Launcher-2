using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Folder picking and message dialogs. WinUI has no
/// <c>Microsoft.Win32.OpenFolderDialog</c> and its <c>FolderPicker</c> must be
/// initialised with the window handle (PLAN.md §3.1.2), so all of that lives behind
/// this interface and view models stay constructible in a test.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows the folder picker, returning null when the user cancels.
    /// </summary>
    /// <param name="settingsIdentifier">
    /// Groups the picker's remembered last-used folder. WinRT exposes no way to open at
    /// an arbitrary path, so this is how a picker returns to where it was last used.
    /// </param>
    Task<string?> PickFolderAsync(string? settingsIdentifier = null);

    /// <summary>
    /// Shows the file picker, returning null when the user cancels.
    /// </summary>
    /// <param name="fileTypes">
    /// Extensions including the dot, or <c>"*"</c> for any. A picker with an empty filter
    /// throws, so this must not be empty.
    /// </param>
    Task<string?> PickFileAsync(
        IReadOnlyList<string> fileTypes,
        string? settingsIdentifier = null);

    /// <summary>Shows a message with a single dismiss button.</summary>
    Task ShowMessageAsync(string title, string message, string closeButtonText = "OK");

    /// <summary>Shows a confirmation. Returns true when the primary button is chosen.</summary>
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "Yes",
        string closeButtonText = "Cancel");

    /// <summary>
    /// Asks for a single line of text, returning null when the user cancels.
    /// </summary>
    /// <param name="validate">
    /// Returns an error message for an unacceptable value, or null when it is fine. Run
    /// on every keystroke so a bad script name is refused before the dialog closes rather
    /// than failing silently afterwards.
    /// </param>
    Task<string?> PromptForTextAsync(
        string title,
        string prompt,
        string? initialValue = null,
        string primaryButtonText = "OK",
        Func<string, string?>? validate = null);
}
