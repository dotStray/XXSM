using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Merge;

namespace Xxsm.Packs.Sorting;

/// <summary>Which mods no character owns, and which of those are in <c>Others</c>: the grid's two tiles.</summary>
/// <remarks>A folder is a character's when some character's <c>ModFilesName</c> names it, hidden ones too.</remarks>
public static class UnsortedMods
{
    /// <summary>The character whose folder this is, or null when no character claims it.</summary>
    /// <param name="folderName">A folder directly inside the Mods folder.</param>
    /// <param name="data">The merged pack and overlay.</param>
    /// <returns>The character, or null.</returns>
    public static MergedVariant? CharacterFor(string? folderName, GameData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return folderName is { Length: > 0 }
            ? data.Variants.FirstOrDefault(variant => PathComparer.AreNamesEqual(variant.ModFilesName, folderName))
            : null;
    }

    /// <summary>Whether a top-level folder is <c>Others</c>, and no character's.</summary>
    /// <param name="folderName">A folder directly inside the Mods folder.</param>
    /// <param name="data">The merged pack and overlay.</param>
    /// <returns><c>true</c> for the folder auto-sort files unidentified mods into.</returns>
    public static bool IsOthersFolder(string? folderName, GameData data) =>
        PathComparer.AreNamesEqual(folderName, ModsFolderLayout.UnsortedFolderName)
        && CharacterFor(folderName, data) is null;

    /// <summary>Whether a mod belongs to no character and is not in <c>Others</c>.</summary>
    /// <param name="mod">The mod.</param>
    /// <param name="data">The merged pack and overlay.</param>
    /// <returns><c>true</c> when it is loose, or in a folder that is neither a character's nor <c>Others</c>.</returns>
    public static bool Contains(InstalledMod mod, GameData data)
    {
        ArgumentNullException.ThrowIfNull(mod);

        return CharacterFor(mod.VariantFolderName, data) is null && !IsOthersFolder(mod.VariantFolderName, data);
    }

    /// <summary>Whether a mod is in <c>Others</c>.</summary>
    /// <param name="mod">The mod.</param>
    /// <param name="data">The merged pack and overlay.</param>
    /// <returns><c>true</c> when its folder is <c>Others</c>.</returns>
    public static bool IsInOthers(InstalledMod mod, GameData data)
    {
        ArgumentNullException.ThrowIfNull(mod);

        return IsOthersFolder(mod.VariantFolderName, data);
    }

    /// <summary>Every mod that belongs to no character and is not in <c>Others</c>.</summary>
    /// <param name="inventory">What is on disk.</param>
    /// <param name="data">The merged pack and overlay.</param>
    public static IReadOnlyList<InstalledMod> Find(ModsInventory inventory, GameData data)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(data);

        return
        [
            .. inventory.UnfiledMods,
            .. inventory.VariantFolders
                .Where(folder => CharacterFor(folder.Name, data) is null && !IsOthersFolder(folder.Name, data))
                .SelectMany(folder => folder.Mods),
        ];
    }

    /// <summary>The <c>Others</c> folder, or null when there is none or a character owns the name.</summary>
    /// <param name="inventory">What is on disk.</param>
    /// <param name="data">The merged pack and overlay.</param>
    public static VariantFolder? OthersFolder(ModsInventory inventory, GameData data)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(data);

        return inventory.VariantFolders.FirstOrDefault(folder => IsOthersFolder(folder.Name, data));
    }
}
