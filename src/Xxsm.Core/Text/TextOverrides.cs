namespace Xxsm.Core.Text;

/// <summary>The contents of the user's interface-text file, as read from disk.</summary>
/// <param name="Path">Where the file is, whether or not it exists.</param>
/// <param name="Exists">Whether there is a file there at all.</param>
/// <param name="Values">Key to replacement text. Empty when the file is absent or unreadable.</param>
/// <param name="Problem">Why the file could not be used, in the parser's words, or null when it was read.</param>
public sealed record TextOverrides(
    string Path,
    bool Exists,
    IReadOnlyDictionary<string, string> Values,
    string? Problem)
{
    /// <summary>An empty result for a path with no file at it.</summary>
    /// <param name="path">Where the file would have been.</param>
    /// <returns>Overrides that change nothing.</returns>
    public static TextOverrides None(string path) =>
        new(path, Exists: false, new Dictionary<string, string>(StringComparer.Ordinal), Problem: null);
}
