namespace Xxsm.Core.Mods;

/// <summary>Watches a Mods folder for changes made outside XXSM.</summary>
public interface IModsFolderWatcher
{
    /// <summary>Starts watching a Mods folder. A refused watch is returned degraded, not thrown.</summary>
    /// <param name="modsDirectory">The folder to watch.</param>
    /// <param name="debounce">How long the folder must be quiet before a change is reported; never below <see
    /// cref="ModsFolderWatcher.DefaultDebounce"/>.</param>
    /// <returns>The watch. Dispose it to stop.</returns>
    /// <exception cref="ModOperationException">The folder does not exist.</exception>
    IModsFolderWatch Watch(string modsDirectory, TimeSpan? debounce = null);
}

/// <summary>One live watch on a Mods folder.</summary>
public interface IModsFolderWatch : IDisposable
{
    /// <summary>The folder being watched.</summary>
    string ModsDirectory { get; }

    /// <summary>Whether the operating system refused to watch, so the user refreshes by hand.</summary>
    bool IsDegraded { get; }

    /// <summary>What to tell the user when <see cref="IsDegraded"/>, in the system's words, with a way out.</summary>
    string? DegradedReason { get; }

    /// <summary>Raised once per quiet period, with everything that changed in it.</summary>
    event EventHandler<ModsFolderChangedEventArgs>? Changed;

    /// <summary>Raised once, on the system's thread, if a watch that started stops working.</summary>
    event EventHandler? Degraded;
}

/// <summary>What changed in a Mods folder while it was being watched.</summary>
public sealed class ModsFolderChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="paths">The paths that changed, deduplicated.</param>
    /// <param name="at">When the quiet period ended.</param>
    /// <param name="isComplete">Whether the list is everything that changed; false after the system's buffer
    /// overflowed.</param>
    public ModsFolderChangedEventArgs(IReadOnlyList<string> paths, DateTimeOffset at, bool isComplete)
    {
        Paths = paths;
        At = at;
        IsComplete = isComplete;
    }

    /// <summary>The paths that changed, deduplicated and in no particular order.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>When the quiet period ended.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Whether <see cref="Paths"/> is complete; when false, rescan the folder.</summary>
    public bool IsComplete { get; }
}
