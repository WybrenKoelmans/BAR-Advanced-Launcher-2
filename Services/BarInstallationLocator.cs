using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class BarInstallationLocator : IBarInstallationLocator
{
    private const string InstallFolderName = "Beyond-All-Reason";
    private const string ElectronExe = "Beyond-All-Reason.exe";

    private readonly ILogger<BarInstallationLocator> _logger;
    private readonly ISettingsService _settings;

    public BarInstallationLocator(ILogger<BarInstallationLocator> logger, ISettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task<BarInstallation?> LocateAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InstallationProbe> probes = await ProbeAllAsync(cancellationToken).ConfigureAwait(false);
        InstallationProbe? hit = probes.FirstOrDefault(p => p.IsValid);

        if (hit is null)
        {
            _logger.LogWarning(
                "No BAR installation found. Tried: {Candidates}.",
                string.Join("; ", probes.Select(p => p.Path + " (" + p.Reason + ")")));
            return null;
        }

        _logger.LogInformation("Using BAR installation at {Path}, found via {Source}.", hit.Path, hit.Source);
        return hit.Installation;
    }

    public Task<IReadOnlyList<InstallationProbe>> ProbeAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<InstallationProbe>>(() => ProbeAll(cancellationToken), cancellationToken);

    private IReadOnlyList<InstallationProbe> ProbeAll(CancellationToken cancellationToken)
    {
        var probes = new List<InstallationProbe>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path, InstallationSource source)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(NormalizePath(path)))
            {
                return;
            }

            probes.Add(Validate(path, source));
        }

        // 1. Remembered from a previous session.
        Consider(_settings.Current.ActiveInstallPath, InstallationSource.Remembered);
        foreach (string known in _settings.Current.KnownInstallPaths)
        {
            Consider(known, InstallationSource.Remembered);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. The default installer target, which was the old launcher's only probe.
        Consider(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                InstallFolderName),
            InstallationSource.DefaultLocalAppData);

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Uninstall registry keys, which catch a non-default install location.
        foreach (string path in ProbeRegistry())
        {
            Consider(path, InstallationSource.Registry);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4. Program Files.
        Consider(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallFolderName),
            InstallationSource.ProgramFiles);

        cancellationToken.ThrowIfCancellationRequested();

        // 5. A running engine or launcher, which catches a portable copy.
        foreach (string path in ProbeRunningProcesses())
        {
            Consider(path, InstallationSource.RunningProcess);
        }

        return probes;
    }

    public InstallationProbe Validate(string path, InstallationSource source = InstallationSource.ManualPick)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return InstallationProbe.Invalid(path ?? string.Empty, source, "the path is empty");
        }

        if (!Directory.Exists(path))
        {
            return InstallationProbe.Invalid(path, source, "the folder does not exist");
        }

        string enginesPath = Path.Combine(path, "data", "engine");
        if (!Directory.Exists(enginesPath))
        {
            return InstallationProbe.Invalid(path, source, @"it has no data\engine folder");
        }

        string[] engineDirs;
        try
        {
            engineDirs = Directory.GetDirectories(enginesPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InstallationProbe.Invalid(path, source, @"data\engine could not be read: " + ex.Message);
        }

        if (engineDirs.Length == 0)
        {
            return InstallationProbe.Invalid(path, source, @"data\engine is empty");
        }

        // PLAN.md §5.1: at least one engine folder must actually hold spring.exe.
        bool anyLaunchable = engineDirs.Any(dir => File.Exists(Path.Combine(dir, EngineBuild.SpringExe)));
        if (!anyLaunchable)
        {
            return InstallationProbe.Invalid(
                path, source, @"no folder under data\engine contains " + EngineBuild.SpringExe);
        }

        return InstallationProbe.Valid(path, source);
    }

    private IEnumerable<string> ProbeRegistry()
    {
        var results = new List<string>();

        var roots = new[]
        {
            (Root: Registry.CurrentUser, SubKey: @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Root: Registry.LocalMachine, SubKey: @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach ((RegistryKey root, string subKey) in roots)
        {
            try
            {
                using RegistryKey? uninstall = root.OpenSubKey(subKey);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("InstallLocation") is not string location ||
                        string.IsNullOrWhiteSpace(location))
                    {
                        continue;
                    }

                    string? displayName = entry.GetValue("DisplayName") as string;
                    bool looksLikeBar =
                        name.Contains(InstallFolderName, StringComparison.OrdinalIgnoreCase) ||
                        location.Contains(InstallFolderName, StringComparison.OrdinalIgnoreCase) ||
                        (displayName?.Contains("Beyond All Reason", StringComparison.OrdinalIgnoreCase) ?? false);

                    if (looksLikeBar)
                    {
                        results.Add(location);
                    }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Could not read uninstall keys under {Key}.", root.Name + "\\" + subKey);
            }
        }

        return results;
    }

    private IEnumerable<string> ProbeRunningProcesses()
    {
        var results = new List<string>();

        foreach (string processName in new[] { "Beyond-All-Reason", "spring" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "Could not enumerate processes named {Name}.", processName);
                continue;
            }

            foreach (Process process in processes)
            {
                try
                {
                    string? exePath = process.MainModule?.FileName;
                    if (exePath is not null && FindInstallRootAbove(exePath) is { } root)
                    {
                        results.Add(root);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // A 32/64-bit mismatch, or the process exited between the two calls.
                    _logger.LogDebug(ex, "Could not read the module path of process {Id}.", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Walks up from an engine or launcher binary to the folder that looks like an
    /// install root. A spring.exe sits three levels below it, under data\engine\build.
    /// </summary>
    private static string? FindInstallRootAbove(string exePath)
    {
        DirectoryInfo? dir = new FileInfo(exePath).Directory;

        for (int depth = 0; dir is not null && depth < 5; depth++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "engine")) ||
                File.Exists(Path.Combine(dir.FullName, ElectronExe)))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
