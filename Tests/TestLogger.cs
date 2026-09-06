using Microsoft.Extensions.Logging;

namespace BAR_Advanced_Launcher_2.Tests;

/// <summary>A no-op logger, so a service under test needs no logging infrastructure.</summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}

/// <summary>A disposable directory, so a test that writes files leaves nothing behind.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "bar-launcher-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Creates a nested folder and returns its full path.</summary>
    public string Dir(params string[] segments)
    {
        string full = System.IO.Path.Combine(new[] { Path }.Concat(segments).ToArray());
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates an empty file, making its parent folders as needed.</summary>
    public string File(params string[] segments)
    {
        string full = System.IO.Path.Combine(new[] { Path }.Concat(segments).ToArray());
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, string.Empty);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
