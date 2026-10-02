using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;

namespace Xxsm.Core.Archives;

/// <summary>Unpacks an archive into a folder of XXSM's own, within limits, for mods and packs alike.</summary>
/// <remarks>
/// Each entry is read once, in archive order, and what is written is counted, not what the archive declares. An
/// entry that would land outside the folder refuses the whole archive. Links, a second entry of the same name or
/// case, and macOS metadata (<c>__MACOSX/</c>, <c>._*</c>, <c>.DS_Store</c>) are skipped; a leading <c>/</c> is
/// dropped.
/// </remarks>
public static class ArchiveUnpacker
{
    /// <summary>Unpacks <paramref name="archivePath"/> into <paramref name="destination"/>.</summary>
    /// <param name="archivePath">The archive.</param>
    /// <param name="destination">An empty folder of XXSM's own.</param>
    /// <param name="bounds">The limits.</param>
    /// <param name="diagnostics">Collects what was skipped, and why.</param>
    /// <param name="cancellationToken">Stops between entries and between reads.</param>
    /// <exception cref="ModOperationException">It is not an archive XXSM can read, needs a password, is damaged, or
    /// holds an entry that would land outside <paramref name="destination"/>. Nothing in it is to be trusted
    /// then.</exception>
    public static (int Entries, long Bytes, ArchiveLimit Reached) Unpack(
        string archivePath,
        string destination,
        ArchiveBounds bounds,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(diagnostics);

        try
        {
            using var source = Open(archivePath);

            return UnpackEntries(source.Reader, archivePath, destination, bounds, diagnostics, cancellationToken);
        }
        catch (CryptographicException exception)
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(archivePath)}' is password-protected, and XXSM cannot unpack it " +
                $"({exception.Message}). Unpack it yourself with the password and install " +
                "the folder instead. Nothing was installed.",
                archivePath,
                exception);
        }
        catch (Exception exception) when (exception is SharpCompressException or InvalidDataException)
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(archivePath)}' could not be read as an archive: {exception.Message}. " +
                "It may be damaged or incomplete; download it again, or unpack it yourself " +
                "and install the folder instead. Nothing was installed.",
                archivePath,
                exception);
        }
    }

    /// <summary>Opens an archive to be read once, front to back.</summary>
    private static Opened Open(string archivePath)
    {
        FileStream? stream = null;

        try
        {
            stream = File.OpenRead(archivePath);

            try
            {
                return new Opened(ReaderFactory.Open(stream, new ReaderOptions()), stream, null);
            }
            catch (Exception exception) when (exception is InvalidOperationException or SharpCompressException)
            {
                stream.Dispose();
                stream = null;

                var archive = ArchiveFactory.Open(archivePath, new ReaderOptions());

                try
                {
                    return new Opened(archive.ExtractAllEntries(), null, archive);
                }
                catch
                {
                    archive.Dispose();
                    throw;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                             or IOException
                                             or UnauthorizedAccessException
                                             or NotSupportedException
                                             or SharpCompressException)
        {
            stream?.Dispose();

            throw new ModOperationException(
                $"'{PathDisplay.Show(archivePath)}' could not be opened as an archive: {exception.Message}. " +
                "If it needs a password, or is a format XXSM does not read, unpack it yourself " +
                "and install the folder instead.",
                archivePath,
                exception);
        }
    }

    private static (int Entries, long Bytes, ArchiveLimit Reached) UnpackEntries(
        IReader reader,
        string archivePath,
        string staging,
        ArchiveBounds bounds,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var entries = 0;
        var bytes = 0L;
        var reached = ArchiveLimit.None;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging));

        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = reader.Entry;

            if (entry.IsDirectory || NameOf(entry.Key) is not { } key)
            {
                continue;
            }

            if (key.Contains('\0', StringComparison.Ordinal))
            {
                diagnostics.Add(Skipped($"An entry with an impossible name ('{key.Replace('\0', '?')}') was skipped."));
                continue;
            }

            if (IsMacJunk(key))
            {
                continue;
            }

            if (entry.LinkTarget is { Length: > 0 } || (reader.ArchiveType == ArchiveType.Zip && IsZipLink(entry)))
            {
                diagnostics.Add(Skipped($"'{key}' is a link and was not made: an archive never makes links."));
                continue;
            }

            if (entries >= bounds.MaxEntries)
            {
                reached |= ArchiveLimit.Entries;
                diagnostics.Add(Limit($"it holds more than {bounds.MaxEntries} files"));
                break;
            }

            if (entry.Size > bounds.MaxEntryBytes)
            {
                reached |= ArchiveLimit.EntryBytes;
                diagnostics.Add(Limit($"'{key}' is larger than {bounds.MaxEntryBytes} bytes"));
                continue;
            }

            if (bytes + entry.Size > bounds.MaxTotalBytes)
            {
                reached |= ArchiveLimit.TotalBytes;
                diagnostics.Add(Limit($"it unpacks to more than {bounds.MaxTotalBytes} bytes"));
                break;
            }

            var destination = Path.GetFullPath(Path.Combine(root, key));

            if (!UntrustedLocation.IsSameOrUnderExactly(root, destination)
                || string.Equals(destination, root, StringComparison.Ordinal))
            {
                throw new ModOperationException(
                    $"'{PathDisplay.Show(archivePath)}' contains an entry that would be written outside the " +
                    $"folder it is unpacked into ('{entry.Key}'). Nothing was installed.",
                    archivePath);
            }

            if (File.Exists(destination))
            {
                diagnostics.Add(Skipped($"'{key}' appears twice in the archive; the first was kept."));
                continue;
            }

            if (PathComparer.TryResolveExisting(destination, out var sameButForCase)
                && !string.Equals(sameButForCase, destination, StringComparison.Ordinal))
            {
                diagnostics.Add(Skipped(
                    $"'{key}' differs from '{Path.GetRelativePath(root, sameButForCase)}' only by capitals, which the game " +
                    "under Wine sees as one file; the first was kept."));
                continue;
            }

            var remaining = bounds.MaxTotalBytes - bytes;
            var cap = Math.Min(bounds.MaxEntryBytes, remaining);
            long written;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                written = WriteCapped(reader, destination, cap, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException
                                                 or UnauthorizedAccessException
                                                 or InvalidOperationException
                                                 or InvalidDataException
                                                 or (SharpCompressException and not CryptographicException))
            {
                // Every entry needs the password, so it refuses the whole archive, not file by file.
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    ArchiveDiagnosticCodes.UnreadableEntry,
                    $"'{key}' could not be unpacked and was skipped: {exception.Message}"));

                continue;
            }

            if (written < 0)
            {
                if (bounds.MaxEntryBytes <= remaining)
                {
                    reached |= ArchiveLimit.EntryBytes;
                    diagnostics.Add(Limit($"'{key}' unpacks to more than {bounds.MaxEntryBytes} bytes"));
                    continue;
                }

                reached |= ArchiveLimit.TotalBytes;
                diagnostics.Add(Limit($"it unpacks to more than {bounds.MaxTotalBytes} bytes"));
                break;
            }

            entries++;
            bytes += written;
        }

        return (entries, bytes, reached);
    }

    /// <summary>An entry's name as a relative path: separators made plain, a leading one taken off.</summary>
    private static string? NameOf(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var name = key.Replace('\\', '/').TrimStart('/');

        return name.Length == 0 || name.EndsWith('/') ? null : name;
    }

    /// <summary>Whether a zip entry is a Unix link, by the Unix mode in its attributes' high half.</summary>
    private static bool IsZipLink(IEntry entry) =>
        entry.Attrib is { } attributes && ((attributes >> 16) & 0xF000) == 0xA000;

    private static bool IsMacJunk(string key)
    {
        var segments = key.Split('/');

        return segments.Any(segment => segment == "__MACOSX")
               || segments[^1] == ".DS_Store"
               || segments[^1].StartsWith("._", StringComparison.Ordinal);
    }

    /// <summary>Writes the current entry, stopping past <paramref name="cap"/> bytes.</summary>
    /// <returns>The bytes written, or -1 when the cap was passed; the partial file is removed then.</returns>
    private static long WriteCapped(IReader reader, string destination, long cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var written = 0L;

        using (var input = reader.OpenEntryStream())
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                written += read;
                if (written > cap)
                {
                    break;
                }

                output.Write(buffer, 0, read);
            }

            if (written > cap)
            {
                // Read to its end, or the reader cannot move to the next entry.
                input.SkipEntry();
            }
        }

        if (written <= cap)
        {
            return written;
        }

        // Staging is XXSM's own folder, made for this unpack: a partial file there is not user content.
        File.Delete(destination);
        return -1;
    }

    private static Diagnostic Limit(string why) => new(
        DiagnosticSeverity.Warning,
        ArchiveDiagnosticCodes.LimitReached,
        $"The archive was only partly unpacked because {why}. Install what was found, or " +
        "unpack it yourself and install the folder.");

    private static Diagnostic Skipped(string message) => new(
        DiagnosticSeverity.Warning,
        ArchiveDiagnosticCodes.SkippedEntry,
        message);

    private sealed class Opened(IReader reader, Stream? stream, IArchive? archive) : IDisposable
    {
        public IReader Reader { get; } = reader;

        public void Dispose()
        {
            Reader.Dispose();
            stream?.Dispose();
            archive?.Dispose();
        }
    }
}
