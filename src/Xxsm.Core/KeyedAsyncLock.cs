namespace Xxsm.Core;

/// <summary>Makes read-change-write sequences on the same key take turns, while different keys run at once.</summary>
/// <remarks>Within one process only; <see cref="Io.AtomicWriteOptions.Expected"/> is the check across two.</remarks>
public sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Entry> _entries;
    private readonly Lock _gate = new();

    /// <summary>Creates the lock.</summary>
    /// <param name="comparer">How keys compare; <see cref="Io.PathComparer.Instance"/> for paths.</param>
    public KeyedAsyncLock(IEqualityComparer<string>? comparer = null) =>
        _entries = new Dictionary<string, Entry>(comparer ?? StringComparer.Ordinal);

    /// <summary>Waits until no one else holds <paramref name="key"/>, then holds it.</summary>
    /// <param name="key">What is about to be read, changed and written.</param>
    /// <param name="cancellationToken">Stops waiting; the lock is then not held.</param>
    /// <returns>Disposing it lets the next caller in.</returns>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        Entry entry;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    /// <summary>How many keys are held or waited for; for tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private void Leave(string key, Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            entry.Semaphore.Release();
            owner.Leave(key, entry);
        }
    }
}
