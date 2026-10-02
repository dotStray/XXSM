using System.Globalization;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Mods;

namespace Xxsm.Packs.Pictures;

/// <summary>The default <see cref="IPictureDownloader"/>.</summary>
public sealed class PictureDownloader(
    HttpClient http,
    ILogger logger,
    Func<CancellationToken, Task>? gameBananaTurn = null) : IPictureDownloader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = http;
    private readonly ILogger _logger = logger.ForContext<PictureDownloader>();
    private readonly Func<CancellationToken, Task>? _gameBananaTurn = gameBananaTurn;

    /// <inheritdoc />
    public async Task<PreviewImageSource> DownloadAsync(Uri address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!address.IsAbsoluteUri)
        {
            throw new ModOperationException($"'{address}' is not a full address, so there is nothing to fetch.");
        }

        return address.Scheme switch
        {
            "data" => FromDataAddress(address),
            "file" => PreviewImageSource.FromFile(address.LocalPath),
            "http" or "https" => await FetchAsync(address, cancellationToken).ConfigureAwait(false),
            _ => throw new ModOperationException(
                $"A '{address.Scheme}:' address cannot be fetched. Drag the picture from a web page, or drop a file."),
        };
    }

    /// <summary>The file extension for an image media type, or null when it is not a format XXSM uses.</summary>
    internal static string? ExtensionOf(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" or "image/x-ms-bmp" => ".bmp",
        _ => null,
    };

    private static PreviewImageSource FromDataAddress(Uri address)
    {
        var text = address.OriginalString;
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        var header = comma > 5 ? text[5..comma].Split(';') : [];

        if (header.Length == 0
            || ExtensionOf(header[0]) is not { } extension
            || !header.Contains("base64", StringComparer.OrdinalIgnoreCase))
        {
            throw new ModOperationException(
                "That picture is embedded in the page in a form XXSM cannot read. Save it to a file and drop the file instead.");
        }

        try
        {
            return PreviewImageSource.FromBytes(Convert.FromBase64String(Uri.UnescapeDataString(text[(comma + 1)..])), extension);
        }
        catch (FormatException ex)
        {
            throw new ModOperationException($"That embedded picture is damaged: {ex.Message}", null, ex);
        }
    }

    private async Task<PreviewImageSource> FetchAsync(Uri address, CancellationToken cancellationToken)
    {
        if (_gameBananaTurn is not null
            && (address.Host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase)
                || address.Host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase)))
        {
            await _gameBananaTurn(cancellationToken).ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var response = await _http
                .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ModOperationException(
                    $"{address.Host} answered {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} " +
                    $"{response.ReasonPhrase} for that picture, so nothing was downloaded.",
                    address.ToString());
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var extension = ExtensionOf(mediaType) ?? (mediaType is null ? ExtensionFromPath(address) : null);

            if (extension is null)
            {
                throw new ModOperationException(
                    $"'{address}' is {(mediaType is null ? "not marked as a picture" : "a " + mediaType)}, not a picture " +
                    "XXSM can use. Use a png, jpg, webp, bmp or gif.",
                    address.ToString());
            }

            if (response.Content.Headers.ContentLength > IModPreviewEditor.MaxBytes)
            {
                throw TooLarge(address);
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            // Bounded as it is read: a server that does not give a length must not fill memory.
            while ((read = await body.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);

                if (buffer.Length > IModPreviewEditor.MaxBytes)
                {
                    throw TooLarge(address);
                }
            }

            _logger.Information(
                "Downloaded a dropped picture from {Address}: {Bytes} bytes of {MediaType}",
                address,
                buffer.Length,
                mediaType ?? "(unmarked)");

            return PreviewImageSource.FromBytes(buffer.ToArray(), extension);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModOperationException(
                $"{address.Host} did not send the picture within " +
                $"{((int)Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds, so nothing was downloaded.",
                address.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // IOException too: a connection dropped mid-body arrives as an HttpIOException.
            throw new ModOperationException(
                $"Could not download the picture from {address.Host}: {ex.Message}", address.ToString(), ex);
        }
    }

    private static string? ExtensionFromPath(Uri address)
    {
        var extension = Path.GetExtension(address.AbsolutePath).ToLowerInvariant();

        return IModPreviewEditor.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? extension
            : null;
    }

    private static ModOperationException TooLarge(Uri address) =>
        new(
            $"That picture is larger than " +
            $"{(IModPreviewEditor.MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB. " +
            "A thumbnail does not need to be that big.",
            address.ToString());
}
