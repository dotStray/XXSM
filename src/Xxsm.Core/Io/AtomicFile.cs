using System.Globalization;
using System.Text;

namespace Xxsm.Core.Io;

/// <summary>Saves a whole file so that a reader, a crash or a second writer never sees half of it.</summary>
/// <remarks>
/// Each save goes to its own <c>{path}.{guid}.tmp</c> and is renamed over the original; a durable save flushes to
/// disk first. On any failure, cancellation included, the temporary file goes and the original is untouched.
/// </remarks>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Saves <paramref name="contents"/> as UTF-8 text, without a byte-order mark.</summary>
    /// <param name="path">The file to save.</param>
    /// <param name="contents">The whole new text.</param>
    /// <param name="options">A backup, a disk flush, and the state the file must still be in.</param>
    /// <param name="cancellationToken">Cancels the save; the original is then untouched.</param>
    /// <returns>A task that completes once the new file is in place.</returns>
    /// <exception cref="FileChangedException"><see cref="AtomicWriteOptions.Expected"/> no longer matches the
    /// file.</exception>
    /// <exception cref="IOException">The file could not be written or moved into place.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or its folder may not be written.</exception>
    public static Task WriteAllTextAsync(
        string path,
        string contents,
        AtomicWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return WriteAllBytesAsync(path, Utf8.GetBytes(contents), options, cancellationToken);
    }

    /// <summary>Saves <paramref name="contents"/> as the file's whole content.</summary>
    /// <param name="path">The file to save.</param>
    /// <param name="contents">The whole new content.</param>
    /// <param name="options">A backup, a disk flush, and the state the file must still be in.</param>
    /// <param name="cancellationToken">Cancels the save; the original is then untouched.</param>
    /// <returns>A task that completes once the new file is in place.</returns>
    /// <exception cref="FileChangedException"><see cref="AtomicWriteOptions.Expected"/> no longer matches the
    /// file.</exception>
    /// <exception cref="IOException">The file could not be written or moved into place.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or its folder may not be written.</exception>
    public static Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        AtomicWriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            path,
            (stream, token) => stream.WriteAsync(contents, token).AsTask(),
            options,
            cancellationToken);

    /// <summary>Saves whatever <paramref name="write"/> puts in the stream it is given.</summary>
    /// <param name="path">The file to save.</param>
    /// <param name="write">Writes the whole new content. It must not close the stream.</param>
    /// <param name="options">A backup, a disk flush, and the state the file must still be in.</param>
    /// <param name="cancellationToken">Cancels the save; the original is then untouched.</param>
    /// <returns>A task that completes once the new file is in place.</returns>
    /// <exception cref="FileChangedException"><see cref="AtomicWriteOptions.Expected"/> no longer matches the
    /// file.</exception>
    /// <exception cref="IOException">The file could not be written or moved into place.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or its folder may not be written.</exception>
    public static async Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        AtomicWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        options ??= AtomicWriteOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();

        var temporary = TemporaryNameFor(path);
        string? backupTemporary = null;

        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             useAsync: true))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (options.Durable)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (options.BackupPath is { } backup && File.Exists(path))
            {
                backupTemporary = TemporaryNameFor(backup);
                await CopyAsync(path, backupTemporary, options.Durable, cancellationToken).ConfigureAwait(false);
            }

            // The last moment another writer's save can be noticed, such as a text editor's.
            if (options.Expected is { } expected)
            {
                var now = FileStamp.Of(path);

                if (now != expected)
                {
                    throw new FileChangedException(path);
                }
            }

            if (backupTemporary is not null)
            {
                File.Move(backupTemporary, options.BackupPath!, overwrite: true);
                backupTemporary = null;
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            OwnScratch.TryDeleteFile(temporary, null);

            if (backupTemporary is not null)
            {
                OwnScratch.TryDeleteFile(backupTemporary, null);
            }

            throw;
        }
    }

    /// <summary>Saves a new file under the first free name of stem, stem-2, stem-3…; never replaces one.</summary>
    /// <param name="directory">The folder; created when missing.</param>
    /// <param name="stem">The name without its extension.</param>
    /// <param name="extension">The extension, with its dot.</param>
    /// <param name="contents">The whole content.</param>
    /// <param name="cancellationToken">Cancels the save; nothing is then left behind.</param>
    /// <returns>The path the file was saved at.</returns>
    /// <exception cref="IOException">There is no free name, or the file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder may not be written.</exception>
    public static async Task<string> WriteNewAsync(
        string directory,
        string stem,
        string extension,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        ArgumentNullException.ThrowIfNull(extension);

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);

        var path = FreeName(directory, stem, extension);
        var temporary = TemporaryNameFor(path);

        try
        {
            await File.WriteAllBytesAsync(temporary, contents, cancellationToken).ConfigureAwait(false);

            // Not overwriting: a file that took the name since it was found free is kept, and this save fails.
            File.Move(temporary, path, overwrite: false);
            return path;
        }
        catch
        {
            OwnScratch.TryDeleteFile(temporary, null);
            throw;
        }
    }

    private static string FreeName(string directory, string stem, string extension)
    {
        for (var attempt = 1; attempt < 10_000; attempt++)
        {
            var candidate = Path.Combine(
                directory,
                (attempt == 1 ? stem : stem + "-" + attempt.ToString(CultureInfo.InvariantCulture)) + extension);

            // Ignoring case: on a case-sensitive disk "Amy.png" is still taken by "amy.png".
            if (!PathComparer.TryResolveExisting(candidate, out _))
            {
                return candidate;
            }
        }

        throw new IOException($"There are too many files called '{stem}' in '{PathDisplay.Show(directory)}'.");
    }

    private static string TemporaryNameFor(string path) => $"{path}.{Guid.NewGuid():n}.tmp";

    private static async Task CopyAsync(string source, string destination, bool durable, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true);

        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (durable)
        {
            output.Flush(flushToDisk: true);
        }
    }

}

