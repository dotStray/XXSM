using System.Globalization;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>A picture the user chose for a mod: a file on disk, or image bytes from the clipboard.</summary>
public sealed record PreviewImageSource
{
    private PreviewImageSource(string? filePath, ReadOnlyMemory<byte> bytes, string extension)
    {
        FilePath = filePath;
        Bytes = bytes;
        Extension = extension;
    }

    /// <summary>The image file, when the picture came from one.</summary>
    public string? FilePath { get; }

    /// <summary>The encoded image, when the picture came without a file.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The file extension to store it under, with its leading dot, in lower case.</summary>
    public string Extension { get; }

    /// <summary>A picture that is a file on disk.</summary>
    /// <param name="path">The image file.</param>
    /// <returns>The source.</returns>
    public static PreviewImageSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new PreviewImageSource(path, ReadOnlyMemory<byte>.Empty, Path.GetExtension(path).ToLowerInvariant());
    }

    /// <summary>A picture that is already-encoded bytes, such as a PNG off the clipboard.</summary>
    /// <param name="bytes">The encoded image.</param>
    /// <param name="extension">What format they are in, for example <c>.png</c>.</param>
    /// <returns>The source.</returns>
    public static PreviewImageSource FromBytes(ReadOnlyMemory<byte> bytes, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return new PreviewImageSource(
            null, bytes, (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant());
    }

    /// <summary>Reads the picture, refusing anything XXSM cannot use as one, for mods and characters alike.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The encoded image.</returns>
    /// <exception cref="ModOperationException">The format is unsupported, the file is missing, empty or over <see
    /// cref="IModPreviewEditor.MaxBytes"/>, or it could not be read; with the system's error text.</exception>
    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!IModPreviewEditor.SupportedExtensions.Contains(Extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ModOperationException(
                $"'{FilePath ?? Extension}' is not a picture XXSM can use. Use " +
                string.Join(", ", IModPreviewEditor.SupportedExtensions) + ".",
                FilePath);
        }

        if (FilePath is not { } path)
        {
            return Bytes.Length switch
            {
                0 => throw new ModOperationException("The picture on the clipboard is empty."),
                > (int)IModPreviewEditor.MaxBytes => throw TooLarge("The picture on the clipboard", null),
                _ => Bytes.ToArray(),
            };
        }

        if (!PathComparer.TryResolveExisting(path, out var resolved) || !File.Exists(resolved))
        {
            throw new ModOperationException($"There is no picture at '{PathDisplay.Show(path)}'.", path);
        }

        try
        {
            var length = new FileInfo(resolved).Length;

            if (length == 0)
            {
                throw new ModOperationException($"'{PathDisplay.Show(resolved)}' is empty, so it cannot be a picture.", resolved);
            }

            if (length > IModPreviewEditor.MaxBytes)
            {
                throw TooLarge($"'{resolved}'", resolved);
            }

            return await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read the picture '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }
    }

    private static ModOperationException TooLarge(string what, string? path) =>
        new(
            $"{what} is larger than {(IModPreviewEditor.MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB. " +
            "A thumbnail does not need to be that big.",
            path);
}

/// <summary>Sets a mod's picture: a copy in its <c>.xxsm/</c> folder, declared in its <c>mod.json</c>.</summary>
/// <remarks>The author's own files are never touched; a picture XXSM stored earlier goes to the trash.</remarks>
public interface IModPreviewEditor
{
    /// <summary>The image formats a preview may be, with their leading dots.</summary>
    static IReadOnlyList<string> SupportedExtensions { get; } = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    /// <summary>The largest image accepted, in bytes.</summary>
    const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>Whether a path looks like an image this accepts, from its extension alone.</summary>
    /// <param name="path">The path to test. Need not exist.</param>
    /// <returns><see langword="true"/> for a supported image extension.</returns>
    static bool IsSupportedImage(string? path) =>
        path is { Length: > 0 }
        && SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes an image the mod's preview.</summary>
    /// <param name="modFolder">The mod's own folder. Must exist.</param>
    /// <param name="image">The picture.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>The preview as the detail pane will now find it.</returns>
    /// <exception cref="ModOperationException">The mod folder is missing, the image is unusable, or a write failed;
    /// with the system's error text.</exception>
    Task<ModPreview> SetAsync(string modFolder, PreviewImageSource image, CancellationToken cancellationToken = default);

    /// <summary>Removes the mod's picture by recording <c>noImage</c>, so no search finds another.</summary>
    /// <param name="modFolder">The mod's own folder. Must exist.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns><see langword="true"/> when the mod had a picture; <see langword="false"/> when it already had
    /// none.</returns>
    /// <exception cref="ModOperationException">The mod folder is missing, or the metadata could not be written; with
    /// the system's error text.</exception>
    Task<bool> ClearAsync(string modFolder, CancellationToken cancellationToken = default);
}
