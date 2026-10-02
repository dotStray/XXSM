using Serilog;
using Xxsm.Core.Mods;

namespace Xxsm.Desktop.Services;

/// <summary>Decodes a mod's own preview image off the UI thread, into a bounded cache.</summary>
public interface IModPreviewCache
{
    /// <summary>Leases a mod's preview image, decoding it if nothing already has.</summary>
    /// <param name="installed">The mod as the last scan found it.</param>
    /// <param name="cancellationToken">Cancels the search and the decode.</param>
    /// <returns>A lease to dispose once no longer shown, or null when there is none or it could not be
    /// decoded.</returns>
    Task<IBitmapLease?> AcquireAsync(InstalledMod installed, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModPreviewCache"/>.</summary>
public sealed class ModPreviewCache : IModPreviewCache, IDisposable
{
    /// <summary>Decode width in pixels: twice the detail pane's usable width, for a 2x display.</summary>
    internal const int DecodeWidthPixels = 536;

    /// <summary>How many released previews stay decoded: only one shows at a time, and each is large.</summary>
    internal const int IdleCapacity = 6;

    private readonly IModPreviewSource _source;
    private readonly LeasedBitmapCache _cache;

    /// <summary>Creates the cache.</summary>
    /// <param name="source">Finds and opens a mod's preview image.</param>
    /// <param name="logger">Structured log sink.</param>
    /// <param name="idleCapacity">Overrides <see cref="IdleCapacity"/>. For tests.</param>
    public ModPreviewCache(IModPreviewSource source, ILogger logger, int idleCapacity = IdleCapacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _cache = new LeasedBitmapCache(logger.ForContext<ModPreviewCache>(), DecodeWidthPixels, idleCapacity);
    }

    /// <summary>How many entries the cache currently holds, leased or idle. For tests.</summary>
    internal int EntryCount => _cache.EntryCount;

    /// <summary>How many entries are currently idle (released, not yet evicted). For tests.</summary>
    internal int IdleCount => _cache.IdleCount;

    /// <inheritdoc />
    public Task<IBitmapLease?> AcquireAsync(
        InstalledMod installed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installed);

        // Keyed by the declared picture and "no picture" too, or a changed picture would show the old one.
        return _cache.AcquireAsync(
            Key(installed),
            ct => _source.OpenAsync(installed.Path, installed.Config, ct),
            installed.DisplayName,
            cancellationToken);
    }

    /// <summary>A mod's picture as a cache key: its folder and which picture it declares, or none.</summary>
    internal static string Key(InstalledMod installed) =>
        installed.Path + "\n" + (installed.Config?.NoImage == true
            ? "(none)"
            : installed.Config?.ImagePath ?? string.Empty);

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();
}
