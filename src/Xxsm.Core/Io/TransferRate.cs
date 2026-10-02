namespace Xxsm.Core.Io;

/// <summary>Turns "so many bytes have arrived" reports into a speed and a time remaining. Thread-safe.</summary>
/// <remarks>The speed is over a short trailing window, not the whole transfer.</remarks>
public sealed class TransferRate
{
    /// <summary>How far back the speed is measured, when the caller names no window.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;

    private long _bytes;
    private DateTimeOffset _lastAt;

    /// <summary>Creates the meter.</summary>
    /// <param name="time">Supplies "now". <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="window">How far back to measure. <see cref="DefaultWindow"/> when null.</param>
    public TransferRate(TimeProvider? time = null, TimeSpan? window = null)
    {
        _time = time ?? TimeProvider.System;
        _window = window is { Ticks: > 0 } chosen ? chosen : DefaultWindow;
    }

    /// <summary>How many bytes the last report said had arrived.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>The speed in bytes a second, or null until two reports far enough apart have arrived.</summary>
    public double? BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < 2)
                {
                    return null;
                }

                var first = _samples.Peek();
                var elapsed = _lastAt - first.At;

                return elapsed > TimeSpan.Zero
                    ? Math.Max(_bytes - first.Bytes, 0) / elapsed.TotalSeconds
                    : null;
            }
        }
    }

    /// <summary>Records how many bytes have arrived altogether.</summary>
    /// <param name="bytes">The running total, not the size of this chunk.</param>
    public void Observe(long bytes)
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            _bytes = bytes;
            _lastAt = now;
            _samples.Enqueue((now, bytes));

            // Keep the sample just outside the window too, or a lone sample leaves no speed at all.
            while (_samples.Count > 2 && now - _samples.Peek().At > _window)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>How much longer it should take.</summary>
    /// <param name="totalBytes">How many bytes there are altogether, or null when unknown.</param>
    /// <returns>The estimate, or null when the total or the speed is unknown, or the transfer has stalled.</returns>
    public TimeSpan? Remaining(long? totalBytes)
    {
        if (totalBytes is not { } total || total <= 0)
        {
            return null;
        }

        if (BytesPerSecond is not { } rate || rate <= 0)
        {
            return null;
        }

        var left = total - Bytes;

        return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left / rate);
    }

    /// <summary>Forgets every sample, for a transfer that is starting again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _bytes = 0;
            _lastAt = default;
        }
    }
}
