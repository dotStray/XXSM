namespace Xxsm.Core.Io;

/// <summary>Marks the folders XXSM made for itself, so nothing that empties one can empty someone else's.</summary>
/// <remarks>A folder is XXSM's when named <c>xxsm</c> or created by XXSM with <see cref="MarkerName"/>; never marked
/// later.</remarks>
public static class OwnFolder
{
    /// <summary>The file that marks a folder XXSM made for itself.</summary>
    public const string MarkerName = ".xxsm-root";

    /// <summary>Creates <paramref name="path"/> if it is missing, marking it as XXSM's when it does.</summary>
    /// <param name="path">One of XXSM's own folders.</param>
    /// <exception cref="IOException">The folder or the marker could not be made.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder may not be made.</exception>
    public static void Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Directory.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, MarkerName),
            "XXSM made this folder for itself. Reset XXSM empties only folders marked like this, or named xxsm." +
            Environment.NewLine);
    }

    /// <summary>Whether <paramref name="path"/> is one of XXSM's own folders.</summary>
    /// <param name="path">The folder.</param>
    /// <returns><see langword="true"/> when it is named <c>xxsm</c> or carries the marker.</returns>
    public static bool IsOwn(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), AppInfo.Slug, StringComparison.Ordinal)
               || File.Exists(Path.Combine(path, MarkerName));
    }
}
