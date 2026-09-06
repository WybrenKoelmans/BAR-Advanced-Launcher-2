using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class EngineCatalog : IEngineCatalog
{
    private readonly ILogger<EngineCatalog> _logger;

    public EngineCatalog(ILogger<EngineCatalog> logger) => _logger = logger;

    public Task<IReadOnlyList<EngineBuild>> DiscoverAsync(
        BarInstallation installation,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<EngineBuild>>(() => Discover(installation, cancellationToken), cancellationToken);

    private IReadOnlyList<EngineBuild> Discover(BarInstallation installation, CancellationToken cancellationToken)
    {
        string enginesPath = installation.EnginesPath;

        if (!Directory.Exists(enginesPath))
        {
            _logger.LogWarning("No engine folder at {Path}.", enginesPath);
            return Array.Empty<EngineBuild>();
        }

        var builds = new List<EngineBuild>();

        foreach (string dir in EnumerateEngineFolders(enginesPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            EngineBuild? build = Probe(dir);
            if (build is not null)
            {
                builds.Add(build);
            }
        }

        // Newest first so the freshest build is the default selection (PLAN.md §5.3).
        builds.Sort((a, b) => b.SortKey.CompareTo(a.SortKey));

        _logger.LogInformation(
            "Discovered {Count} engine build(s) in {Path}: {Names}.",
            builds.Count,
            enginesPath,
            string.Join(", ", builds.Select(b => b.Name)));

        return builds;
    }

    private IEnumerable<string> EnumerateEngineFolders(string enginesPath)
    {
        try
        {
            return Directory.GetDirectories(enginesPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // PLAN.md §2.5(3): the old FindEngines swallowed this and returned nothing.
            _logger.LogError(ex, "Could not list engine folders under {Path}.", enginesPath);
            return Array.Empty<string>();
        }
    }

    private EngineBuild? Probe(string dir)
    {
        string name = Path.GetFileName(dir);
        string springPath = Path.Combine(dir, EngineBuild.SpringExe);

        var capabilities = new EngineCapabilities(
            HasSpring: File.Exists(springPath),
            HasHeadless: File.Exists(Path.Combine(dir, EngineBuild.HeadlessExe)),
            HasDedicated: File.Exists(Path.Combine(dir, EngineBuild.DedicatedExe)),
            HasPrDownloader: File.Exists(Path.Combine(dir, EngineBuild.PrDownloaderExe)),
            HasUnitsync: File.Exists(Path.Combine(dir, EngineBuild.UnitsyncDll)));

        if (capabilities == EngineCapabilities.None)
        {
            // Not an engine at all — a stray folder under data\engine.
            _logger.LogDebug("Skipping {Path}: no engine binaries found.", dir);
            return null;
        }

        if (!capabilities.HasSpring)
        {
            _logger.LogWarning(
                "Engine folder {Name} has no {Exe}; it cannot be launched.",
                name,
                EngineBuild.SpringExe);
        }

        return new EngineBuild
        {
            Name = name,
            Path = dir,
            Capabilities = capabilities,
            FileVersion = ReadFileVersion(springPath),
            LastWriteUtc = LastWriteUtc(capabilities.HasSpring ? springPath : dir),
            NamedDate = ParseNamedDate(name),
        };
    }

    private string? ReadFileVersion(string springPath)
    {
        if (!File.Exists(springPath))
        {
            return null;
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(springPath);
            string? version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the file version of {Path}.", springPath);
            return null;
        }
    }

    private DateTime LastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the write time of {Path}.", path);
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Pulls the date out of a <c>recoil_YYYY.MM.DD</c> folder name. A local build such
    /// as <c>development</c> has none, and falls back to the binary's write time.
    /// </summary>
    internal static DateOnly? ParseNamedDate(string name)
    {
        int underscore = name.LastIndexOf('_');
        if (underscore < 0 || underscore == name.Length - 1)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            name[(underscore + 1)..],
            "yyyy.MM.dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateOnly date)
            ? date
            : null;
    }
}
