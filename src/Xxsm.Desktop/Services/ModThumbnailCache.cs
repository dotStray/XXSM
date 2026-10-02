using Serilog;
using Xxsm.Core.Mods;

namespace Xxsm.Desktop.Services;

/// <summary>Mods' pictures small, for tiles: a profile's mods and the mod picker.</summary>
public interface IModThumbnailCache
{
    /// <summary>Leases a mod's picture at tile size, decoding it if nothing already has.</summary>
    /// <param name="installed">The mod as the last scan found it.</param>
    /// <param name="cancellationToken">Cancels the search and the decode.</param>
    /// <returns>A lease to dispose once the tile is gone, or null when the mod has no picture.</returns>
    Task<IBitmapLease?> AcquireAsync(InstalledMod installed, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModThumbnailCache"/>.</summary>
public sealed class ModThumbnailCache(IModPreviewSource source, ILogger logger) : IModThumbnailCache, IDisposable
{
    /// <summary>Decode width in pixels, for a tile picture 150 wide.</summary>
    internal const int DecodeWidthPixels = 192;

    /// <summary>How many released thumbnails stay decoded, so reopening a profile is instant.</summary>
    internal const int IdleCapacity = 64;

    private readonly IModPreviewSource _source = source;
    private readonly LeasedBitmapCache _cache = new LeasedBitmapCache(logger.ForContext<ModThumbnailCache>(), DecodeWidthPixels, IdleCapacity);

    /// <inheritdoc />
    public Task<IBitmapLease?> AcquireAsync(InstalledMod installed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installed);

        return _cache.AcquireAsync(
            ModPreviewCache.Key(installed),
            ct => _source.OpenAsync(installed.Path, installed.Config, ct),
            installed.DisplayName,
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();
}
