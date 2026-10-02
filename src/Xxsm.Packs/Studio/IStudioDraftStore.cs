using Xxsm.Packs.Loading;

namespace Xxsm.Packs.Studio;

/// <summary>Reads and writes Pack Studio drafts as loose pack files, atomically, with a backup.</summary>
public interface IStudioDraftStore
{
    /// <summary>The folder a game's draft lives in.</summary>
    /// <exception cref="Xxsm.Core.ModOperationException">The game id cannot name a folder.</exception>
    string GetDraftDirectory(string gameId);

    /// <summary>Whether a draft exists for a game.</summary>
    /// <returns><see langword="true"/> when its folder holds a manifest.</returns>
    bool Exists(string gameId);

    /// <summary>Lists every draft, including any that can no longer be read.</summary>
    Task<IReadOnlyList<StudioDraftSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a draft.</summary>
    /// <returns>The draft, with every row kept, valid or not.</returns>
    /// <exception cref="Xxsm.Core.PackLoadException">There is no such draft, or a file cannot be read (the
    /// parser's message names the line).</exception>
    Task<PackDraft> ReadAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Saves a draft, writing only the files that changed.</summary>
    /// <exception cref="Xxsm.Core.ModOperationException">A file could not be written; its previous version
    /// stays.</exception>
    Task<StudioSaveResult> WriteAsync(PackDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Saves a brand-new draft, refusing to replace one that exists.</summary>
    /// <param name="draft">The draft, normally from <see cref="PackDrafts.Create"/>.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">A draft for that game already exists, or the draft could
    /// not be written.</exception>
    Task<StudioSaveResult> CreateAsync(PackDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Starts a draft from an installed pack, copying its pictures beside it.</summary>
    /// <exception cref="Xxsm.Core.ModOperationException">A draft for that game already exists, or it could not be
    /// written.</exception>
    Task<PackDraft> CreateFromPackAsync(GamePack pack, CancellationToken cancellationToken = default);

    /// <summary>Moves a draft's whole folder to the trash, so the deletion can be undone.</summary>
    /// <param name="gameId">The game. A draft that can no longer be read can still be deleted.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">There is no such draft, or the folder could not be
    /// moved.</exception>
    Task<Xxsm.Core.Io.TrashResult> DeleteAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Puts a deleted draft back where it was; never over a draft for the same game started since.</summary>
    /// <param name="trashed">What <see cref="DeleteAsync"/> returned, or its trash record read back.</param>
    /// <param name="cancellationToken">Cancels before anything is moved.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The record is not a draft's, a draft for that game exists
    /// again, the trash no longer holds it, or the move failed.</exception>
    Task<string> RestoreAsync(Xxsm.Core.Io.TrashResult trashed, CancellationToken cancellationToken = default);

    /// <summary>The size of a picture inside a draft, for the validator's picture checks.</summary>
    /// <param name="gameId">The draft's game.</param>
    /// <param name="relativePath">A pack-relative path such as <c>images/Name.png</c>.</param>
    /// <returns>The size in bytes, or null when the draft has no such file inside its folder.</returns>
    long? GetImageSize(string gameId, string relativePath);

    /// <summary>A token that changes whenever a picture inside a draft is replaced under the same name.</summary>
    /// <param name="gameId">The draft's game.</param>
    /// <param name="relativePath">A pack-relative path such as <c>images/Name.png</c>.</param>
    /// <returns>Its size and write time as one string, or null as for <see cref="GetImageSize"/>.</returns>
    string? GetImageVersion(string gameId, string relativePath);

    /// <summary>Opens a picture inside a draft for reading, for the Studio table's thumbnails.</summary>
    /// <param name="gameId">The draft's game.</param>
    /// <param name="relativePath">A pack-relative path such as <c>images/Name.png</c>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A seekable stream the caller disposes, or null as for <see cref="GetImageSize"/>.</returns>
    Task<Stream?> OpenImageAsync(string gameId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Copies a picture into a draft's <c>images/</c> folder.</summary>
    /// <param name="gameId">The draft's game.</param>
    /// <param name="name">A variant's internal name, or <c>_game</c> for the icon; the extension is kept.</param>
    /// <param name="sourceFile">The picture to copy. Left untouched.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The file does not exist, is not a picture a pack can
    /// carry, the name cannot name a file, or the copy failed.</exception>
    Task<string> StoreImageAsync(
        string gameId,
        string name,
        string sourceFile,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a picture file or image bytes into a draft's <c>images/</c> under a name of its own.</summary>
    /// <remarks>Never replaces a file already there: the new one becomes <c>Name-2.png</c>, and so on.</remarks>
    /// <param name="gameId">The draft's game.</param>
    /// <param name="name">What to call it: a variant's internal name.</param>
    /// <param name="image">The picture.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The pack-relative path to put in the variant's <c>image</c>.</returns>
    /// <exception cref="Xxsm.Core.ModOperationException">The picture is not a readable PNG, JPEG or WebP, the name
    /// cannot name a file, or the copy failed.</exception>
    Task<string> StoreImageAsync(
        string gameId,
        string name,
        Xxsm.Core.Mods.PreviewImageSource image,
        CancellationToken cancellationToken = default);
}
