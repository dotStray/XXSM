namespace Xxsm.Core.Io;

/// <summary>An <see cref="IVolumeResolver"/> over <see cref="DriveInfo.GetDrives"/>, the mount table on Unix.</summary>
public sealed class DriveInfoVolumeResolver : IVolumeResolver
{
    /// <inheritdoc />
    public string GetVolumeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Through every link first: a Mods folder linked to another drive is on that drive.
        var full = PathComparer.Normalize(ResolveLinks(Path.GetFullPath(path)));
        var fallback = PathComparer.Normalize(Path.GetPathRoot(full)) is { Length: > 0 } root
            ? root
            : "/";

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable mount table only loses the volume-trash shortcut.
            return fallback;
        }

        var best = fallback;
        foreach (var drive in drives)
        {
            string mount;
            try
            {
                mount = PathComparer.Normalize(drive.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Exact comparison on purpose: /mnt/Games and /mnt/games are two places.
            if (mount.Length == 0 || !UntrustedLocation.IsSameOrUnderExactly(mount, full))
            {
                continue;
            }

            if (mount.Length > best.Length)
            {
                best = mount;
            }
        }

        return best;
    }

    /// <summary>The path with every existing link along it replaced by its target; the rest is kept as given.</summary>
    /// <param name="full">A full path.</param>
    /// <returns>The real path.</returns>
    internal static string ResolveLinks(string full)
    {
        var root = Path.GetPathRoot(full);

        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        // A loop of links stops at the kernel's own limit.
        var hops = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);

            FileSystemInfo? target;

            try
            {
                var info = new FileInfo(next);

                if (!info.Exists && !Directory.Exists(next))
                {
                    return Path.Combine([current, .. parts[i..]]);
                }

                target = info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Path.Combine([current, .. parts[i..]]);
            }

            if (target is not null && ++hops <= 40)
            {
                next = ResolveLinks(Path.GetFullPath(target.FullName));
            }

            current = next;
        }

        return current;
    }
}
