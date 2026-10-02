namespace Xxsm.Core.Io;

/// <summary>Where an XXSM deletion goes. Nothing deletes user content any other way.</summary>
public interface ITrashService
{
    /// <summary>Moves a file or directory to the trash.</summary>
    /// <param name="path">The file or directory to trash. Must exist.</param>
    /// <param name="fallbackRoot">Where to make <c>.xxsm-trash/</c> when the disk has no usable trash; for a mod, the
    /// folder beside Mods. Null for no fallback.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>What moved where, and how.</returns>
    /// <exception cref="ModOperationException">The path does not exist, or every trash location failed; with the OS
    /// error text and the original exception inside.</exception>
    Task<TrashResult> TrashAsync(string path, string? fallbackRoot, CancellationToken cancellationToken = default);

    /// <summary>Puts a trashed item back where it was, recreating its folder. Never overwrites.</summary>
    /// <param name="trashed">What <see cref="TrashAsync"/> reported, or what <see cref="ReadRecordAsync"/> read
    /// back.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <returns>The path the item is at again.</returns>
    /// <exception cref="ModOperationException">The item is no longer in the trash, something occupies its path, or the
    /// move failed; with the OS error text.</exception>
    Task<string> RestoreAsync(TrashResult trashed, CancellationToken cancellationToken = default);

    /// <summary>Reads a <c>.trashinfo</c> record back into the result trashing produced, for a later process.</summary>
    /// <param name="infoFilePath">The record, as <see cref="TrashResult.InfoFilePath"/> named it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The trash result, ready for <see cref="RestoreAsync"/>.</returns>
    /// <exception cref="ModOperationException">The record is missing or not a trash record XXSM can read.</exception>
    Task<TrashResult> ReadRecordAsync(string infoFilePath, CancellationToken cancellationToken = default);
}

/// <summary>Which trash location a deletion actually landed in.</summary>
public enum TrashMethod
{
    /// <summary>The user's home trash, normally <c>~/.local/share/Trash</c>.</summary>
    HomeTrash,

    /// <summary>A trash directory on the item's own volume, <c>&lt;top&gt;/.Trash-&lt;uid&gt;</c>.</summary>
    VolumeTrash,

    /// <summary>XXSM's own <c>.xxsm-trash/</c> folder, used when neither of the above worked.</summary>
    FallbackFolder,
}

/// <summary>The outcome of a successful trash operation.</summary>
/// <param name="OriginalPath">Where the item was, before the move.</param>
/// <param name="TrashedPath">Where the item is now.</param>
/// <param name="InfoFilePath">The <c>.trashinfo</c> record written alongside it.</param>
/// <param name="Method">Which trash location was used.</param>
/// <param name="DeletedAt">The timestamp recorded in the <c>.trashinfo</c> file.</param>
public sealed record TrashResult(
    string OriginalPath,
    string TrashedPath,
    string InfoFilePath,
    TrashMethod Method,
    DateTimeOffset DeletedAt);
