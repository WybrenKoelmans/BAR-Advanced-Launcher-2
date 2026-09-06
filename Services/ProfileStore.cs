using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BAR_Advanced_Launcher_2.Infrastructure;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class ProfileStore : IProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<ProfileStore> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LaunchProfile> _profiles = new();

    public ProfileStore(ILogger<ProfileStore> logger)
        : this(logger, Path.Combine(AppPaths.Root, "profiles.json"))
    {
    }

    /// <summary>Test seam: lets a test persist into a disposable folder.</summary>
    public ProfileStore(ILogger<ProfileStore> logger, string path)
    {
        _logger = logger;
        _path = path;
    }

    public IReadOnlyList<LaunchProfile> Profiles
    {
        get
        {
            lock (_profiles)
            {
                return _profiles.ToArray();
            }
        }
    }

    public LaunchProfile? Default => Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault();

    public event EventHandler? ProfilesChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<LaunchProfile> loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);

            if (loaded.Count == 0)
            {
                loaded.Add(LaunchProfile.CreateDefaultChobbyProfile());
                _logger.LogInformation("Seeded the built-in Chobby profile.");
            }

            NormaliseDefault(loaded);

            lock (_profiles)
            {
                _profiles.Clear();
                _profiles.AddRange(loaded);
            }

            await WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(LaunchProfile profile, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_profiles)
            {
                int existing = _profiles.FindIndex(p => p.Id == profile.Id);
                if (existing >= 0)
                {
                    _profiles[existing] = profile;
                }
                else
                {
                    _profiles.Add(profile);
                }

                NormaliseDefault(_profiles);
            }

            await WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        bool removed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_profiles)
            {
                LaunchProfile? profile = _profiles.FirstOrDefault(p => p.Id == profileId);

                if (profile is null || profile.IsBuiltIn)
                {
                    // Deleting the shipped profile would leave a fresh install with no
                    // way to start the game.
                    removed = false;
                }
                else
                {
                    removed = _profiles.Remove(profile);
                    NormaliseDefault(_profiles);
                }
            }

            if (removed)
            {
                await WriteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (removed)
        {
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public async Task SetDefaultAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_profiles)
            {
                if (!_profiles.Any(p => p.Id == profileId))
                {
                    return;
                }

                foreach (LaunchProfile profile in _profiles)
                {
                    profile.IsDefault = profile.Id == profileId;
                }
            }

            await WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Guarantees exactly one default, even if the file was hand-edited.</summary>
    private static void NormaliseDefault(List<LaunchProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return;
        }

        List<LaunchProfile> defaults = profiles.Where(p => p.IsDefault).ToList();

        if (defaults.Count == 1)
        {
            return;
        }

        foreach (LaunchProfile profile in profiles)
        {
            profile.IsDefault = false;
        }

        // Keep the first one marked, or fall back to the first profile overall.
        (defaults.FirstOrDefault() ?? profiles[0]).IsDefault = true;
    }

    private async Task<List<LaunchProfile>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new List<LaunchProfile>();
        }

        try
        {
            await using FileStream stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer
                .DeserializeAsync<List<LaunchProfile>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? new List<LaunchProfile>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read profiles from {Path}; starting from the built-in set.", _path);
            return new List<LaunchProfile>();
        }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            string temp = _path + ".tmp";
            await using (FileStream stream = File.Create(temp))
            {
                await JsonSerializer
                    .SerializeAsync(stream, Profiles, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write profiles to {Path}.", _path);
        }
    }
}
