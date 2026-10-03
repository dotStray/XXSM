using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Core.Ini;

/// <summary>Finds the INIs 3DMigoto loads in a mod folder, resolves them safely, and keeps their originals.</summary>
internal static class ModInis
{
    private const int MaximumDepth = 8;
    private const int MaximumFiles = 500;

    /// <summary>The mod folder, resolved and normalised.</summary>
    /// <exception cref="ModOperationException">The folder does not exist.</exception>
    public static string Root(string modFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        if (!PathComparer.TryResolveExisting(modFolder, out var resolved) || !Directory.Exists(resolved))
        {
            throw new ModOperationException($"The mod folder '{PathDisplay.Show(modFolder)}' does not exist.", modFolder);
        }

        return PathComparer.Normalize(resolved);
    }

    /// <summary>An INI's path relative to the mod folder, with <c>/</c> separators.</summary>
    public static string Relative(string root, string file) =>
        (PathComparer.TryGetRelativePath(root, file) ?? Path.GetFileName(file)).Replace('\\', '/');

    /// <summary>Where the first original of an INI is kept: <c>.xxsm/originals/name.ini.original</c>, which 3DMigoto does not load.</summary>
    public static string OriginalOf(string root, string path) =>
        Path.Combine(root, ModConfigSchema.DirectoryName, "originals", Path.GetRelativePath(root, path) + ".original");

    /// <summary>Resolves an INI named relative to the mod folder, refusing anything outside it or not an INI.</summary>
    /// <exception cref="ModOperationException">The name is not an INI file in this mod.</exception>
    public static string Resolve(string root, string relative)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, relative));

        if (!UntrustedLocation.IsSameOrUnderExactly(root, candidate) ||
            !string.Equals(Path.GetExtension(candidate), ".ini", StringComparison.OrdinalIgnoreCase) ||
            !PathComparer.TryResolveExisting(candidate, out var resolved) ||
            !File.Exists(resolved))
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(relative)}' is not an INI file in this mod. Nothing was changed.", candidate);
        }

        return resolved;
    }

    /// <summary>Keeps an INI's first original, once; later calls leave the kept copy alone.</summary>
    /// <exception cref="ModOperationException">The copy could not be made, so the INI must not be changed.</exception>
    public static void KeepOriginal(string root, string path, ILogger logger)
    {
        var original = OriginalOf(root, path);

        try
        {
            if (File.Exists(original))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(path, original, overwrite: false);
            logger.Information("Kept the original of {Path} at {Original} before its first edit", path, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not keep a copy of '{PathDisplay.Show(Path.GetRelativePath(root, path))}' before changing it ({ex.Message}), so it was not changed.",
                path,
                ex);
        }
    }

    /// <summary>Every INI 3DMigoto would load from the mod, in ordinal order; what could not be listed goes to <paramref name="problems"/>.</summary>
    public static List<string> Find(string root, List<string> problems)
    {
        var found = new List<string>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Relative(root, directory)}: {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);

                // 3DMigoto skips anything named DISABLED…; XXSM's own folders are dot-folders.
                if (name.StartsWith('.') || ModsFolderLayout.IsDisabled(name))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (depth + 1 <= MaximumDepth && new DirectoryInfo(entry).LinkTarget is null)
                    {
                        pending.Push((entry, depth + 1));
                    }

                    continue;
                }

                if (string.Equals(Path.GetExtension(entry), ".ini", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(entry);

                    if (found.Count >= MaximumFiles)
                    {
                        problems.Add($"This mod has more than {MaximumFiles} INI files; only the first {MaximumFiles} were read.");
                        return [.. found.Order(StringComparer.Ordinal)];
                    }
                }
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }
}
