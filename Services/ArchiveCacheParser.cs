using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;
using MoonSharp.Interpreter;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class ArchiveCacheParser : IArchiveCacheParser
{
    private readonly ILogger<ArchiveCacheParser> _logger;

    public ArchiveCacheParser(ILogger<ArchiveCacheParser> logger) => _logger = logger;

    public ArchiveCacheStamp? FindNewestCache(string cacheFolder)
    {
        if (string.IsNullOrWhiteSpace(cacheFolder) || !Directory.Exists(cacheFolder))
        {
            _logger.LogWarning("No archive cache folder at {Path}.", cacheFolder);
            return null;
        }

        try
        {
            // Matched by glob and newest mtime rather than by name, so an
            // ArchiveCache22 -> 23 schema bump is picked up automatically (PLAN.md §9).
            FileInfo? newest = new DirectoryInfo(cacheFolder)
                .GetFiles("*.lua", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is null)
            {
                _logger.LogWarning("No .lua archive cache found in {Path}.", cacheFolder);
                return null;
            }

            return new ArchiveCacheStamp(newest.FullName, newest.Length, newest.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not inspect the archive cache folder {Path}.", cacheFolder);
            return null;
        }
    }

    public Task<ArchiveIndex> ParseAsync(ArchiveCacheStamp stamp, CancellationToken cancellationToken = default) =>
        Task.Run(() => Parse(stamp, cancellationToken), cancellationToken);

    private ArchiveIndex Parse(ArchiveCacheStamp stamp, CancellationToken cancellationToken)
    {
        var index = new ArchiveIndex
        {
            Stamp = stamp,
            ParsedAtUtc = DateTimeOffset.UtcNow,
        };

        string source;
        try
        {
            // FileShare.ReadWrite: the engine may still hold this file open.
            using var stream = new FileStream(
                stamp.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            source = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the archive cache {Path}.", stamp.Path);
            return index;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        Table? archives;

        try
        {
            // CoreModules.None: this is a data file, and nothing in it should be able to
            // reach the filesystem or the OS through the Lua standard library.
            var script = new Script(CoreModules.None);
            DynValue result = script.DoString(source, codeFriendlyName: Path.GetFileName(stamp.Path));

            archives = result.Type == DataType.Table ? result.Table.Get("archives").Table : null;
        }
        catch (Exception ex) when (ex is InterpreterException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not execute the archive cache {Path}.", stamp.Path);
            return index;
        }

        if (archives is null)
        {
            _logger.LogError("The archive cache {Path} did not return a table with an 'archives' key.", stamp.Path);
            return index;
        }

        foreach (DynValue value in archives.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (value.Type == DataType.Table)
            {
                Add(index, value.Table);
            }
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Parsed {Path} ({Megabytes:F1} MB) in {Elapsed} ms: {Games} game(s), {Maps} map(s), {Menus} menu(s), {Other} other.",
            stamp.Path,
            stamp.Length / 1024d / 1024d,
            stopwatch.ElapsedMilliseconds,
            index.Games.Count,
            index.Maps.Count,
            index.Menus.Count,
            index.Other.Count);

        return index;
    }

    private static void Add(ArchiveIndex index, Table archive)
    {
        Table? data = archive.Get("archivedata").Table;
        if (data is null)
        {
            return;
        }

        string? name = Str(data, "name");
        if (string.IsNullOrEmpty(name))
        {
            // Without a name the entry cannot be referenced from a start script.
            return;
        }

        string? fileName = Str(archive, "name");
        string? archivePath = Str(archive, "path");
        string? checksum = Str(archive, "checksum");

        int modType = (int)(Num(data, "modtype") ?? 0);

        switch (modType)
        {
            case (int)ArchiveKind.Game:
                index.Games.Add(new GameArchive
                {
                    Name = name,
                    NamePure = Str(data, "name_pure"),
                    Version = Str(data, "version"),
                    ShortName = Str(data, "shortname"),
                    Description = Str(data, "description"),
                    Author = Str(data, "author"),
                    ArchiveFileName = fileName,
                    ArchivePath = archivePath,
                    Checksum = checksum,
                    Game = Str(data, "game"),
                    ShortGame = Str(data, "shortgame"),
                    Url = Str(data, "url"),
                    Mutator = Str(data, "mutator"),
                    Depends = StrArray(data, "depend"),
                });
                break;

            case (int)ArchiveKind.Map:
                index.Maps.Add(new MapArchive
                {
                    Name = name,
                    NamePure = Str(data, "name_pure"),
                    Version = Str(data, "version"),
                    ShortName = Str(data, "shortname"),
                    Description = Str(data, "description"),
                    Author = Str(data, "author"),
                    ArchiveFileName = fileName,
                    ArchivePath = archivePath,
                    Checksum = checksum,
                    MapFile = Str(data, "mapfile"),
                    MaxMetal = Num(data, "maxmetal"),
                    Gravity = Num(data, "gravity"),
                    TidalStrength = Num(data, "tidalstrength"),
                    ExtractorRadius = Num(data, "extractorradius"),
                    MapHardness = Num(data, "maphardness"),
                    VoidWater = Bool(data, "voidwater"),
                    VoidGround = Bool(data, "voidground"),
                    NotDeformable = Bool(data, "notdeformable"),
                    AutoShowMetal = Bool(data, "autoshowmetal"),
                });
                break;

            case (int)ArchiveKind.Menu:
                index.Menus.Add(new MenuArchive
                {
                    Name = name,
                    NamePure = Str(data, "name_pure"),
                    Version = Str(data, "version"),
                    ShortName = Str(data, "shortname"),
                    Description = Str(data, "description"),
                    Author = Str(data, "author"),
                    ArchiveFileName = fileName,
                    ArchivePath = archivePath,
                    Checksum = checksum,
                    Mutator = Str(data, "mutator"),
                    OnlyLocal = Bool(data, "onlylocal") ?? false,
                    Depends = StrArray(data, "depend"),
                });
                break;

            default:
                // Base content, and any modtype a later engine introduces.
                index.Other.Add(new OtherArchive
                {
                    Name = name,
                    NamePure = Str(data, "name_pure"),
                    Version = Str(data, "version"),
                    ShortName = Str(data, "shortname"),
                    Description = Str(data, "description"),
                    Author = Str(data, "author"),
                    ArchiveFileName = fileName,
                    ArchivePath = archivePath,
                    Checksum = checksum,
                    RawModType = modType,
                });
                break;
        }
    }

    private static string? Str(Table table, string key)
    {
        DynValue value = table.Get(key);
        return value.Type switch
        {
            DataType.String => value.String,
            DataType.Number => value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static double? Num(Table table, string key)
    {
        DynValue value = table.Get(key);
        return value.Type switch
        {
            DataType.Number => value.Number,
            // Several numeric-looking fields, e.g. `modified`, are quoted in the cache.
            DataType.String when double.TryParse(
                value.String,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed) => parsed,
            _ => null,
        };
    }

    private static bool? Bool(Table table, string key)
    {
        DynValue value = table.Get(key);
        return value.Type switch
        {
            DataType.Boolean => value.Boolean,
            DataType.Number => value.Number != 0,
            _ => null,
        };
    }

    private static string[] StrArray(Table table, string key)
    {
        Table? list = table.Get(key).Table;
        if (list is null)
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        foreach (DynValue value in list.Values)
        {
            if (value.Type == DataType.String)
            {
                results.Add(value.String);
            }
        }

        return results.ToArray();
    }
}
