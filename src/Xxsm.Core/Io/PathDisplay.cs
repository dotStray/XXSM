namespace Xxsm.Core.Io;

/// <summary>Turns a stored path into the form the user's system writes: <c>\</c> on Windows, <c>/</c> elsewhere.</summary>
/// <remarks>Display only. Paths are stored, compared and passed around in <see cref="PathComparer.Normalize"/>'s
/// form.</remarks>
public static class PathDisplay
{
    /// <summary>The path with this system's separator, for showing to the user; empty for null.</summary>
    public static string Show(string? path) => Show(path, Path.DirectorySeparatorChar);

    /// <summary>The path with <paramref name="separator"/> for every <c>/</c>; empty for null.</summary>
    /// <param name="path">A stored path; nothing else about it changes, so a <c>\</c> in a Linux name stays.</param>
    /// <param name="separator">The separator to show.</param>
    public static string Show(string? path, char separator)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return separator == '/' ? path : path.Replace('/', separator);
    }
}
