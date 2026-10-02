using Xxsm.Packs.Merge;

namespace Xxsm.Packs.Portraits;

/// <summary>Resolves a variant's portrait (pack-relative, <c>file://</c> or <c>https://</c>) to bytes.</summary>
public interface IPortraitSource
{
    /// <summary>Opens the variant's portrait, if it has one and it could be read.</summary>
    /// <returns>A seekable stream at the start, or null when there is no image or it could not be read.</returns>
    Task<Stream?> OpenAsync(
        GameData data, MergedVariant variant, CancellationToken cancellationToken = default);

    /// <summary>Opens any picture a pack names, portrait or icon, as <see cref="OpenAsync"/> does.</summary>
    /// <param name="packDirectory">The pack a relative path is resolved against, or null when there is none.</param>
    /// <param name="image">The path or address as the pack gives it.</param>
    /// <param name="describe">What to call it in a log line, such as a character's or game's id.</param>
    /// <param name="cancellationToken">Cancels the read or the fetch.</param>
    /// <returns>A readable stream, or null when there is nothing to read. Never throws for content.</returns>
    Task<Stream?> OpenImageAsync(
        string? packDirectory, string image, string describe, CancellationToken cancellationToken = default);
}
