using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Deleting a file to the recycle bin rather than destroying it (PLAN.md §5.7). Behind an
/// interface so tests do not fill the developer's actual recycle bin.
/// </summary>
public interface IRecycleBin
{
    /// <summary>Sends a file to the recycle bin. False when it is missing or locked.</summary>
    bool Delete(string path);
}

/// <summary>
/// The Windows shell recycle bin, reached through the Visual Basic file services — which
/// is the only thing in the framework that wraps <c>SHFileOperation</c>'s recycle flag.
/// </summary>
public sealed class RecycleBin : IRecycleBin
{
    private readonly ILogger<RecycleBin> _logger;

    public RecycleBin(ILogger<RecycleBin> logger) => _logger = logger;

    public bool Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning("Cannot delete {Path}: it does not exist.", path);
            return false;
        }

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            _logger.LogInformation("Sent {Path} to the recycle bin.", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogError(ex, "Could not recycle {Path}.", path);
            return false;
        }
    }
}
