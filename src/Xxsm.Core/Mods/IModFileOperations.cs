using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>Every change XXSM makes to mod folders: never overwriting, deleting or clashing by case.</summary>
/// <remarks>Asking for the state a mod is already in reports <see cref="ModOperationResult.Changed"/> false and touches
/// nothing.</remarks>
public interface IModFileOperations
{
    /// <summary>Enables or disables a mod by renaming its folder.</summary>
    /// <param name="modFolder">The mod folder, in either state.</param>
    /// <param name="enabled">The state to put it in.</param>
    /// <param name="cancellationToken">Cancels before anything is renamed.</param>
    /// <returns>What happened, including the folder's new path.</returns>
    /// <exception cref="ModOperationException">The folder is missing, the new name is taken or clashes by case, or the
    /// rename failed; with the system's words, and nothing copied or deleted instead.</exception>
    Task<ModOperationResult> SetEnabledAsync(
        string modFolder, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Moves a mod folder under a different character.</summary>
    /// <param name="modFolder">The mod folder to move.</param>
    /// <param name="destinationParent">The character folder to move it into; created unless it would clash by
    /// case.</param>
    /// <param name="name">The folder name at the destination; the current name when null.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>What happened; a taken name gains a number, reported in <see
    /// cref="ModOperationResult.DisambiguatedFromName"/>.</returns>
    /// <exception cref="ModOperationException">The mod folder is missing, the destination could not be created, or the
    /// move failed. A copy across disks is verified before anything is removed.</exception>
    Task<ModOperationResult> MoveAsync(
        string modFolder,
        string destinationParent,
        string? name = null,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a mod folder in place; the displayed name is <see cref="ModConfig.CustomName"/>.</summary>
    /// <param name="modFolder">The mod folder to rename, in either state.</param>
    /// <param name="newName">The name as typed; a <c>DISABLED_</c> prefix is ignored and the current state
    /// kept.</param>
    /// <param name="cancellationToken">Cancels before anything is renamed.</param>
    /// <returns>What happened, including the folder's new path.</returns>
    /// <exception cref="ModOperationException">The folder is missing, the name unusable or taken (the message offers a
    /// free one), or the rename failed; with the system's words.</exception>
    Task<ModOperationResult> RenameAsync(
        string modFolder, string newName, CancellationToken cancellationToken = default);

    /// <summary>Asks what a mod folder could be called, without renaming anything.</summary>
    /// <param name="modFolder">The mod folder that would be renamed.</param>
    /// <param name="newName">The name the user typed.</param>
    /// <param name="cancellationToken">Cancels the look at the folder.</param>
    /// <returns>The name asked for and a free one; the mod's own current name never counts as taken.</returns>
    /// <exception cref="ModOperationException">The folder does not exist, or the name is unusable whatever it is
    /// suffixed with.</exception>
    Task<ModNameProposal> ProposeFolderNameAsync(
        string modFolder, string newName, CancellationToken cancellationToken = default);

    /// <summary>Copies a folder into the Mods folder as a new mod.</summary>
    /// <param name="sourceFolder">The folder to install. Left untouched.</param>
    /// <param name="destinationParent">The character folder to install it under.</param>
    /// <param name="name">The name to give the installed mod; the source folder's own name when null.</param>
    /// <param name="keepExistingName">True when the name is one the mod already had in the Mods folder, so it is kept
    /// unchecked, as an update does.</param>
    /// <param name="cancellationToken">Cancels the copy, leaving nothing behind.</param>
    /// <returns>What happened, including the installed mod's path.</returns>
    /// <exception cref="ModOperationException">The source is missing, the name is unusable and not kept, the
    /// destination could not be created, or the copy failed.</exception>
    Task<ModOperationResult> InstallAsync(
        string sourceFolder,
        string destinationParent,
        string? name = null,
        bool keepExistingName = false,
        CancellationToken cancellationToken = default);

    /// <summary>Moves a mod to the trash.</summary>
    /// <param name="modFolder">The mod folder to delete.</param>
    /// <param name="modsDirectory">The Mods folder; a fallback <c>.xxsm-trash</c> goes in the folder beside it, never
    /// inside.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>What happened, with the trash record in <see cref="ModOperationResult.Trash"/>.</returns>
    /// <exception cref="ModOperationException">The mod folder does not exist, or every trash location
    /// failed.</exception>
    Task<ModOperationResult> DeleteAsync(
        string modFolder, string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Moves a character folder with no mods in it to the trash, checking the disk again first.</summary>
    /// <param name="characterFolder">A folder directly inside the Mods folder.</param>
    /// <param name="modsDirectory">The Mods folder it must sit directly inside; the fallback trash is beside
    /// it.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>What happened, with the trash record <see cref="RestoreAsync"/> puts back.</returns>
    /// <exception cref="ModOperationException">The folder is missing, not directly inside Mods, a mod, or holds a mod;
    /// or every trash location failed.</exception>
    Task<ModOperationResult> DeleteEmptyCharacterFolderAsync(
        string characterFolder, string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Moves a trash folder an older XXSM made inside Mods to beside it, keeping every record.</summary>
    /// <param name="trashFolder">A <c>.xxsm-trash</c> folder inside <paramref name="modsDirectory"/>.</param>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="cancellationToken">Stops between items.</param>
    /// <returns>Where it went, how many items moved, and what could not.</returns>
    /// <exception cref="ModOperationException">The folder is not an XXSM trash folder inside Mods, or Mods has no
    /// folder above it.</exception>
    Task<TrashMoveOutResult> MoveTrashOutAsync(
        string trashFolder, string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Puts a deleted mod back where it was. Never overwrites.</summary>
    /// <param name="trashed">The trash record its deletion produced.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>What happened: from the trash, to the mod's original path.</returns>
    /// <exception cref="ModOperationException">The mod is no longer in the trash, its original path is taken, or the
    /// move failed.</exception>
    Task<ModOperationResult> RestoreAsync(TrashResult trashed, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IModFileOperations.MoveTrashOutAsync"/> did.</summary>
/// <param name="Source">The trash folder that was inside the Mods folder.</param>
/// <param name="Destination">The trash folder beside the Mods folder.</param>
/// <param name="Moved">How many deleted items moved.</param>
/// <param name="Problems">Each item that could not move, with the system's reason; it stays where it was.</param>
public sealed record TrashMoveOutResult(string Source, string Destination, int Moved, IReadOnlyList<string> Problems);
