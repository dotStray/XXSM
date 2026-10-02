using Serilog;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Portraits;

namespace Xxsm.Desktop.Services;

/// <summary>The default <see cref="IPortraitCache"/>: the portrait key and bytes over the leased cache.</summary>
public sealed class PortraitCache : IPortraitCache, IDisposable
{
    /// <summary>Decode width in pixels: twice the grid tile's 132px portrait height, for a 2x display.</summary>
    internal const int DecodeWidthPixels = 264;

    /// <summary>How many released portraits stay decoded: fixed, and above the largest roster shown.</summary>
    internal const int IdleCapacity = 256;

    private readonly IPortraitSource _source;
    private readonly LeasedBitmapCache _cache;

    /// <summary>Creates the cache.</summary>
    /// <param name="source">Resolves a variant's portrait to bytes.</param>
    /// <param name="logger">Structured log sink.</param>
    /// <param name="idleCapacity">Overrides <see cref="IdleCapacity"/>, for a test of eviction.</param>
    public PortraitCache(IPortraitSource source, ILogger logger, int idleCapacity = IdleCapacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _cache = new LeasedBitmapCache(logger.ForContext<PortraitCache>(), DecodeWidthPixels, idleCapacity);
    }

    /// <summary>How many entries the cache currently holds, leased or idle. For tests.</summary>
    internal int EntryCount => _cache.EntryCount;

    /// <summary>How many entries are currently idle (released, not yet evicted). For tests.</summary>
    internal int IdleCount => _cache.IdleCount;

    /// <inheritdoc />
    public async Task<IBitmapLease?> AcquireAsync(
        GameData data, MergedVariant variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(variant);

        var lease = await _cache.AcquireAsync(
            CacheKey(data, variant),
            ct => _source.OpenAsync(data, variant, ct),
            variant.InternalName,
            cancellationToken).ConfigureAwait(true);

        return lease;
    }

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();

    private static string CacheKey(GameData data, MergedVariant variant) =>
        string.Concat(
            data.GameId.ToUpperInvariant(), "|",
            variant.InternalName.ToUpperInvariant(), "|",
            variant.Image);
}
