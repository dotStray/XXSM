using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Xxsm.Core;
using Xxsm.Core.Mods;
using Xxsm.Packs.Pictures;

namespace Xxsm.Desktop.Services;

/// <summary>What a drop carried that could be a picture: a file, pixels, or an address. No Avalonia here.</summary>
public sealed partial record PictureDrop
{
    private static readonly string[] AddressFormats = ["text/html", "text/x-moz-url", "text/uri-list", "text/plain"];

    /// <summary>The dropped image file, when the drop was a file.</summary>
    public string? FilePath { get; init; }

    /// <summary>The picture itself, as PNG, when the drag carried pixels.</summary>
    public ReadOnlyMemory<byte> Pixels { get; init; }

    /// <summary>The picture's address, when the drag carried only that.</summary>
    public Uri? Address { get; init; }

    /// <summary>Why the drag's pixels could not be read, in the platform's words; such a drop is refused.</summary>
    public string? PixelsError { get; init; }

    /// <summary>The text formats an address can arrive in, most telling first.</summary>
    public static IReadOnlyList<string> TextFormats => AddressFormats;

    /// <summary>A drop that was one image file.</summary>
    public static PictureDrop FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PictureDrop { FilePath = path };
    }

    /// <summary>Picks the picture out of what a drag offered: files, then damaged pixels, pixels, text.</summary>
    /// <param name="localFiles">The local files the drag carried, if any.</param>
    /// <param name="pixels">Image pixels the drag carried as PNG, or empty.</param>
    /// <param name="texts">Text the drag carried, keyed by format, such as <c>text/html</c>.</param>
    /// <param name="pixelsError">Why the drag's pixels could not be read, or null.</param>
    /// <returns>The picture, or null when nothing in the drag is one.</returns>
    public static PictureDrop? From(
        IReadOnlyList<string> localFiles,
        ReadOnlyMemory<byte> pixels,
        IReadOnlyDictionary<string, string> texts,
        string? pixelsError = null)
    {
        ArgumentNullException.ThrowIfNull(localFiles);
        ArgumentNullException.ThrowIfNull(texts);

        // Files decide on their own: a folder or an archive here is a mod to install, not a picture.
        if (localFiles.Count > 0)
        {
            return localFiles is [var only] && IModPreviewEditor.IsSupportedImage(only) ? FromFile(only) : null;
        }

        // Damaged pixels are refused, not replaced by fetching the address behind the user's back.
        if (pixelsError is { Length: > 0 })
        {
            return new PictureDrop { PixelsError = pixelsError };
        }

        if (!pixels.IsEmpty)
        {
            return new PictureDrop { Pixels = pixels };
        }

        foreach (var format in AddressFormats)
        {
            if (texts.TryGetValue(format, out var text) && AddressIn(format, text) is { } address)
            {
                return new PictureDrop { Address = address };
            }
        }

        return null;
    }

    /// <summary>Decodes drag text given as bytes: UTF-16 without a mark from Firefox, UTF-8 otherwise.</summary>
    public static string DecodeText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var text = bytes switch
        {
            [0xFF, 0xFE, ..] => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
            [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
            [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
            [_, 0, ..] => Encoding.Unicode.GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes),
        };

        return text.TrimEnd('\0');
    }

    /// <summary>Turns the drop into a picture, fetching it when all it carried was an address.</summary>
    /// <param name="downloader">Fetches an address.</param>
    /// <param name="text">Words the refusal of a damaged picture.</param>
    /// <param name="cancellationToken">Cancels a download.</param>
    /// <exception cref="ModOperationException">The pixels arrived damaged, or the address is not a fetchable
    /// picture.</exception>
    public async Task<PreviewImageSource> ToSourceAsync(
        IPictureDownloader downloader, ITextCatalogue text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(text);

        if (PixelsError is { } reason)
        {
            throw new ModOperationException(text.Format(nameof(Strings.ModImage_DropUnreadable), reason));
        }

        if (FilePath is { } path)
        {
            return PreviewImageSource.FromFile(path);
        }

        if (!Pixels.IsEmpty)
        {
            return PreviewImageSource.FromBytes(Pixels, ".png");
        }

        return Address is { } address
            ? await downloader.DownloadAsync(address, cancellationToken).ConfigureAwait(true)
            : throw new InvalidOperationException("A picture drop carries a file, pixels or an address.");
    }

    private static Uri? AddressIn(string format, string text)
    {
        var candidate = format == "text/html"
            ? ImageSource().Match(text) is { Success: true } match ? WebUtility.HtmlDecode(match.Groups["src"].Value) : null
            : text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => !line.StartsWith('#'));

        return candidate is not null
               && Uri.TryCreate(candidate, UriKind.Absolute, out var address)
               && (address.Scheme is "http" or "https"
                   || (address.Scheme == "data" && candidate.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
            ? address
            : null;
    }

    [GeneratedRegex(
        """<img\b[^>]*?\bsrc\s*=\s*(?:"(?<src>[^"]*)"|'(?<src>[^']*)'|(?<src>[^\s>]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageSource();
}
