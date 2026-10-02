using System.Net;
using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Packs.Registry;

/// <summary>The default <see cref="IPackResourceFetcher"/>: <c>HttpClient</c>, or file IO for <c>file://</c>.</summary>
public sealed class PackResourceFetcher(
    HttpClient http,
    ILogger logger,
    TimeSpan? idleTimeout = null) : IPackResourceFetcher
{
    /// <summary>The largest pack source index read: 4 MB, a hundred times the real one.</summary>
    public const long MaxIndexBytes = 4L * 1024 * 1024;

    /// <summary>The largest pack downloaded when the pack source gives no size: 1 GB.</summary>
    public const long MaxPackBytes = 1024L * 1024 * 1024;

    private readonly HttpClient _http = http;
    private readonly ILogger _logger = logger.ForContext<PackResourceFetcher>();
    private readonly TimeSpan _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task<string> ReadTextAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.IsFile)
        {
            var path = uri.LocalPath;

            if (!File.Exists(path))
            {
                throw new PackRegistryException(
                    $"There is no registry file at '{PathDisplay.Show(path)}'.");
            }

            try
            {
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackRegistryException(
                    $"Could not read the registry at '{PathDisplay.Show(path)}': {ex.Message}", ex);
            }
        }

        try
        {
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            EnsureFetched(response, uri);

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var text = new MemoryStream();
            var buffer = new byte[81920];
            int read;

            // Counted as it arrives: an index that does not stop must not fill memory.
            while ((read = await ReadAsync(body, buffer, uri, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (text.Length + read > MaxIndexBytes)
                {
                    throw new PackRegistryException(
                        $"'{uri}' is larger than {MaxIndexBytes / (1024 * 1024)} MB, far more than a pack source's index; " +
                        "it was not read.");
                }

                text.Write(buffer, 0, read);
            }

            text.Position = 0;
            using var reader = new StreamReader(text, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            throw Unreachable(uri, ex);
        }
    }

    /// <inheritdoc />
    public async Task<long> DownloadAsync(
        Uri uri,
        string destination,
        IProgress<(long Done, long? Total)>? progress = null,
        long? expectedBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var ceiling = expectedBytes is > 0 and var expected
            ? expected + Math.Max(1024 * 1024, expected / 20)
            : MaxPackBytes;

        if (uri.IsFile)
        {
            var source = uri.LocalPath;

            if (!File.Exists(source))
            {
                throw new PackRegistryException(
                    $"There is no pack archive at '{PathDisplay.Show(source)}'.");
            }

            try
            {
                File.Copy(source, destination, overwrite: true);

                var length = new FileInfo(destination).Length;
                progress?.Report((length, length));

                return length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PackRegistryException(
                    $"Could not copy the pack from '{PathDisplay.Show(source)}': {ex.Message}", ex);
            }
        }

        // A file of its own, renamed only when whole: a cut-off download leaves nothing.
        var part = $"{destination}.{Guid.NewGuid():n}.part";

        try
        {
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            EnsureFetched(response, uri);

            var total = response.Content.Headers.ContentLength;

            if (total > ceiling)
            {
                throw TooLarge(uri, ceiling);
            }

            long done = 0;

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;

                while ((read = await ReadAsync(stream, buffer, uri, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (done + read > ceiling)
                    {
                        throw TooLarge(uri, ceiling);
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            File.Move(part, destination, overwrite: true);
            _logger.Information("Downloaded {Uri} to {Destination}", uri, destination);

            return done;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            OwnScratch.TryDeleteFile(part, _logger);
            throw Unreachable(uri, ex);
        }
        catch
        {
            OwnScratch.TryDeleteFile(part, _logger);
            throw;
        }
    }

    /// <summary>One read of a body, given up when nothing arrives for the idle timeout.</summary>
    private async Task<int> ReadAsync(Stream body, byte[] buffer, Uri uri, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);

        try
        {
            return await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PackRegistryException(
                $"'{uri}' stopped sending for {_idleTimeout.TotalSeconds:0} seconds, so the download was given up. " +
                "Try again later.");
        }
    }

    private static PackRegistryException TooLarge(Uri uri, long ceiling) =>
        new($"'{uri}' is larger than the {ceiling / (1024 * 1024)} MB the pack source said it would be, so the download " +
            "was stopped. Nothing was installed.");

    /// <inheritdoc />
    public async Task<long> DownloadPictureAsync(Uri uri, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (!Xxsm.Core.Io.UntrustedLocation.IsWebAddress(uri.OriginalString, out _))
        {
            throw new PackRegistryException(
                $"'{uri}' is not an https address, so the picture was not fetched.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var part = $"{destination}.{Guid.NewGuid():n}.part";

        try
        {
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            EnsureFetched(response, uri);

            var mediaType = response.Content.Headers.ContentType?.MediaType;

            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new PackRegistryException(
                    $"'{uri}' is {(mediaType is null ? "not marked as a picture" : "a " + mediaType)}, not a picture, " +
                    "so it was not kept.");
            }

            if (response.Content.Headers.ContentLength > IPackResourceFetcher.MaxPictureBytes)
            {
                throw TooLarge(uri);
            }

            long written;

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;

                // Counted as it arrives: a server that gives no length cannot fill the disk.
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (file.Length + read > IPackResourceFetcher.MaxPictureBytes)
                    {
                        throw TooLarge(uri);
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                written = file.Length;
            }

            // Marked as a picture is not being one: check before it is cached for good.
            await using (var check = File.OpenRead(part))
            {
                if (!Xxsm.Core.Mods.PictureSize.TryRead(check, out _))
                {
                    throw new PackRegistryException(
                        $"'{uri}' did not send a whole picture, so it was not kept.");
                }
            }

            File.Move(part, destination, overwrite: true);
            _logger.Information("Downloaded picture {Uri} to {Destination} ({Bytes} bytes)", uri, destination, written);

            return written;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                   !cancellationToken.IsCancellationRequested)
        {
            OwnScratch.TryDeleteFile(part, _logger);
            throw Unreachable(uri, ex);
        }
        catch
        {
            OwnScratch.TryDeleteFile(part, _logger);
            throw;
        }
    }

    private static PackRegistryException TooLarge(Uri uri) =>
        new($"The picture at '{uri}' is larger than 2 MB, so it was not kept.");

    /// <summary>Turns a non-success status into a message that says what to do about it.</summary>
    private static void EnsureFetched(HttpResponseMessage response, Uri uri)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var reason = response.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                "there is nothing at that address. Check the registry URL.",
            HttpStatusCode.TooManyRequests =>
                "the server is rate-limiting XXSM. Wait a few minutes and try again.",
            HttpStatusCode.Forbidden =>
                "the server refused the request. It may be rate-limiting or the file may be private.",
            _ => $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };

        throw new PackRegistryException($"Could not fetch '{uri}': {reason}");
    }

    /// <summary>The error: what happened, in the operating system's words; the caller decides if it matters.</summary>
    private static PackRegistryException Unreachable(Uri uri, Exception ex) =>
        new($"Could not reach '{uri}': {ex.Message}", ex);
}
