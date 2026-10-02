namespace Xxsm.Core.Mods;

/// <summary>Remembers which character a person filed a mod under, so auto-sort leaves it there.</summary>
public interface IModFiling
{
    /// <summary>Records that a person filed a mod under a character.</summary>
    /// <param name="modFolder">The mod folder, where it is now.</param>
    /// <param name="variantInternalName">The character's internal name.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">The folder is missing, its metadata could not be read, or the write
    /// failed.</exception>
    Task RememberAsync(string modFolder, string variantInternalName, CancellationToken cancellationToken = default);

    /// <summary>Forgets a person's filing, so the next auto-sort decides where the mod belongs.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>True when a filing was forgotten; false when there was none, and nothing is written.</returns>
    /// <exception cref="ModOperationException">There is no such folder, the metadata could not be read, or the write
    /// failed.</exception>
    Task<bool> ForgetAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Moves a mod under a character and remembers that a person put it there.</summary>
    /// <param name="modFolder">The mod folder to move.</param>
    /// <param name="destinationParent">The character's folder. Created if it does not exist.</param>
    /// <param name="variantInternalName">The character's internal name.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>What the move did, and why the choice could not be saved, when it could not.</returns>
    /// <exception cref="ModOperationException">The move itself failed. Nothing was remembered.</exception>
    Task<ModFilingResult> MoveAsync(
        string modFolder,
        string destinationParent,
        string variantInternalName,
        CancellationToken cancellationToken = default);

    /// <summary>Puts a hand-moved mod back and restores its old filing. Never renames on the way back.</summary>
    /// <param name="currentFolder">Where the mod is now: <see cref="ModOperationResult.ToPath"/>.</param>
    /// <param name="originalFolder">Where it was: <see cref="ModOperationResult.FromPath"/>.</param>
    /// <param name="previousFiling">The character it was filed under before, or null to forget the filing.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>What the move back did.</returns>
    /// <exception cref="ModOperationException">The mod is not where it was left, its old path is taken, the move
    /// failed, or the old filing could not be written.</exception>
    Task<ModOperationResult> UndoMoveAsync(
        string currentFolder,
        string originalFolder,
        string? previousFiling,
        CancellationToken cancellationToken = default);
}

/// <summary>What moving a mod by hand did.</summary>
/// <param name="Move">The move.</param>
/// <param name="RememberError">Why the choice could not be saved, in the system's words; null when it was.</param>
/// <param name="PreviousFiling">The character it was filed under before, or null; what an undo restores.</param>
public sealed record ModFilingResult(ModOperationResult Move, string? RememberError, string? PreviousFiling);
