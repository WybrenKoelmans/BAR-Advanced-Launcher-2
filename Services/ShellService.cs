using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class ShellService : IShellService
{
    private readonly ILogger<ShellService> _logger;
    private readonly IUiDispatcher _dispatcher;

    public ShellService(ILogger<ShellService> logger, IUiDispatcher dispatcher)
    {
        _logger = logger;
        _dispatcher = dispatcher;
    }

    public bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _logger.LogWarning("Cannot open folder {Path}: it does not exist.", path);
            return false;
        }

        return Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    public bool OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning("Cannot open file {Path}: it does not exist.", path);
            return false;
        }

        if (Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }))
        {
            return true;
        }

        // No registered handler for the extension: fall back to showing it in Explorer.
        _logger.LogInformation("No handler for {Path}; revealing it in Explorer instead.", path);
        return RevealInExplorer(path);
    }

    public bool RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            _logger.LogWarning("Cannot reveal {Path}: it does not exist.", path);
            return false;
        }

        var info = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };

        // explorer.exe takes /select, as one token including the comma. ArgumentList
        // quotes each entry, which is what keeps a path with spaces intact (PLAN.md §2.4).
        info.ArgumentList.Add("/select,\"" + path + "\"");
        return Start(info);
    }

    public Task<bool> SetClipboardTextAsync(string text)
    {
        var completion = new TaskCompletionSource<bool>();

        // The clipboard APIs are UI-thread affine.
        _dispatcher.Post(() =>
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(text);
                Clipboard.SetContent(package);
                completion.SetResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write to the clipboard.");
                completion.SetResult(false);
            }
        });

        return completion.Task;
    }

    private bool Start(ProcessStartInfo info)
    {
        try
        {
            using Process? process = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogError(ex, "Shell execute failed for {FileName}.", info.FileName);
            return false;
        }
    }
}
