using System;
using Microsoft.UI.Dispatching;

namespace BAR_Advanced_Launcher_2.Services;

public sealed class UiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public UiDispatcher(DispatcherQueue queue) => _queue = queue;

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess)
        {
            action();
            return;
        }

        _queue.TryEnqueue(() => action());
    }
}
