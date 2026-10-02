namespace Xxsm.Core.Mods;

/// <summary>Stable codes for problems found while scanning a mod folder.</summary>
public static class ModDiagnosticCodes
{
    /// <summary>A directory inside the mod could not be listed.</summary>
    public const string UnreadableDirectory = "mod.directory.unreadable";

    /// <summary>A file inside the mod could not be read.</summary>
    public const string UnreadableFile = "mod.file.unreadable";

    /// <summary>A directory was a symbolic link and was not followed.</summary>
    public const string SkippedSymlink = "mod.directory.symlink-skipped";

    /// <summary>A <c>hash</c> setting's value was not 8 or 16 hex digits.</summary>
    public const string UnreadableHash = "mod.hash.unreadable";

    /// <summary>One override section gave more than one hash.</summary>
    public const string ConflictingHashes = "mod.hash.conflicting";

    /// <summary>Two folders differ only by case, which 3DMigoto under Wine will see as one.</summary>
    public const string CaseCollision = "mods.folder.case-collision";

    /// <summary>A mod folder is loose at the root of the Mods folder, not filed under a character.</summary>
    public const string UnfiledMod = "mods.folder.unfiled";

    /// <summary>A mod's <c>.xxsm/mod.json</c> exists but could not be read.</summary>
    public const string UnreadableModConfig = "mod.config.unreadable";

    /// <summary>A character folder contains no mods.</summary>
    public const string EmptyVariantFolder = "mods.folder.empty";

    /// <summary>An XXSM trash folder an older XXSM made inside Mods, where the game still loads its mods.</summary>
    public const string TrashInsideMods = "mods.folder.trash-inside";

    /// <summary>A spare copy an interrupted update or move left beside the mod. It can go to the trash.</summary>
    public const string LeftoverSpareCopy = "mods.folder.leftover-spare";

    /// <summary>A mod's only copy, left under a working name by an interrupted move. It can be put back.</summary>
    public const string LeftoverOnlyCopy = "mods.folder.leftover-only";

    /// <summary>A move's working folder holding something XXSM cannot place. Left to be looked at.</summary>
    public const string LeftoverUnrecognised = "mods.folder.leftover-unrecognised";
}
