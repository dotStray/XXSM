namespace Xxsm.Packs.Registry;

/// <summary>Fetches a registry's files, over HTTP or from the local filesystem.</summary>
public interface IPackResourceFetcher
{
    /// <summary>Reads a text resource, such as an <c>index.json</c>.</summary>
    /// <exception cref="PackRegistryException">It could not be fetched.</exception>
    Task<string> ReadTextAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>Downloads a resource to a local file, replacing anything already there.</summary>
    /// <param name="uri">Where it lives.</param>
    /// <param name="destination">The file to write.</param>
    /// <param name="progress">Told the bytes so far and, when known, the size; null when nobody is watching.</param>
    /// <param name="expectedBytes">The size the pack source gave, beyond which (with a margin) it stops; null or
    /// 0 for a ceiling of 1 GB.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="PackRegistryException">It could not be downloaded, was too large, or stopped arriving.
    /// Nothing is left at <paramref name="destination"/>.</exception>
    Task<long> DownloadAsync(
        Uri uri,
        string destination,
        IProgress<(long Done, long? Total)>? progress = null,
        long? expectedBytes = null,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads a picture a pack names: https only, an <c>image/*</c>, at most 2 MB.</summary>
    Task<long> DownloadPictureAsync(Uri uri, string destination, CancellationToken cancellationToken = default);

    /// <summary>The largest picture <see cref="DownloadPictureAsync"/> takes: 2 MB.</summary>
    const long MaxPictureBytes = 2 * 1024 * 1024;
}
