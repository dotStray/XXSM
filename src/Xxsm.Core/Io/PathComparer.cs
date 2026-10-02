using System.Diagnostics.CodeAnalysis;

namespace Xxsm.Core.Io;

/// <summary>Compares filesystem paths: ignoring case, keeping it, and reading <c>\</c> as <c>/</c>.</summary>
/// <remarks>Never compare paths with <c>==</c> or <see cref="string.Equals(string, string)"/>; use this.</remarks>
public sealed class PathComparer : IEqualityComparer<string>, IComparer<string>
{
    /// <summary>The comparison used for every path segment and whole-path comparison.</summary>
    public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Shared stateless instance. Safe to use as a dictionary or set comparer.</summary>
    public static PathComparer Instance { get; } = new();

    private PathComparer()
    {
    }

    /// <summary>Normalises a path without the disk: <c>/</c> separators, dot segments resolved, casing kept.</summary>
    /// <param name="path">The path to normalise. May be relative or absolute.</param>
    /// <returns>The normalised path, or <see cref="string.Empty"/> for a null or blank input.</returns>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var value = path.Replace('\\', '/');

        var isRooted = value.StartsWith('/');

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case ".":
                    continue;
                case ".." when stack.Count > 0 && stack[^1] != "..":
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                case ".." when isRooted:
                    continue;
                default:
                    stack.Add(segment);
                    break;
            }
        }

        var joined = string.Join('/', stack);

        if (isRooted)
        {
            return "/" + joined;
        }

        return joined.Length == 0 ? "." : joined;
    }

    /// <summary>Compares two paths by XXSM's rules. Null, empty and blank all equal one another.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when the two paths denote the same location.</returns>
    public static bool AreEqual(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), Comparison);

    /// <summary>Compares two single path segments (a file or directory name) by XXSM's rules.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when the names collide on a case-insensitive filesystem.</returns>
    public static bool AreNamesEqual(string? left, string? right) =>
        string.Equals(left, right, Comparison);

    /// <summary>Whether the candidate is the ancestor or beneath it, by whole segments.</summary>
    /// <param name="ancestor">The containing directory.</param>
    /// <param name="candidate">The path being tested.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> is at or below <paramref
    /// name="ancestor"/>.</returns>
    public static bool IsSameOrUnder(string? ancestor, string? candidate)
    {
        var a = Normalize(ancestor);
        var c = Normalize(candidate);

        if (a.Length == 0 || c.Length == 0)
        {
            return false;
        }

        if (string.Equals(a, c, Comparison))
        {
            return true;
        }

        var prefix = a.EndsWith('/') ? a : a + "/";
        return c.StartsWith(prefix, Comparison);
    }

    /// <summary>Joins a directory and a single child name into a path, separator-normalised.</summary>
    /// <param name="directory">The parent directory. Need not exist.</param>
    /// <param name="childName">The child's name. A single segment, not itself a path.</param>
    public static string Join(string directory, string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);

        var normalized = Normalize(directory);
        return normalized.EndsWith('/') ? normalized + childName : normalized + "/" + childName;
    }

    /// <summary>The candidate relative to the ancestor, or null when it is not beneath it.</summary>
    /// <param name="ancestor">The containing directory.</param>
    /// <param name="candidate">The path to relativise.</param>
    /// <returns>The relative path, casing kept, or null.</returns>
    public static string? TryGetRelativePath(string? ancestor, string? candidate)
    {
        if (!IsSameOrUnder(ancestor, candidate))
        {
            return null;
        }

        var a = Normalize(ancestor);
        var c = Normalize(candidate);

        if (string.Equals(a, c, Comparison))
        {
            return string.Empty;
        }

        var skip = a.EndsWith('/') ? a.Length : a.Length + 1;
        return c[skip..];
    }

    /// <summary>Finds a path's real on-disk casing; an exact match wins, then the first by ordinal.</summary>
    /// <param name="path">The path to resolve. Relative paths are resolved against the process directory.</param>
    /// <param name="resolved">The existing path with its true casing, when the whole path exists.</param>
    /// <returns><see langword="true"/> when every segment was found on disk.</returns>
    public static bool TryResolveExisting(string? path, [NotNullWhen(true)] out string? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var absolute = Path.GetFullPath(path);
        var full = Normalize(absolute);

        if (Path.Exists(full))
        {
            resolved = full;
            return true;
        }

        var root = Normalize(Path.GetPathRoot(absolute));
        if (root.Length == 0)
        {
            return false;
        }

        var relative = TryGetRelativePath(root, full);
        if (relative is null)
        {
            return false;
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var current = root.EndsWith('/') ? root : root + "/";

        foreach (var segment in segments)
        {
            var exact = Path.Combine(current, segment);
            if (Path.Exists(exact))
            {
                current = exact;
                continue;
            }

            string? match = null;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var name = Path.GetFileName(entry);
                    if (!AreNamesEqual(name, segment))
                    {
                        continue;
                    }

                    if (match is null || string.CompareOrdinal(name, match) < 0)
                    {
                        match = name;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder counts as missing; using the path later surfaces the real error.
                return false;
            }

            if (match is null)
            {
                return false;
            }

            current = Path.Combine(current, match);
        }

        resolved = Normalize(current);
        return true;
    }

    /// <summary>Whether creating the name in the parent would clash with a sibling by case only.</summary>
    /// <param name="parent">The directory the new entry would be created in.</param>
    /// <param name="name">The entry name to test. Must be a single path segment.</param>
    /// <param name="existingName">The colliding sibling's real name, when there is one.</param>
    /// <returns><see langword="true"/> only for a case-only clash; an exact match returns <see
    /// langword="false"/>.</returns>
    public static bool IsCaseCollision(string parent, string name, [NotNullWhen(true)] out string? existingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        existingName = null;

        if (!Directory.Exists(parent))
        {
            return false;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                var candidate = Path.GetFileName(entry);
                if (string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    return false;
                }

                if (AreNamesEqual(candidate, name))
                {
                    existingName = candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            existingName = null;
            return false;
        }

        return existingName is not null;
    }

    /// <summary>Whether two paths are one entry on disk; on ext4, names differing by case are not.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when equal once normalised, or differing only by case and leading to one
    /// existing entry.</returns>
    public static bool AreSameOnDisk(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) || !AreEqual(left, right))
        {
            return false;
        }

        var leftFull = Normalize(Path.GetFullPath(left));
        var rightFull = Normalize(Path.GetFullPath(right));

        if (string.Equals(leftFull, rightFull, StringComparison.Ordinal))
        {
            return true;
        }

        var leftSegments = leftFull.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rightSegments = rightFull.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var shared = 0;
        while (shared < leftSegments.Length &&
               string.Equals(leftSegments[shared], rightSegments[shared], StringComparison.Ordinal))
        {
            shared++;
        }

        var directory = (leftFull.StartsWith('/') ? "/" : string.Empty) + string.Join('/', leftSegments[..shared]);

        if (directory.Length == 0)
        {
            return true;
        }

        for (var i = shared; i < leftSegments.Length; i++)
        {
            var leftName = OnDiskName(directory, leftSegments[i]);
            var rightName = OnDiskName(directory, rightSegments[i]);

            if (leftName is null || rightName is null || !string.Equals(leftName, rightName, StringComparison.Ordinal))
            {
                return false;
            }

            directory = directory.EndsWith('/') ? directory + leftName : directory + "/" + leftName;
        }

        return true;
    }

    /// <summary>The entry a name leads to in a directory: exact, else by case; null if none.</summary>
    private static string? OnDiskName(string directory, string name)
    {
        if (!Path.Exists(directory.EndsWith('/') ? directory + name : directory + "/" + name))
        {
            return null;
        }

        string? folded = null;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var candidate = Path.GetFileName(entry);

                if (string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    return candidate;
                }

                if (AreNamesEqual(candidate, name) && (folded is null || string.CompareOrdinal(candidate, folded) < 0))
                {
                    folded = candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return folded;
    }

    /// <inheritdoc />
    public bool Equals(string? x, string? y) => AreEqual(x, y);

    /// <inheritdoc />
    public int GetHashCode(string obj) =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj));

    /// <inheritdoc />
    public int Compare(string? x, string? y) =>
        string.Compare(Normalize(x), Normalize(y), Comparison);
}
