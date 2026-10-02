using Xxsm.Packs.Merge;

namespace Xxsm.Desktop.Services;

/// <summary>Decodes a variant's portrait off the UI thread, into a bounded, leased cache.</summary>
public interface IPortraitCache
{
    /// <summary>Leases the variant's portrait, decoding it if nothing already has.</summary>
    /// <param name="data">The merged game data the variant came from.</param>
    /// <param name="variant">The variant whose portrait is wanted.</param>
    /// <param name="cancellationToken">Cancels the decode.</param>
    /// <returns>A handle to dispose when done, or null when there is no portrait or it could not be
    /// decoded.</returns>
    Task<IBitmapLease?> AcquireAsync(
        GameData data, MergedVariant variant, CancellationToken cancellationToken = default);
}
