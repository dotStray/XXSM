using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The naming rules of a 3DMigoto Mods folder, the <c>DISABLED_</c> prefix above all.</summary>
public static class ModsFolderLayout
{
    /// <summary>The prefix XXSM writes to disable a mod.</summary>
    public const string DisabledPrefix = "DISABLED_";

    /// <summary>The prefix older tools wrote. Accepted on read, never written.</summary>
    public const string LegacyDisabledPrefix = "DISABLED";

    /// <summary>XXSM's own state directory at the root of a Mods folder.</summary>
    public const string StateDirectoryName = ".xxsm";

    /// <summary>The folder unsortable mods are filed under; an ordinary folder.</summary>
    public const string UnsortedFolderName = "Others";

    /// <summary>Whether a folder name marks a disabled mod, ignoring case.</summary>
    /// <param name="folderName">The folder's own name, not a path.</param>
    /// <returns><c>true</c> when the name carries either disabled prefix.</returns>
    public static bool IsDisabled(string? folderName) => DisabledPrefixLength(folderName) > 0;

    /// <summary>Removes the disabled prefix from a folder name.</summary>
    /// <param name="folderName">The folder's own name, not a path.</param>
    /// <returns>The name with any disabled prefix removed. Unchanged when there is none.</returns>
    public static string StripDisabledPrefix(string folderName)
    {
        ArgumentNullException.ThrowIfNull(folderName);

        return folderName[DisabledPrefixLength(folderName)..];
    }

    /// <summary>How many leading characters of a folder name are a disabled prefix; 0 for none.</summary>
    /// <param name="folderName">The folder's own name, not a path.</param>
    /// <returns>The prefix length, or 0 when the name is not disabled.</returns>
    /// <remarks>
    /// <c>DISABLED_</c> always counts. The bare legacy prefix counts only before an uppercase letter or a digit, so
    /// <c>Disabledragon</c> is a name; and a folder named exactly <c>DISABLED_</c> is a name too.
    /// </remarks>
    public static int DisabledPrefixLength(string? folderName)
    {
        if (folderName is null)
        {
            return 0;
        }

        if (folderName.Length > DisabledPrefix.Length &&
            folderName.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return DisabledPrefix.Length;
        }

        if (folderName.Length > LegacyDisabledPrefix.Length &&
            folderName.StartsWith(LegacyDisabledPrefix, StringComparison.OrdinalIgnoreCase) &&
            !char.IsLower(folderName[LegacyDisabledPrefix.Length]) &&
            folderName[LegacyDisabledPrefix.Length] != '_')
        {
            return LegacyDisabledPrefix.Length;
        }

        return 0;
    }

    /// <summary>Adds the disabled prefix to a folder name, once.</summary>
    /// <param name="folderName">The folder's own name, not a path.</param>
    /// <returns>The name with <see cref="DisabledPrefix"/>, or unchanged when already disabled.</returns>
    public static string AddDisabledPrefix(string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        return IsDisabled(folderName) ? folderName : DisabledPrefix + folderName;
    }

    /// <summary>Whether an entry in a Mods folder is bookkeeping to skip: anything starting with a dot.</summary>
    /// <param name="name">The entry's own name, not a path.</param>
    /// <returns><c>true</c> when the scan must skip it.</returns>
    public static bool IsReservedEntry(string? name) =>
        string.IsNullOrEmpty(name) || name.StartsWith('.');

