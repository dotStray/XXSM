using Serilog;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop.Services;

/// <summary>Decodes Pack Studio's table pictures off the UI thread, keyed apart from the installed pack's.</summary>
public interface IStudioThumbnailCache
{
    /// <summary>Leases a draft picture, decoding it if nothing already has.</summary>
    /// <param name="gameId">The draft's game.</param>
    /// <param name="imagePath">The pack-relative path, such as <c>images/Name.png</c>.</param>
    /// <param name="version">The picture's version, so one replaced under the same name is a new entry.</param>
    /// <param name="cancellationToken">Cancels the read and the decode.</param>
    /// <returns>A lease to dispose once no longer shown, or null when there is nothing to show.</returns>
    Task<IBitmapLease?> AcquireAsync(
        string gameId, string imagePath, string version, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IStudioThumbnailCache"/>.</summary>
public sealed class StudioThumbnailCache(IStudioDraftStore store, ILogger logger) : IStudioThumbnailCache, IDisposable
{
    /// <summary>Decode width in pixels: the size shown when the pointer rests on a row's thumbnail.</summary>
    internal const int DecodeWidthPixels = 128;

    /// <summary>How many released thumbnails stay decoded, for a table closed and opened again.</summary>
    internal const int IdleCapacity = 64;

    private readonly IStudioDraftStore _store = store;
    private readonly LeasedBitmapCache _cache = new LeasedBitmapCache(logger.ForContext<StudioThumbnailCache>(), DecodeWidthPixels, IdleCapacity);

    /// <inheritdoc />
    public Task<IBitmapLease?> AcquireAsync(
        string gameId, string imagePath, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(version);

        return _cache.AcquireAsync(
            string.Concat(gameId.ToUpperInvariant(), "\n", imagePath, "\n", version),
            ct => _store.OpenImageAsync(gameId, imagePath, ct),
            imagePath,
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();
}
