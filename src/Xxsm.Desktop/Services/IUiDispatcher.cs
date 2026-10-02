using Avalonia.Threading;

namespace Xxsm.Desktop.Services;

/// <summary>Marshals work onto the thread that owns the UI, as a file watcher's events need.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues work on the UI thread and returns immediately.</summary>
    void Post(Action work);
}

/// <summary>The real dispatcher.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Dispatcher.UIThread.Post(work);
    }
}
