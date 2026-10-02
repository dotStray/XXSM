using Avalonia.Media.Imaging;
using Serilog;
using Xxsm.Core.Mods;

namespace Xxsm.Desktop.Services;

/// <summary>A leased bitmap. Dispose it when the bitmap is no longer being shown.</summary>
public interface IBitmapLease : IDisposable
{
    /// <summary>The decoded image. Valid until this lease is disposed.</summary>
    Bitmap Bitmap { get; }
}

/// <summary>The reference-counted, bounded bitmap cache under the portrait and preview caches.</summary>
/// <remarks>An entry is evicted only once nothing leases it, then from a bounded idle list.</remarks>
internal sealed class LeasedBitmapCache : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _decodeWidthPixels;
    private readonly int _idleCapacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _idle = new();

    /// <summary>Creates the cache.</summary>
    /// <param name="logger">Structured log sink, already scoped to the owning cache.</param>
    /// <param name="decodeWidthPixels">The width every image is decoded down to.</param>
    /// <param name="idleCapacity">How many released images stay decoded.</param>
    public LeasedBitmapCache(ILogger logger, int decodeWidthPixels, int idleCapacity)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _decodeWidthPixels = decodeWidthPixels;
        _idleCapacity = idleCapacity;
    }

    /// <summary>How many entries the cache currently holds, leased or idle. For tests.</summary>
    public int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>How many entries are currently idle (released, not yet evicted). For tests.</summary>
    public int IdleCount
    {
        get
        {
            lock (_gate)
            {
                return _idle.Count;
            }
        }
    }

    /// <summary>Leases the image for a key, decoding it if nothing already has.</summary>
    /// <param name="key">What identifies this image. Two callers using one key share a bitmap.</param>
    /// <param name="open">Opens the bytes to decode. Called at most once per cached entry.</param>
    /// <param name="describe">What to call the image in a decode-failure log line.</param>
    /// <param name="cancellationToken">Cancels this lease's wait, and a decode it started; another waiting
    /// lease starts its own.</param>
    /// <returns>A lease to dispose when done, or null when there was nothing to show.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<IBitmapLease?> AcquireAsync(
        string key,
        Func<CancellationToken, Task<Stream?>> open,
        string describe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(open);

        Entry entry;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                existing = new Entry { DecodeTask = DecodeAsync(open, describe, cancellationToken) };
                _entries[key] = existing;
            }

            existing.LeaseCount++;

            if (existing.IdleNode is { } node)
            {
                _idle.Remove(node);
                existing.IdleNode = null;
            }

            entry = existing;
        }

        Bitmap? bitmap;

        try
        {
            bitmap = await entry.DecodeTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A cancelled decode is forgotten, so the next lease asks again instead of seeing nothing.
            Forget(key, entry);
            cancellationToken.ThrowIfCancellationRequested();
            return await AcquireAsync(key, open, describe, cancellationToken).ConfigureAwait(true);
        }

        if (bitmap is not null)
        {
            return new Lease(this, key, bitmap);
        }

        // A negative result stays cached like a real one, so the lease taken still has to be released.
        Release(key);
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<Entry> entries;

        lock (_gate)
        {
            entries = [.. _entries.Values];
            _entries.Clear();
            _idle.Clear();
        }

        foreach (var entry in entries)
        {
            DisposeWhenDecoded(entry);
        }
    }

    /// <summary>Drops an entry whose decode was cancelled, unless it has already been replaced.</summary>
    private void Forget(string key, Entry entry)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            _entries.Remove(key);

            if (entry.IdleNode is { } node)
            {
                _idle.Remove(node);
                entry.IdleNode = null;
            }
        }
    }

    private void Release(string key)
    {
        Entry? evicted = null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.LeaseCount--;

            if (entry.LeaseCount > 0)
            {
                return;
            }

            entry.IdleNode = _idle.AddLast(key);

            while (_idle.Count > _idleCapacity)
            {
                var oldestKey = _idle.First!.Value;
                _idle.RemoveFirst();

                if (_entries.Remove(oldestKey, out var oldest))
                {
                    evicted = oldest;
                }
            }
        }

        if (evicted is not null)
        {
            DisposeWhenDecoded(evicted);
        }
    }

    /// <summary>Disposes a decoded bitmap once its decode finishes, never under the thread making it.</summary>
    private static void DisposeWhenDecoded(Entry entry) =>
        _ = entry.DecodeTask.ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    task.Result?.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task<Bitmap?> DecodeAsync(
        Func<CancellationToken, Task<Stream?>> open, string describe, CancellationToken cancellationToken)
    {
        Stream? stream = null;

        try
        {
            stream = await open(cancellationToken).ConfigureAwait(false);

            if (stream is null)
            {
                return null;
            }

            var toDecode = stream;

            return await Task.Run(
                () =>
                {
                    try
                    {
                        // Refused from the header alone, before any decoder sees it.
                        if (toDecode.CanSeek && PictureSize.TryRead(toDecode, out var size) && size.IsTooLarge)
                        {
                            _logger.Warning(
                                "Not showing the image for {Subject}: {Width} x {Height} pixels", describe, size.Width, size.Height);
                            return null;
                        }

                        return Bitmap.DecodeToWidth(
                            toDecode, _decodeWidthPixels, BitmapInterpolationMode.MediumQuality);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Could not decode the image for {Subject}", describe);
                        return null;
                    }
                    finally
                    {
                        toDecode.Dispose();
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Rethrown, not turned into "nothing to show": the caller tells the two apart.
            stream?.Dispose();
            throw;
        }
    }

    private sealed class Entry
    {
        public required Task<Bitmap?> DecodeTask { get; init; }

        public int LeaseCount { get; set; }

        public LinkedListNode<string>? IdleNode { get; set; }
    }

    private sealed class Lease : IBitmapLease
    {
        private readonly LeasedBitmapCache _cache;
        private readonly string _key;
        private bool _disposed;

        public Lease(LeasedBitmapCache cache, string key, Bitmap bitmap)
        {
            _cache = cache;
            _key = key;
            Bitmap = bitmap;
        }

        public Bitmap Bitmap { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.Release(_key);
        }
    }
}
