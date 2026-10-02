namespace Xxsm.Core.Mods;

/// <summary>How sure XXSM is that a file is a picture of the mod, best first; add new ones last.</summary>
public enum ModPreviewMatch
{
    /// <summary>The mod's own <c>.xxsm/mod.json</c> named it. Nothing outranks it.</summary>
    Declared = 0,

    /// <summary>Called exactly <c>preview</c>, whatever the extension. The overwhelming convention.</summary>
    NamedExactly = 1,

    /// <summary>Starts with <c>preview</c> — <c>preview 2.png</c>, <c>preview_thicc.png</c>.</summary>
    NamedPrefix = 2,

    /// <summary>Starts with <c>prev</c> — the <c>prev0.png</c>, <c>prev1.png</c> convention.</summary>
    NamedAbbreviated = 3,

    /// <summary>Mentions a preview somewhere — <c>00cover.png</c>, <c>thumb.jpg</c>, <c>screenshot.png</c>.</summary>
    NamedLike = 4,

    /// <summary>Nothing is named like a preview; this is the most promising image. A guess.</summary>
    Unnamed = 5,
}

/// <summary>A mod's preview image, and how sure XXSM is that it is the right file.</summary>
/// <param name="Path">The image's absolute path.</param>
/// <param name="RelativePath">Its path relative to the mod folder, as <c>imagePath</c> writes it.</param>
/// <param name="Match">Why this file was chosen.</param>
/// <param name="LastWriteTimeUtc">When the file last changed, so a cache can notice it replaced.</param>
public sealed record ModPreview(
    string Path, string RelativePath, ModPreviewMatch Match, DateTimeOffset LastWriteTimeUtc);

/// <summary>Finds a mod's picture: the one its metadata declares, else its most preview-like image.</summary>
/// <remarks>Textures and watermarks are excluded by name, not ranked low.</remarks>
public interface IModPreviewSource
{
    /// <summary>Works out which file to show for a mod, without reading its contents.</summary>
    /// <param name="modDirectory">The mod's own folder.</param>
    /// <param name="config">The mod's metadata, when it has one; a declared picture that is missing is ignored.</param>
    /// <param name="bounds">How far to look; <see cref="ModScanBounds.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The chosen image, or null when the folder holds no candidate.</returns>
    Task<ModPreview?> FindAsync(
        string modDirectory,
        ModConfig? config = null,
        ModScanBounds? bounds = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the image <see cref="FindAsync"/> would choose.</summary>
    /// <param name="modDirectory">The mod's own folder.</param>
    /// <param name="config">The mod's metadata, when it has one.</param>
    /// <param name="cancellationToken">Cancels the search and the open.</param>
    /// <returns>A seekable stream at its start, or null when there is no preview or it cannot be read.</returns>
    Task<Stream?> OpenAsync(
        string modDirectory, ModConfig? config = null, CancellationToken cancellationToken = default);
}
