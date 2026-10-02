using System.Security.Cryptography;
using System.Text;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Packs.Portraits;

/// <summary>The default <see cref="IPortraitSource"/>.</summary>
public sealed class PortraitSource(IAppPaths paths, IPackResourceFetcher fetcher, ILogger logger) : IPortraitSource
{
    private readonly IAppPaths _paths = paths;
    private readonly IPackResourceFetcher _fetcher = fetcher;
    private readonly ILogger _logger = logger.ForContext<PortraitSource>();

    /// <inheritdoc />
    public async Task<Stream?> OpenAsync(
        GameData data, MergedVariant variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(variant);

        if (variant.Image is not { Length: > 0 } image)
        {
            return null;
        }

        try
        {
            if (UntrustedLocation.IsWebAddress(image, out var address))
            {
                return await OpenReferenceModeAsync(address, cancellationToken).ConfigureAwait(false);
            }

            var path = Uri.TryCreate(image, UriKind.Absolute, out var fileUri) && fileUri.IsFile
                ? fileUri.LocalPath
                : Path.IsPathRooted(image) ? image : null;

            return path is null ? null : OpenFile(path, variant.InternalName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not read {Subject}'s picture", variant.InternalName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenImageAsync(
        string? packDirectory, string image, string describe, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        try
        {
            if (UntrustedLocation.IsWebAddress(image, out var address))
            {
                return await OpenReferenceModeAsync(address, cancellationToken).ConfigureAwait(false);
            }

            if (packDirectory is not { Length: > 0 } directory)
            {
                return null;
            }

            // Inside the pack or nothing.
            if (!UntrustedLocation.TryResolveInside(directory, image, out var path))
            {
                _logger.Warning(
                    "{Subject} names a picture at {Image}, outside its pack and not an https address; it is not opened",
                    describe,
                    image);

                return null;
            }

            return OpenFile(path, describe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not read {Subject}'s picture", describe);
            return null;
        }
    }

    /// <summary>Opens a picture only when it is an ordinary file: a device or pipe would hang the grid.</summary>
    private FileStream? OpenFile(string path, string describe)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _logger.Warning("{Subject} declares a picture at {Path}, which does not exist", describe, path);
            return null;
        }

        if (!UntrustedLocation.IsRegularFile(path))
        {
            _logger.Warning("{Subject} declares a picture at {Path}, which is not an ordinary file", describe, path);
            return null;
        }

        return File.OpenRead(path);
    }

    /// <summary>Downloads a reference-mode portrait to the cache once, then reads the cached copy.</summary>
    private async Task<Stream?> OpenReferenceModeAsync(Uri uri, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_paths.CacheDirectory, "portraits", CacheFileName(uri));

        if (!File.Exists(destination))
        {
            try
            {
                // https only, a picture only, 2 MB at most.
                await _fetcher.DownloadPictureAsync(uri, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (PackRegistryException ex)
            {
                _logger.Warning(ex, "Could not fetch reference-mode portrait {Uri}", uri);
                return null;
            }
        }

        return File.OpenRead(destination);
    }

    private static string CacheFileName(Uri uri)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        var extension = Path.GetExtension(uri.AbsolutePath);

        return extension is { Length: > 0 } ? hash + extension : hash;
    }
}
