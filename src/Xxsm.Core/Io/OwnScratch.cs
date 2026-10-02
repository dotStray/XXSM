using Serilog;

namespace Xxsm.Core.Io;

/// <summary>Removes XXSM's own scratch: a half-written file or a staging folder it made. Never user content.</summary>
public static class OwnScratch
{
    /// <summary>Deletes a file XXSM made itself; one already gone is fine, and a failure is logged, never thrown.</summary>
    /// <param name="path">The file. A folder at this path is left alone.</param>
    /// <param name="logger">Where a failure is logged; null says nothing.</param>
    public static void TryDeleteFile(string path, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            // File.Delete on purpose: only ever XXSM's own scratch, which the trash has no use for.
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warning(ex, "Could not remove XXSM's own temporary file {Path}", path);
        }
    }

    /// <summary>Deletes a folder XXSM made itself, with everything in it; a failure is logged, never thrown.</summary>
    /// <param name="path">The folder. A file at this path is left alone.</param>
    /// <param name="logger">Where a failure is logged; null says nothing.</param>
    public static void TryDeleteFolder(string path, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (Directory.Exists(path))
            {
                // Directory.Delete on purpose: only ever XXSM's own staging, which the trash has no use for.
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warning(ex, "Could not remove XXSM's own temporary folder {Path}", path);
        }
    }
}
