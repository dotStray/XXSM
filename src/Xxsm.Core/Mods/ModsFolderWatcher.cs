using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModsFolderWatcher"/>, over <see cref="FileSystemWatcher"/>.</summary>
public sealed class ModsFolderWatcher(TimeProvider time, ILogger logger) : IModsFolderWatcher
{
    /// <summary>The shortest quiet period a watch will use: one extraction is hundreds of events.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>The longest a change waits to be reported while the folder keeps changing.</summary>
    public static readonly TimeSpan MaxLatency = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<ModsFolderWatcher>();

    /// <inheritdoc />
    public IModsFolderWatch Watch(string modsDirectory, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        if (!PathComparer.TryResolveExisting(modsDirectory, out var resolved) ||
            !Directory.Exists(resolved))
        {
            throw new ModOperationException(
                $"Cannot watch '{PathDisplay.Show(modsDirectory)}': it does not exist.", modsDirectory);
        }

        var window = debounce is { } requested && requested > DefaultDebounce
            ? requested
            : DefaultDebounce;

        return new ModsFolderWatch(PathComparer.Normalize(resolved), window, _time, _logger);
    }
}

/// <summary>One live watch. Created by <see cref="ModsFolderWatcher"/>.</summary>
internal sealed class ModsFolderWatch : IModsFolderWatch
{
    /// <summary>Larger than the 8 KB default, so an extraction does not overflow the kernel's queue.</summary>
    private const int InternalBufferSize = 64 * 1024;

    private readonly TimeSpan _debounce;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(PathComparer.Instance);

    private FileSystemWatcher? _watcher;
    private ITimer? _timer;
    private DateTimeOffset? _burstStarted;
    private int _delivered;

    /// <summary>How many changes the system has delivered so far, for tests to wait on.</summary>
    internal int DeliveredCount
    {
        get
        {
            lock (_gate)
            {
                return _delivered;
            }
        }
    }
    private bool _overflowed;
    private bool _disposed;

    internal ModsFolderWatch(string modsDirectory, TimeSpan debounce, TimeProvider time, ILogger logger)
    {
        ModsDirectory = modsDirectory;
        _debounce = debounce;
        _time = time;
        _logger = logger;

        try
        {
            _watcher = new FileSystemWatcher(modsDirectory)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                InternalBufferSize = InternalBufferSize,
            };

            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Usually the per-user inotify limit: the app keeps working, with a refresh button.
            Degrade(ex);
        }
    }

    /// <inheritdoc />
    public event EventHandler<ModsFolderChangedEventArgs>? Changed;

    /// <inheritdoc />
    public event EventHandler? Degraded;

    /// <inheritdoc />
    public string ModsDirectory { get; }

    /// <inheritdoc />
    public bool IsDegraded { get; private set; }

    /// <inheritdoc />
    public string? DegradedReason { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_watcher is { } watcher)
        {
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;

            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.Debug(ex, "Stopping the watch on {ModsDirectory} raised an error", ModsDirectory);
            }

            watcher.Dispose();
            _watcher = null;
        }

        _timer?.Dispose();
        _timer = null;
    }

    private void Degrade(Exception ex)
    {
        IsDegraded = true;

        // The limit's advice only when the system names inotify; anything else in its own words.
        var isLimit = ex.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase);

        DegradedReason =
            $"XXSM cannot watch '{PathDisplay.Show(ModsDirectory)}' for changes: {ex.Message} XXSM will keep working — " +
            "use Refresh after changing mods outside the app." +
            (isLimit
                ? " To raise the limit, increase fs.inotify.max_user_watches (and max_user_instances) " +
                  "in /etc/sysctl.conf."
                : string.Empty);

        _logger.Warning(ex, "Falling back to manual refresh for {ModsDirectory}", ModsDirectory);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Record(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Both ends of a rename: that is how enable and disable work.
        Record(e.OldFullPath);
        Record(e.FullPath);
    }

    /// <summary>The system watcher's error. Internal so a test can deliver one.</summary>
    /// <param name="sender">The system watcher.</param>
    /// <param name="e">What went wrong.</param>
    internal void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();

        if (ex is InternalBufferOverflowException)
        {
            // The queue overflowed: the paths so far are not the whole story, so the listener rescans.
            lock (_gate)
            {
                _overflowed = true;
            }

            _logger.Warning(ex, "Change notifications overflowed for {ModsDirectory}", ModsDirectory);
            Schedule();
            return;
        }

        lock (_gate)
        {
            if (_disposed || IsDegraded)
            {
                return;
            }

            Degrade(ex);
        }

        try
        {
            Degraded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception listenerError)
        {
            // A listener's bug is logged, and must not take the process down from the system's thread.
            _logger.Error(listenerError, "A listener threw while handling a failed watch on {ModsDirectory}", ModsDirectory);
        }
    }

    /// <summary>Records a changed path, unless it is XXSM's own bookkeeping in <c>.xxsm/</c>.</summary>
    private void Record(string fullPath)
    {
        if (IsXxsmBookkeeping(fullPath))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(PathComparer.Normalize(fullPath));
            _delivered++;
        }

        Schedule();
    }

    private bool IsXxsmBookkeeping(string fullPath)
    {
        var relative = PathComparer.TryGetRelativePath(ModsDirectory, fullPath);

        if (relative is null)
        {
            return false;
        }

        return relative
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(ModsFolderLayout.IsReservedEntry);
    }

    /// <summary>Restarts the quiet period, so one extraction is one report.</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The quiet period restarts on each change, but never past MaxLatency from the first unreported one.
            var now = _time.GetUtcNow();
            _burstStarted ??= now;
            var left = ModsFolderWatcher.MaxLatency - (now - _burstStarted.Value);
            var due = left < _debounce ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : _debounce;

            if (_timer is null)
            {
                _timer = _time.CreateTimer(_ => Fire(), null, due, Timeout.InfiniteTimeSpan);
                return;
            }

            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        string[] paths;
        bool overflowed;

        lock (_gate)
        {
            if (_disposed || (_pending.Count == 0 && !_overflowed))
            {
                return;
            }

            paths = [.. _pending];
            overflowed = _overflowed;

            _pending.Clear();
            _overflowed = false;
            _burstStarted = null;
        }

        try
        {
            Changed?.Invoke(this, new ModsFolderChangedEventArgs(paths, _time.GetUtcNow(), !overflowed));
        }
        catch (Exception ex)
        {
            // On a timer thread an escaping exception ends the process: logged instead, and the watch runs on.
            _logger.Error(ex, "A listener threw while handling changes in {ModsDirectory}", ModsDirectory);
        }
    }
}
