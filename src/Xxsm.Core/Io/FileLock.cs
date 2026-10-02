namespace Xxsm.Core.Io;

/// <summary>Takes turns with other programs, the app and the CLI, over a file both save.</summary>
/// <remarks>An exclusively held lock file, left in place afterwards. Keep it out of folders people edit.</remarks>
public static class FileLock
{
    /// <summary>How long to wait for another program to finish before giving up in words.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Waits until no other program holds <paramref name="lockPath"/>, then holds it.</summary>
    /// <param name="lockPath">The lock file; created with its folder when missing.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Disposing it lets the next program in.</returns>
    /// <exception cref="IOException">Another program held it for over ten seconds, or the lock file cannot be
    /// made.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file's folder may not be written.</exception>
    public static async Task<IDisposable> AcquireAsync(string lockPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);

        if (Path.GetDirectoryName(lockPath) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex) when (IsHeldElsewhere(ex) && waited.Elapsed < Wait)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (IsHeldElsewhere(ex))
            {
                throw new IOException(
                    $"another program kept '{PathDisplay.Show(lockPath)}' for more than {Wait.TotalSeconds:0} seconds while saving " +
                    "the same file",
                    ex);
            }
        }
    }

    // EWOULDBLOCK (11) on Linux; ERROR_SHARING_VIOLATION (32) on Windows.
    private static bool IsHeldElsewhere(IOException ex) => ex.HResult == 11 || (ex.HResult & 0xFFFF) == 32;
}
