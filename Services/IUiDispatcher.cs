using System;

namespace BAR_Advanced_Launcher_2.Services;

/// <summary>
/// Marshals a callback onto the UI thread. Abstracted so view models that observe
/// background events stay constructible in a unit test with no dispatcher queue.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread, now if already on it.</summary>
    void Post(Action action);
}
