using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>One file in the start-script library.</summary>
public sealed record StartScriptFile(string FileName, string FullPath, long Length, DateTime LastWriteUtc)
{
    /// <summary>File name without its extension, which is what the UI shows.</summary>
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(FileName);

    /// <summary>
    /// Pre-formatted for the list. x:Bind is strongly typed and will not coerce a
    /// DateTime to a TextBlock's Text, so the formatting has to happen here.
    /// </summary>
    public string LastWriteText => LastWriteUtc.ToLocalTime().ToString("g");

    public override string ToString() => DisplayName;
}

/// <summary>Why a proposed script name was rejected, or <see cref="Valid"/>.</summary>
public sealed record ScriptNameValidation(bool IsValid, string? Error)
{
    public static readonly ScriptNameValidation Valid = new(true, null);

    public static ScriptNameValidation Invalid(string error) => new(false, error);
}

/// <summary>
/// The start-script library under <c>%LOCALAPPDATA%\BAR Advanced Launcher 2\StartScripts</c>.
///
/// PLAN.md §9 Q4: the old launcher's folder is neither imported nor shared, so this
/// library starts empty. The folder is flat — every name is resolved through
/// <see cref="ResolvePath"/>, which strips any directory part, so nothing stored in a
/// profile or a settings file can point the engine outside the library.
/// </summary>
public interface IStartScriptStore
{
    /// <summary>The library folder. Created on first use.</summary>
    string LibraryPath { get; }

    /// <summary>Scripts in the library, newest first. Empty when the folder has none.</summary>
    Task<IReadOnlyList<StartScriptFile>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Full path of a library script. Returns the path whether or not it exists, so the
    /// caller reports "does not exist" with the path it looked for.
    /// </summary>
    string ResolvePath(string fileName);

    /// <summary>Reads a script's raw text, or null when it is missing or unreadable.</summary>
    Task<string?> ReadTextAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a script's raw text, creating it if needed. Written through a temporary file
    /// and moved into place, so an interrupted save cannot leave a half-written script
    /// that the engine would then refuse to load.
    /// </summary>
    Task<StartScriptFile?> SaveTextAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new script, uniquing <paramref name="desiredName"/> against what is
    /// already there rather than overwriting it.
    /// </summary>
    Task<StartScriptFile?> CreateAsync(
        string desiredName,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a script to "&lt;name&gt; copy", uniqued. Null when the source is missing.</summary>
    Task<StartScriptFile?> DuplicateAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a script. Fails rather than overwriting when the target name is taken, so a
    /// typo cannot silently destroy another script.
    /// </summary>
    Task<StartScriptFile?> RenameAsync(
        string fileName,
        string newName,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a script to the recycle bin, so a mis-click is recoverable.</summary>
    bool Delete(string fileName);

    /// <summary>
    /// Copies an external file into the library under <paramref name="fileName"/>,
    /// replacing any existing script of that name. Used to import a Chobby-configured
    /// match in Phase 6.
    /// </summary>
    Task<StartScriptFile?> ImportAsync(
        string sourcePath,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>Checks a name for the UI before it is committed, so errors appear as you type.</summary>
    ScriptNameValidation ValidateName(string? name);

    /// <summary>
    /// Appends " 2", " 3" … until the name is free. Exposed so the UI can show the name a
    /// create will actually use.
    /// </summary>
    string MakeUniqueName(string desiredName);
}
