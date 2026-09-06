using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BAR_Advanced_Launcher_2.Models;
using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Reads <c>data\config.json</c> — the official launcher's own config — and hands back
/// the <c>env_variables</c> block for this platform.
/// </summary>
public sealed class EngineEnvironment : IEngineEnvironment
{
    /// <summary>
    /// The <c>package.platform</c> value the official launcher uses for Windows. Its
    /// config carries a setup per platform, and only this one's variables apply here.
    /// </summary>
    private const string WindowsPlatform = "win32";

    private readonly ILogger<EngineEnvironment> _logger;

    /// <summary>
    /// Cached by path and write time. The file changes only when the official launcher
    /// updates itself, but a run that picks up a stale CDN would be a confusing failure,
    /// so the timestamp is part of the key rather than caching for the session.
    /// </summary>
    private (string Path, DateTime WriteUtc, IReadOnlyDictionary<string, string> Variables)? _cached;

    private readonly object _gate = new();

    public EngineEnvironment(ILogger<EngineEnvironment> logger) => _logger = logger;

    /// <summary>Where the official launcher keeps its config, relative to the install.</summary>
    public static string ConfigPath(BarInstallation installation) =>
        Path.Combine(installation.DataPath, "config.json");

    public IReadOnlyDictionary<string, string> Resolve(BarInstallation installation)
    {
        string path = ConfigPath(installation);

        try
        {
            if (!File.Exists(path))
            {
                // A hand-assembled install has no launcher config. Not an error: it just
                // means there is nothing to add.
                _logger.LogDebug("No launcher config at {Path}; launching with the inherited environment.", path);
                return Empty;
            }

            DateTime writeUtc = File.GetLastWriteTimeUtc(path);

            lock (_gate)
            {
                if (_cached is { } cached
                    && string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase)
                    && cached.WriteUtc == writeUtc)
                {
                    return cached.Variables;
                }
            }

            IReadOnlyDictionary<string, string> variables = Parse(File.ReadAllText(path));

            lock (_gate)
            {
                _cached = (path, writeUtc, variables);
            }

            if (variables.Count > 0)
            {
                _logger.LogInformation(
                    "Engine environment from {Path}: {Names}.", path, string.Join(", ", variables.Keys));
            }

            return variables;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // The environment is an enhancement, not a precondition: a broken config
            // should degrade to "downloads may not work", never to "cannot launch".
            _logger.LogWarning(ex, "Could not read the launcher config at {Path}.", path);
            return Empty;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Pulls <c>env_variables</c> out of the win32 setups. Every setup in the real file
    /// carries the same three values, so later entries simply overwrite earlier ones
    /// rather than the parser having to decide which setup is the active one.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Parse(string json)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("setups", out JsonElement setups)
            || setups.ValueKind != JsonValueKind.Array)
        {
            return variables;
        }

        foreach (JsonElement setup in setups.EnumerateArray())
        {
            if (!IsForWindows(setup)
                || !setup.TryGetProperty("env_variables", out JsonElement environment)
                || environment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty variable in environment.EnumerateObject())
            {
                // Values are strings in the real file, but a bare true/false or number
                // would still be a usable setting, so anything scalar is taken.
                string? value = variable.Value.ValueKind switch
                {
                    JsonValueKind.String => variable.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                        variable.Value.GetRawText(),
                    _ => null,
                };

                if (value is not null)
                {
                    variables[variable.Name] = value;
                }
            }
        }

        return variables;
    }

    private static bool IsForWindows(JsonElement setup) =>
        setup.TryGetProperty("package", out JsonElement package)
        && package.TryGetProperty("platform", out JsonElement platform)
        && platform.ValueKind == JsonValueKind.String
        && string.Equals(platform.GetString(), WindowsPlatform, StringComparison.OrdinalIgnoreCase);
}
