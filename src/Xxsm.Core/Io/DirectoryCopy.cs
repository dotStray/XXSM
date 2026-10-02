using System.Globalization;

namespace Xxsm.Core.Io;

/// <summary>Copies a folder whole and checks the copy, for moves across disks.</summary>
internal static class DirectoryCopy
{
    /// <summary>Copies every folder and file under <paramref name="source"/> to <paramref name="target"/>.</summary>
    /// <param name="source">The folder to copy.</param>
    /// <param name="target">Where the copy goes; created.</param>
    /// <param name="cancellationToken">Stops between files.</param>
    /// <exception cref="IOException">A folder or file could not be read or written; the partial copy is the caller's to
    /// remove.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for a permission.</exception>
    public static async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in FileTree.Directories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in FileTree.Files(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var reading = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

            await using var writing = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

            await reading.CopyToAsync(writing, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Checks every source file arrived, at the same relative path and length, with nothing extra.</summary>
    /// <param name="source">The original.</param>
    /// <param name="target">The copy.</param>
    /// <exception cref="ModOperationException">A file is missing, a different length, or extra.</exception>
    public static void Verify(string source, string target)
    {
        var expected = Measure(source, source);
        var actual = Measure(target, source);

        foreach (var (relative, length) in expected)
        {
            if (!actual.TryGetValue(relative, out var copiedLength))
            {
                throw new ModOperationException(
                    $"The copy of '{PathDisplay.Show(source)}' is incomplete: '{PathDisplay.Show(relative)}' is missing from " +
                    $"'{PathDisplay.Show(target)}'. The original has not been touched.",
                    target);
            }

            if (copiedLength != length)
            {
                throw new ModOperationException(
                    $"The copy of '{PathDisplay.Show(source)}' is damaged: '{PathDisplay.Show(relative)}' is " +
                    $"{copiedLength.ToString(CultureInfo.InvariantCulture)} bytes " +
                    $"but should be {length.ToString(CultureInfo.InvariantCulture)}. " +
                    "The original has not been touched.",
                    target);
            }
        }

        if (actual.Count != expected.Count)
        {
            throw new ModOperationException(
                $"The copy of '{PathDisplay.Show(source)}' does not match it: it holds " +
                $"{actual.Count.ToString(CultureInfo.InvariantCulture)} files where the " +
                $"original holds {expected.Count.ToString(CultureInfo.InvariantCulture)}. " +
                "The original has not been touched.",
                target);
        }

        static Dictionary<string, long> Measure(string root, string source)
        {
            // Ordinal on purpose: a.ini and A.ini are two files, and both must have arrived.
            var measured = new Dictionary<string, long>(StringComparer.Ordinal);

            try
            {
                foreach (var file in FileTree.Files(root))
                {
                    measured[Path.GetRelativePath(root, file)] = new FileInfo(file).Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ModOperationException(
                    $"Could not check the copy of '{PathDisplay.Show(source)}': {ex.Message}. The original has not been touched.",
                    root,
                    ex);
            }

            return measured;
        }
    }
}
