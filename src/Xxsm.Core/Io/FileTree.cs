namespace Xxsm.Core.Io;

/// <summary>Walks a folder and everything in it without following a link, to a folder or to a file.</summary>
public static class FileTree
{
    /// <summary>A recursive walk that skips links and nothing else.</summary>
    public static EnumerationOptions WithoutLinks => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Every file under <paramref name="root"/>, not through a link and not a link itself.</summary>
    /// <param name="root">The folder.</param>
    /// <returns>Full paths.</returns>
    public static IEnumerable<string> Files(string root) => Directory.EnumerateFiles(root, "*", WithoutLinks);

    /// <summary>Every folder under <paramref name="root"/>, not through a link and not a link itself.</summary>
    /// <param name="root">The folder.</param>
    /// <returns>Full paths.</returns>
    public static IEnumerable<string> Directories(string root) => Directory.EnumerateDirectories(root, "*", WithoutLinks);
}
