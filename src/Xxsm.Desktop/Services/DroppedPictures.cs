using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Xxsm.Core.Mods;

namespace Xxsm.Desktop.Services;

/// <summary>Reads a picture out of a drag from a file manager or a browser, for <see cref="PictureDrop"/>.</summary>
internal static class DroppedPictures
{
    /// <summary>Whether a drag might be a picture, from its formats alone: cheap enough for every drag-over.</summary>
    public static bool MightBePicture(IDataTransfer data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var files = LocalFiles(data);

        if (files.Count > 0)
        {
            return files is [var only] && IModPreviewEditor.IsSupportedImage(only);
        }

        return data.Formats.Any(format =>
            format.Equals(DataFormat.Bitmap) || format.Equals(DataFormat.Text) || AddressFormatOf(format) is not null);
    }

    /// <summary>Reads the picture a drop carried, or null when there is none.</summary>
    public static PictureDrop? Read(IDataTransfer data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var files = LocalFiles(data);

        if (files.Count > 0)
        {
            return PictureDrop.From(files, ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>());
        }

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in data.Items)
        {
            foreach (var format in item.Formats)
            {
                if (AddressFormatOf(format) is not { } key || texts.ContainsKey(key))
                {
                    continue;
                }

                var text = item.TryGetRaw(format) switch
                {
                    string value => value,
                    byte[] bytes => PictureDrop.DecodeText(bytes),
                    _ => null,
                };

                if (text is { Length: > 0 })
                {
                    texts[key] = text;
                }
            }
        }

        if (!texts.ContainsKey("text/plain") && data.TryGetText() is { Length: > 0 } plain)
        {
            texts["text/plain"] = plain;
        }

        // Caught on purpose: the X11 reader throws, rather than returning null, on bytes that do not decode.
        ReadOnlyMemory<byte> pixels;
        string? pixelsError = null;

        try
        {
            pixels = Pixels(data);
        }
        catch (ArgumentException exception)
        {
            pixels = ReadOnlyMemory<byte>.Empty;
            pixelsError = exception.Message;
        }

        return PictureDrop.From([], pixels, texts, pixelsError);
    }

    private static List<string> LocalFiles(IDataTransfer data) =>
        [.. data.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>() ?? []];

    private static string? AddressFormatOf(DataFormat format) =>
        PictureDrop.TextFormats.FirstOrDefault(known =>
            format.Identifier.StartsWith(known, StringComparison.OrdinalIgnoreCase));

    private static ReadOnlyMemory<byte> Pixels(IDataTransfer data)
    {
        using var bitmap = data.TryGetBitmap();

        if (bitmap is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        using var encoded = new MemoryStream();
        bitmap.Save(encoded, new PngBitmapEncoderOptions());
        return encoded.ToArray();
    }
}
