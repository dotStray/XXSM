using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Xxsm.Core.Mods;

namespace Xxsm.Desktop.Services;

/// <summary>Reads a picture off the clipboard.</summary>
public interface IClipboardImageReader
{
    /// <summary>What the clipboard holds that could be a picture.</summary>
    /// <returns>A copied file's path (still to be checked) or copied pixels as PNG; null for neither.</returns>
    Task<PreviewImageSource?> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>The real reader, over the window's Avalonia clipboard.</summary>
public sealed class AvaloniaClipboardImageReader(Func<TopLevel?> topLevel) : IClipboardImageReader
{
    private readonly Func<TopLevel?> _topLevel = topLevel;

    /// <inheritdoc />
    /// <remarks>A copied file wins over pixels: it is the original, not a re-encoded copy.</remarks>
    public async Task<PreviewImageSource?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_topLevel()?.Clipboard is not { } clipboard)
        {
            return null;
        }

        var files = await clipboard.TryGetFilesAsync().ConfigureAwait(true);

        if (files?.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            return PreviewImageSource.FromFile(path);
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var bitmap = await clipboard.TryGetBitmapAsync().ConfigureAwait(true);

        if (bitmap is null)
        {
            return null;
        }

        using var encoded = new MemoryStream();
        bitmap.Save(encoded, new PngBitmapEncoderOptions());

        return PreviewImageSource.FromBytes(encoded.ToArray(), ".png");
    }
}
