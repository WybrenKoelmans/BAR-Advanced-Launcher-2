using System;
using System.IO;

namespace BAR_Advanced_Launcher_2.Models;

/// <summary>
/// Which binaries an engine folder actually contains. PLAN.md §2.2: a local
/// <c>development</c> build ships only <c>spring.exe</c> and <c>unitsync.dll</c>, so
/// commands bind <c>CanExecute</c> to these flags rather than assuming a full build.
/// </summary>
public sealed record EngineCapabilities(
    bool HasSpring,
    bool HasHeadless,
    bool HasDedicated,
    bool HasPrDownloader,
    bool HasUnitsync)
{
    public static readonly EngineCapabilities None = new(false, false, false, false, false);

    /// <summary>A build with every binary present, i.e. a stock release.</summary>
    public bool IsComplete => HasSpring && HasHeadless && HasDedicated && HasPrDownloader && HasUnitsync;

    /// <summary>Human-readable list of what is missing, empty when complete.</summary>
    public string MissingSummary
    {
        get
        {
            if (IsComplete)
            {
                return string.Empty;
            }

            var missing = new System.Collections.Generic.List<string>(5);
            if (!HasSpring) missing.Add(EngineBuild.SpringExe);
            if (!HasHeadless) missing.Add(EngineBuild.HeadlessExe);
            if (!HasDedicated) missing.Add(EngineBuild.DedicatedExe);
            if (!HasPrDownloader) missing.Add(EngineBuild.PrDownloaderExe);
            if (!HasUnitsync) missing.Add(EngineBuild.UnitsyncDll);
            return string.Join(", ", missing);
        }
    }
}

/// <summary>One folder under <c>data\engine</c>.</summary>
public sealed record EngineBuild
{
    public const string SpringExe = "spring.exe";
    public const string HeadlessExe = "spring-headless.exe";
    public const string DedicatedExe = "spring-dedicated.exe";
    public const string PrDownloaderExe = "pr-downloader.exe";
    public const string UnitsyncDll = "unitsync.dll";

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required EngineCapabilities Capabilities { get; init; }

    /// <summary>File version read off <c>spring.exe</c>, null when it is absent or unstamped.</summary>
    public string? FileVersion { get; init; }

    /// <summary>Last write time of <c>spring.exe</c>, or of the folder when it is absent.</summary>
    public DateTime LastWriteUtc { get; init; }

    /// <summary>
    /// Date parsed out of a <c>recoil_YYYY.MM.DD</c> folder name. Null for a local build
    /// such as <c>development</c>, which then sorts on <see cref="LastWriteUtc"/>.
    /// </summary>
    public DateOnly? NamedDate { get; init; }

    public string SpringPath => System.IO.Path.Combine(Path, SpringExe);

    public string HeadlessPath => System.IO.Path.Combine(Path, HeadlessExe);

    public string DedicatedPath => System.IO.Path.Combine(Path, DedicatedExe);

    public string PrDownloaderPath => System.IO.Path.Combine(Path, PrDownloaderExe);

    /// <summary>What the pickers show: name plus a warning when the build is partial.</summary>
    public string DisplayName => Capabilities.IsComplete
        ? Name
        : $"{Name}  (missing {Capabilities.MissingSummary})";

    /// <summary>Freshness key for newest-first ordering.</summary>
    public DateTime SortKey => NamedDate?.ToDateTime(TimeOnly.MinValue) ?? LastWriteUtc;

    public override string ToString() => Name;
}