/// <summary>How <see cref="AtomicFile"/> saves a file.</summary>
public sealed record AtomicWriteOptions
{
    /// <summary>No backup, no disk flush, no check.</summary>
    public static AtomicWriteOptions Default { get; } = new();

    /// <summary>A disk flush and nothing else.</summary>
    public static AtomicWriteOptions DurableOnly { get; } = new() { Durable = true };

    /// <summary>Where to copy the previous version before replacing it; null for no copy.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Flush the new file (and the backup) to the disk before renaming it into place.</summary>
    public bool Durable { get; init; }

    /// <summary>The file's state when read; a save throws <see cref="FileChangedException"/> if it changed.</summary>
    public FileStamp? Expected { get; init; }
}

/// <summary>What a file looked like at one moment: enough to tell that someone saved it since.</summary>
/// <param name="Exists">Whether there was a file at all.</param>
/// <param name="Length">Its length in bytes, 0 when missing.</param>
/// <param name="LastWriteUtc">When it was last written, <see cref="DateTime.MinValue"/> when missing.</param>
public readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteUtc)
{
    /// <summary>The state of the file at <paramref name="path"/> now.</summary>
    /// <param name="path">A file that may or may not exist.</param>
    /// <returns>Its stamp; a missing file has one too.</returns>
    public static FileStamp Of(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        return info.Exists
            ? new FileStamp(true, info.Length, info.LastWriteTimeUtc)
            : new FileStamp(false, 0, DateTime.MinValue);
    }
}

/// <summary>Thrown when a file was saved by someone else between read and write. Read it again.</summary>
public sealed class FileChangedException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="path">The file that changed.</param>
    public FileChangedException(string path)
        : base($"'{PathDisplay.Show(path)}' was saved by another program while XXSM was changing it.") =>
        Path = path;

    /// <summary>The file that changed.</summary>
    public string Path { get; }
}
