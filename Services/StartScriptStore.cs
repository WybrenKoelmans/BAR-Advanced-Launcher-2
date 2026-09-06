using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class StartScriptStore : IStartScriptStore
{
    private const string ScriptExtension = ".txt";

    /// <summary>
    /// UTF-8 without a BOM. The engine's script reader treats the BOM bytes as part of the
    /// first token, so a BOM turns "[game]" into something it does not recognise.
    /// </summary>
    private static readonly UTF8Encoding ScriptEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// CRLF, matching what the engine writes and what the serializer emits.
    ///
    /// WinUI's TextBox hands back a bare CR for every line the user typed, so text
    /// arriving from the raw editor has its line endings mixed: the parts the serializer
    /// wrote are CRLF and the parts the user typed are CR. Saved as-is, the file reads as
    /// one enormous line in every other tool, which for a library meant to be opened in
    /// an external editor is a real problem.
    /// </summary>
    private static string NormaliseLineEndings(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private readonly ILogger<StartScriptStore> _logger;
    private readonly IRecycleBin _recycleBin;

    public StartScriptStore(ILogger<StartScriptStore> logger, IRecycleBin recycleBin)
        : this(logger, recycleBin, AppPaths.StartScriptsFolder)
    {
    }

    /// <summary>Test seam: lets a test use a disposable library folder.</summary>
    public StartScriptStore(ILogger<StartScriptStore> logger, IRecycleBin recycleBin, string libraryPath)
    {
        _logger = logger;
        _recycleBin = recycleBin;
        LibraryPath = libraryPath;
    }

    public string LibraryPath { get; }

    public Task<IReadOnlyList<StartScriptFile>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<StartScriptFile>>(List, cancellationToken);

    private IReadOnlyList<StartScriptFile> List()
    {
        if (!Directory.Exists(LibraryPath))
        {
            return Array.Empty<StartScriptFile>();
        }

        try
        {
            return new DirectoryInfo(LibraryPath)
                .GetFiles("*" + ScriptExtension, SearchOption.TopDirectoryOnly)

                // A wildcard extension also matches longer ones through 8.3 short names,
                // which would list the .txt.tmp of a save in flight as a script.
                .Where(f => f.Extension.Equals(ScriptExtension, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new StartScriptFile(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not list the script library at {Path}.", LibraryPath);
            return Array.Empty<StartScriptFile>();
        }
    }

    public string ResolvePath(string fileName)
    {
        // Guard against a stored name that contains a directory separator: the library
        // is flat, and a profile must not be able to point at an arbitrary file.
        string safeName = Path.GetFileName(fileName);

        if (!safeName.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase))
        {
            safeName += ScriptExtension;
        }

        return Path.Combine(LibraryPath, safeName);
    }

    public async Task<string?> ReadTextAsync(string fileName, CancellationToken cancellationToken = default)
    {
        string path = ResolvePath(fileName);

        if (!File.Exists(path))
        {
            _logger.LogWarning("Cannot read {Path}: it does not exist.", path);
            return null;
        }

        try
        {
            // FileShare.ReadWrite so a script the engine currently has open still opens
            // here — the engine holds _script.txt while a match runs.
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            using var reader = new StreamReader(stream, ScriptEncoding, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read {Path}.", path);
            return null;
        }
    }

    public async Task<StartScriptFile?> SaveTextAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        string path = ResolvePath(fileName);
        string temporary = path + ".tmp";

        try
        {
            Directory.CreateDirectory(LibraryPath);

            await File.WriteAllTextAsync(temporary, NormaliseLineEndings(content), ScriptEncoding, cancellationToken)
                .ConfigureAwait(false);

            // Move over the original in one step: a crash mid-save leaves either the old
            // script or the new one, never a truncated file.
            File.Move(temporary, path, overwrite: true);

            var info = new FileInfo(path);
            _logger.LogInformation("Saved {Name} ({Length} bytes).", info.Name, info.Length);
            return new StartScriptFile(info.Name, info.FullName, info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save {Path}.", path);
            TryDeleteTemporary(temporary);
            return null;
        }
    }

    public async Task<StartScriptFile?> CreateAsync(
        string desiredName,
        string content,
        CancellationToken cancellationToken = default)
    {
        ScriptNameValidation validation = ValidateName(desiredName);

        if (!validation.IsValid)
        {
            _logger.LogWarning("Cannot create a script named '{Name}': {Error}", desiredName, validation.Error);
            return null;
        }

        return await SaveTextAsync(MakeUniqueName(desiredName), content, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StartScriptFile?> DuplicateAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        string? content = await ReadTextAsync(fileName, cancellationToken).ConfigureAwait(false);

        if (content is null)
        {
            return null;
        }

        string baseName = Path.GetFileNameWithoutExtension(ResolvePath(fileName));
        return await SaveTextAsync(MakeUniqueName(baseName + " copy"), content, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<StartScriptFile?> RenameAsync(
        string fileName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        string source = ResolvePath(fileName);
        string destination = ResolvePath(newName);

        if (!File.Exists(source))
        {
            _logger.LogWarning("Cannot rename {Path}: it does not exist.", source);
            return Task.FromResult<StartScriptFile?>(null);
        }

        ScriptNameValidation validation = ValidateName(newName);

        if (!validation.IsValid)
        {
            _logger.LogWarning("Cannot rename to '{Name}': {Error}", newName, validation.Error);
            return Task.FromResult<StartScriptFile?>(null);
        }

        // A rename that only changes case is the same file on Windows, and File.Move
        // would refuse it as an existing destination.
        bool sameFile = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);

        if (!sameFile && File.Exists(destination))
        {
            _logger.LogWarning("Cannot rename to '{Name}': a script by that name already exists.", newName);
            return Task.FromResult<StartScriptFile?>(null);
        }

        try
        {
            File.Move(source, destination, overwrite: sameFile);

            var info = new FileInfo(destination);
            _logger.LogInformation("Renamed {Old} to {New}.", Path.GetFileName(source), info.Name);
            return Task.FromResult<StartScriptFile?>(
                new StartScriptFile(info.Name, info.FullName, info.Length, info.LastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not rename {Path}.", source);
            return Task.FromResult<StartScriptFile?>(null);
        }
    }

    public bool Delete(string fileName) => _recycleBin.Delete(ResolvePath(fileName));

    public async Task<StartScriptFile?> ImportAsync(
        string sourcePath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Cannot import {Path}: it does not exist.", sourcePath);
            return null;
        }

        string destination = ResolvePath(fileName);

        try
        {
            Directory.CreateDirectory(LibraryPath);

            // Copied through a read rather than File.Copy, so the engine holding the
            // source open for writing does not block the import.
            await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await using (FileStream target = File.Create(destination))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(destination);
            _logger.LogInformation("Imported {Source} into the library as {Name}.", sourcePath, info.Name);
            return new StartScriptFile(info.Name, info.FullName, info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not import {Source} into the library.", sourcePath);
            return null;
        }
    }

    public ScriptNameValidation ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ScriptNameValidation.Invalid("The name cannot be empty.");
        }

        string trimmed = name.Trim();

        if (trimmed != Path.GetFileName(trimmed))
        {
            return ScriptNameValidation.Invalid("The name cannot contain a folder path.");
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var offending = trimmed.Where(c => Array.IndexOf(invalid, c) >= 0).Distinct().ToArray();

        if (offending.Length > 0)
        {
            return ScriptNameValidation.Invalid($"The name cannot contain {string.Join(' ', offending)}");
        }

        // Trailing dots and spaces are silently dropped by Windows, which would make the
        // saved name differ from the one the user typed.
        if (trimmed.EndsWith('.'))
        {
            return ScriptNameValidation.Invalid("The name cannot end with a dot.");
        }

        string stem = Path.GetFileNameWithoutExtension(trimmed);

        if (stem.Length == 0)
        {
            return ScriptNameValidation.Invalid("The name cannot be only an extension.");
        }

        if (IsReservedDeviceName(stem))
        {
            return ScriptNameValidation.Invalid($"'{stem}' is a name Windows reserves for a device.");
        }

        return ScriptNameValidation.Valid;
    }

    public string MakeUniqueName(string desiredName)
    {
        string stem = Path.GetFileNameWithoutExtension(ResolvePath(desiredName));

        if (!File.Exists(ResolvePath(stem)))
        {
            return stem;
        }

        // Start at 2 so the second copy of "vs_ai" is "vs_ai 2", not "vs_ai 1".
        for (int suffix = 2; suffix < int.MaxValue; suffix++)
        {
            string candidate = $"{stem} {suffix}";

            if (!File.Exists(ResolvePath(candidate)))
            {
                return candidate;
            }
        }

        return stem;
    }

    private static bool IsReservedDeviceName(string stem) =>
        ReservedDeviceNames.Contains(stem);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not clean up {Path}.", path);
        }
    }
}
