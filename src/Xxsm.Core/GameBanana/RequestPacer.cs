namespace Xxsm.Core.GameBanana;

/// <summary>Keeps every GameBanana request at least a set gap apart, and holds them after a refusal.</summary>
internal sealed class RequestPacer
{
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // The earliest time the next request may go, in UTC ticks; claimed by compare-and-swap.
    private long _nextTicks;

    /// <summary>Creates the pacer.</summary>
    /// <param name="time">Supplies "now".</param>
    /// <param name="delay">How to wait.</param>
    public RequestPacer(TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    /// <summary>Claims the next slot, and waits until it comes round.</summary>
    /// <param name="gap">The shortest gap between two requests.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task WaitAsync(TimeSpan gap, CancellationToken cancellationToken = default)
    {
        var step = Math.Max(gap.Ticks, 0);
        long slot;

        while (true)
        {
            var claimed = Interlocked.Read(ref _nextTicks);
            slot = Math.Max(claimed, _time.GetUtcNow().UtcTicks);

            if (Interlocked.CompareExchange(ref _nextTicks, slot + step, claimed) == claimed)
            {
                break;
            }
        }

        var wait = TimeSpan.FromTicks(slot - _time.GetUtcNow().UtcTicks);

        if (wait > TimeSpan.Zero)
        {
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Holds every request back for a while, after the site said to. A shorter hold does nothing.</summary>
    public void Hold(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var until = _time.GetUtcNow().UtcTicks + duration.Ticks;

        while (true)
        {
            var claimed = Interlocked.Read(ref _nextTicks);

            if (claimed >= until
                || Interlocked.CompareExchange(ref _nextTicks, until, claimed) == claimed)
            {
                return;
            }
        }
    }

    /// <summary>How long to wait before retrying; a <c>Retry-After</c> is obeyed unjittered, both capped.</summary>
    /// <param name="attempt">Which attempt has just failed, counting from 1.</param>
    /// <param name="retryAfter">What the site asked for, when it asked for anything.</param>
    /// <param name="jitter">A number in 0..1, which spreads retries out.</param>
    /// <returns>The wait, never longer than <see cref="BackoffCeiling"/>.</returns>
    public static TimeSpan BackoffFor(int attempt, TimeSpan? retryAfter, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        if (retryAfter is { } asked && asked > TimeSpan.Zero)
        {
            return asked < BackoffCeiling ? asked : BackoffCeiling;
        }

        var doubling = BackoffBase * Math.Pow(2, Math.Min(attempt - 1, 8));
        var spread = doubling * (0.5 + (Math.Clamp(jitter, 0, 1) * 1.0));

        return TimeSpan.FromSeconds(Math.Min(spread, BackoffCeiling.TotalSeconds));
    }

    /// <summary>The first backoff, in seconds, doubled on each further attempt.</summary>
    public const double BackoffBase = 1;

    /// <summary>The longest XXSM will ever wait before retrying.</summary>
    public static TimeSpan BackoffCeiling { get; } = TimeSpan.FromSeconds(30);
}
