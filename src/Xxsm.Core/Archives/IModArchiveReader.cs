namespace Xxsm.Core.Archives;

/// <summary>Opens a mod archive and works out what is inside it, by unpacking it and inspecting the files.</summary>
public interface IModArchiveReader
{
    /// <summary>The archive formats a mod can come in, with their leading dots.</summary>
    static IReadOnlyList<string> SupportedExtensions { get; } =
        [".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zipx"];

    /// <summary>Whether a path looks like an archive XXSM can open, from its extension alone.</summary>
    /// <param name="path">The path to test. Need not exist.</param>
    /// <returns><see langword="true"/> when the extension is one of the supported ones.</returns>
    bool IsSupportedArchive(string? path);

    /// <summary>Unpacks an archive and finds the mods in it.</summary>
    /// <param name="archivePath">The archive. Left untouched.</param>
    /// <param name="bounds">The limits to work within; <see cref="ArchiveBounds.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the extraction, leaving nothing behind.</param>
    /// <returns>The staging directory and what was found. Disposing it deletes the directory.</returns>
    /// <exception cref="ModOperationException">The file is missing, unreadable as an archive, holds an entry that would
    /// land outside the staging directory, or could not be unpacked; with the underlying error text.</exception>
    Task<ExtractedArchive> ExtractAsync(
        string archivePath,
        ArchiveBounds? bounds = null,
        CancellationToken cancellationToken = default);
}
