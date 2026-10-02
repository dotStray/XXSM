using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Characters;

/// <summary>The Character Manager's data layer: everything that writes to the user's overlay.</summary>
/// <remarks>Each call re-reads the overlay; the game data only decides. Callers reload afterwards.</remarks>
public interface ICharacterEditor
{
    /// <summary>Creates a character in the user's overlay. Only a name is required.</summary>
    /// <param name="data">The merged game data, for the names already taken.</param>
    /// <param name="request">What to create.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The name is blank, or the overlay could not be
    /// written.</exception>
    Task<CharacterEditResult> CreateAsync(
        GameData data, NewCharacter request, CancellationToken cancellationToken = default);

    /// <summary>Edits any variant; a pack variant becomes modified and locked against pack updates.</summary>
    /// <param name="data">The merged game data, for the variant's current values.</param>
    /// <param name="internalName">The variant to edit.</param>
    /// <param name="edit">The fields to change; unchanged ones are left alone.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">There is no such variant, the edit is invalid, or the
    /// overlay could not be written.</exception>
    Task<CharacterEditResult> EditAsync(
        GameData data,
        string internalName,
        CharacterEdit edit,
        CancellationToken cancellationToken = default);

    /// <summary>Adds hashes to a variant; duplicates and conflicts are reported, never refused.</summary>
    /// <param name="data">The merged game data, for the hashes already recorded.</param>
    /// <param name="internalName">The variant to add them to.</param>
    /// <param name="hashes">The hashes to add; their variant is replaced with <paramref name="internalName"/>.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">There is no such variant, or the overlay could not be
    /// written.</exception>
    Task<CharacterEditResult> AddHashesAsync(
        GameData data,
        string internalName,
        IReadOnlyList<PackHashEntry> hashes,
        CancellationToken cancellationToken = default);

    /// <summary>Removes hashes, in any capitalisation, from a variant.</summary>
    /// <returns>What was removed, and a diagnostic for anything the variant did not have.</returns>
    /// <exception cref="Xxsm.Core.ModOperationException">There is no such variant, or the overlay could not be
    /// written.</exception>
    Task<CharacterEditResult> RemoveHashesAsync(
        GameData data,
        string internalName,
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken = default);

    /// <summary>Puts a variant's fields back to the pack's values, clearing the user's override.</summary>
    /// <param name="data">The merged game data.</param>
    /// <param name="internalName">The variant to reset.</param>
    /// <param name="fields">The overlay field names to reset, or null for the whole variant.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">There is no such variant, a full reset of a custom one
    /// was asked for (that is a deletion), or the overlay could not be written.</exception>
    Task<CharacterEditResult> ResetAsync(
        GameData data,
        string internalName,
        IReadOnlyList<string>? fields,
        CancellationToken cancellationToken = default);

    /// <summary>Keeps the user's values but lets future pack updates change them again.</summary>
    /// <param name="data">The merged game data.</param>
    /// <param name="internalName">The variant to unlock.</param>
    /// <param name="fields">The fields to keep locked, or null to unlock the variant entirely.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">There is no such variant, it is custom, or the overlay
    /// could not be written.</exception>
    Task<CharacterEditResult> UnlockAsync(
        GameData data,
        string internalName,
        IReadOnlyList<string>? fields,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a custom variant, moving its mods elsewhere first; its empty folder is left on disk.</summary>
    /// <param name="data">The merged game data.</param>
    /// <param name="internalName">The custom variant to delete.</param>
    /// <param name="modsDirectory">The Mods folder its mods live under.</param>
    /// <param name="rehomeTo">The character its mods move to, or null for <c>Others</c>.</param>
    /// <param name="cancellationToken">Cancels between mods; the entry goes only once every mod has moved.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">There is no such variant, it is not custom, the target
    /// does not exist, a mod could not be moved, or the overlay could not be written.</exception>
    Task<CharacterDeleteResult> DeleteAsync(
        GameData data,
        string internalName,
        string modsDirectory,
        string? rehomeTo,
        CancellationToken cancellationToken = default);

    /// <summary>Undoes a deletion: puts the character back and moves its mods home. Never overwrites.</summary>
    /// <param name="data">The merged game data as it is now.</param>
    /// <param name="deletion">What the deletion took away.</param>
    /// <param name="recordPath">The record it came from, or null; deleted once all is back, else rewritten.</param>
    /// <param name="cancellationToken">Cancels before the character is restored, or between mods.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The deletion is another game's, its internal name is
    /// taken, or the overlay could not be written. Nothing has moved.</exception>
    Task<CharacterRestoreResult> RestoreDeletedAsync(
        GameData data,
        CharacterDeletion deletion,
        string? recordPath,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a deletion back from the record <see cref="DeleteAsync"/> wrote.</summary>
    /// <exception cref="Xxsm.Core.ModOperationException">The record is missing or unreadable.</exception>
    Task<CharacterDeletion> ReadDeletionAsync(string recordPath, CancellationToken cancellationToken = default);
}