    /// <summary>Where things taken out of Mods go when no trash will take them: the folder holding Mods.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <returns>Its parent, or null for a Mods folder at the top of a disk.</returns>
    /// <remarks>Never inside Mods: 3DMigoto loads every INI there outside a <c>DISABLED</c> folder.</remarks>
    public static string? TrashFallbackFor(string modsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDirectory)));
    }

    /// <summary>Why a name cannot be used for a mod folder, or null when it can. Stricter than the disk.</summary>
    /// <param name="folderName">The name a user typed. Not a path.</param>
    /// <returns>A sentence naming the problem, ready to show verbatim, or null when the name is usable.</returns>
    public static string? DescribeUnusableFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "A mod folder needs a name.";
        }

        if (folderName.Contains('/', StringComparison.Ordinal) ||
            folderName.Contains('\\', StringComparison.Ordinal))
        {
            return "A mod folder name cannot contain a slash — it is one folder, not a path.";
        }

        if (folderName is "." or "..")
        {
            return $"'{folderName}' means a folder position, not a folder name.";
        }

        if (folderName.StartsWith('.'))
        {
            return "A name starting with a dot is hidden, and XXSM would stop showing the mod.";
        }

        foreach (var character in folderName)
        {
            if (char.IsControl(character))
            {
                return "A mod folder name cannot contain control characters.";
            }

            // Refused whatever this machine allows: the folder is read under Wine and copied to NTFS.
            if (WindowsReservedCharacters.Contains(character))
            {
                return $"A mod folder name cannot contain '{character.ToString()}' — " +
                       "Windows and Wine both refuse it.";
            }
        }

        if (folderName != folderName.TrimEnd(' ', '.'))
        {
            return "A mod folder name cannot end in a space or a dot — Windows silently drops them.";
        }

        if (folderName.TrimStart() != folderName)
        {
            return "A mod folder name cannot begin with a space.";
        }

        return null;
    }

    /// <summary>The characters NTFS refuses in a name, minus the separators handled above.</summary>
    private static readonly System.Buffers.SearchValues<char> WindowsReservedCharacters =
        System.Buffers.SearchValues.Create(":*?\"<>|");

    /// <summary>Directory names 3DMigoto itself defines, which only ever appear inside a mod.</summary>
    public static IReadOnlyList<string> ModContentDirectoryNames { get; } =
        ["ShaderFixes", "ShaderCache"];

    /// <summary>Whether a file is mod content, which never sits at a character folder's top level.</summary>
    /// <param name="fileName">The file's own name, not a path.</param>
    /// <returns><c>true</c> when its presence marks the containing folder as a mod.</returns>
    public static bool IsModContentFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);

        return extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ib", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".buf", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dds", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a directory tree holds mod content at any depth: the question for an archive.</summary>
    /// <param name="directory">The directory to look under. Not modified.</param>
    /// <returns><see langword="true"/> when it holds an INI, a structural directory, or a file 3DMigoto
    /// loads.</returns>
    public static bool ContainsModContent(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        try
        {
            foreach (var known in ModContentDirectoryNames)
            {
                if (Directory.Exists(Path.Combine(directory, known)))
                {
                    return true;
                }
            }

            if (Directory.Exists(Path.Combine(directory, ModConfigSchema.DirectoryName)))
            {
                return true;
            }

            foreach (var file in FileTree.Files(directory))
            {
                if (IsModContentFile(Path.GetFileName(file)))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: no. The caller reports the failure.
            return false;
        }
    }

    /// <summary>Whether a directory is itself a mod rather than a folder of mods: the question for Mods.</summary>
    /// <param name="directory">The directory to look at. Not modified.</param>
    /// <returns><see langword="true"/> when it is a mod.</returns>
    /// <remarks>
    /// Mod content at its top level, or loose files and no subfolders, make a mod. A mod whose content is all in
    /// subfolders looks like a character folder holding one mod, and is read as that.
    /// </remarks>
    public static bool LooksLikeModFolder(string directory)
    {
        try
        {
            if (Directory.Exists(Path.Combine(directory, ModConfigSchema.DirectoryName)))
            {
                return true;
            }

            foreach (var known in ModsFolderLayout.ModContentDirectoryNames)
            {
                if (Directory.Exists(Path.Combine(directory, known)))
                {
                    return true;
                }
            }

            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToList();

            if (files.Exists(file => ModsFolderLayout.IsModContentFile(Path.GetFileName(file))))
            {
                return true;
            }

            // Loose files and no subfolders: a mod, even a readme and a preview alone. An empty folder is a character.
            return files.Count > 0 &&
                   !Directory.EnumerateDirectories(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: read as a character folder, the harmless reading.
            return false;
        }
    }
}
