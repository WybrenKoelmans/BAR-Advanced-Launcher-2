using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BAR_Advanced_Launcher_2.Services.Logging;

/// <summary>
/// The in-memory tail that backs the Log page. PLAN.md §2.5 calls out the old app's
/// silent <c>catch { }</c> blocks; everything that used to be swallowed lands here.
/// </summary>
public interface IAppLogSink
{
    /// <summary>Newest entries last. Capped at <see cref="Capacity"/>.</summary>
    IReadOnlyList<AppLogEntry> Entries { get; }

    event EventHandler<AppLogEntry>? EntryAdded;

    void Add(AppLogEntry entry);
    void Clear();
}

public sealed class AppLogSink : IAppLogSink
{
    public const int Capacity = 5000;

    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _entries = new();

    public event EventHandler<AppLogEntry>? EntryAdded;

    public IReadOnlyList<AppLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Add(AppLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
