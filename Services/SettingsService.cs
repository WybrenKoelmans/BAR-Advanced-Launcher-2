using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Infrastructure;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsService(ILogger<SettingsService> logger)
        : this(logger, AppPaths.SettingsFile)
    {
    }

    /// <summary>Test seam: lets a test point settings at a disposable file.</summary>
    public SettingsService(ILogger<SettingsService> logger, string path)
    {
        _logger = logger;
        _path = path;
    }

    public AppSettings Current { get; private set; } = new();

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("No settings file at {Path}; starting from defaults.", _path);
            Current = new AppSettings();
            return Current;
        }

        try
        {
            await using FileStream stream = File.Open(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Current = await JsonSerializer
                .DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? new AppSettings();

            _logger.LogInformation("Loaded settings from {Path}.", _path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings must not block startup, but unlike the old
            // app's silent catch the reason is recorded.
            _logger.LogError(ex, "Could not read settings from {Path}; falling back to defaults.", _path);
            Current = new AppSettings();
        }

        return Current;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write to a sibling then move, so a crash mid-write cannot truncate the
            // existing settings into unparseable JSON.
            string temp = _path + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer
                    .SerializeAsync(stream, Current, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
            _logger.LogDebug("Saved settings to {Path}.", _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save settings to {Path}.", _path);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
