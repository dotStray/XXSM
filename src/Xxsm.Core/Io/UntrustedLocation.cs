namespace Xxsm.Core.Io;

/// <summary>Where a file someone else wrote may point: inside its own folder, or to https.</summary>
/// <remarks>Containment is exact, not case-blind as <see cref="PathComparer.IsSameOrUnder"/> is.</remarks>
public static class UntrustedLocation
{
    /// <summary>Resolves a relative path inside a root, refusing anything that would leave it.</summary>
    /// <param name="root">The pack's or the mod's folder.</param>
    /// <param name="relative">The path as the file gave it.</param>
    /// <param name="resolved">The full path, when inside.</param>
    /// <returns><see langword="false"/> for an absolute path, any address, a NUL, or a <c>..</c> that climbs
    /// out.</returns>
    public static bool TryResolveInside(string root, string? relative, out string resolved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(relative)
            || relative.Contains('\0', StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || relative.StartsWith('\\')
            || HasScheme(relative))
        {
            return false;
        }

        var normalisedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(normalisedRoot, relative.Replace('\\', '/')));

        if (!IsSameOrUnderExactly(normalisedRoot, full) || PathsEqualExactly(full, normalisedRoot))
        {
            return false;
        }

        resolved = full;
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it, letter for letter.</summary>
    /// <param name="root">The folder.</param>
    /// <param name="path">The candidate; both are made full first.</param>
    /// <returns><see langword="true"/> when inside. <c>/x/Mods2</c> is not inside <c>/x/Mods</c>.</returns>
    public static bool IsSameOrUnderExactly(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        return PathsEqualExactly(p, r)
               || (p.Length > r.Length
                   && p.StartsWith(r, StringComparison.Ordinal)
                   && (p[r.Length] == Path.DirectorySeparatorChar || p[r.Length] == Path.AltDirectorySeparatorChar
                       || r.EndsWith(Path.DirectorySeparatorChar)));
    }

    /// <summary>Whether <paramref name="value"/> is an address a picture may be fetched from.</summary>
    /// <param name="value">The text as the file gave it.</param>
    /// <param name="address">The address, when allowed.</param>
    /// <returns><see langword="true"/> only for an absolute <c>https</c> address with a host.</returns>
    public static bool IsWebAddress(string? value, out Uri address)
    {
        address = null!;

        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        address = uri;
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is a non-empty regular file: not a link, device or pipe.</summary>
    /// <param name="path">A full path.</param>
    /// <returns><see langword="true"/> for a regular, non-empty file that is not a link.</returns>
    public static bool IsRegularFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var info = new FileInfo(path);

            return info.Exists
                   && info.LinkTarget is null
                   && (info.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0
                   && info.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':', StringComparison.Ordinal);

        return colon > 1 && value[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    private static bool PathsEqualExactly(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.Ordinal);
}
