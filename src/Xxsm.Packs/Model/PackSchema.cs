namespace Xxsm.Packs.Model;

/// <summary>The pack format versions this build understands, and the file names a pack is made of.</summary>
/// <remarks>A pack outside the range is listed as needing a newer XXSM and is never loaded.</remarks>
public static class PackSchema
{
    /// <summary>The pack schema versions this build can read.</summary>
    public static IReadOnlySet<int> SupportedVersions { get; } = new HashSet<int> { 1 };

    /// <summary>The overlay schema versions this build can read.</summary>
    public static IReadOnlySet<int> SupportedOverlayVersions { get; } = new HashSet<int> { 1 };

    /// <summary>The manifest file name. Required.</summary>
    public const string ManifestFile = "manifest.json";

    /// <summary>The game definition file name. Required.</summary>
    public const string GameFile = "game.json";

    /// <summary>The variant list file name. Required: every entry the pack holds is in it.</summary>
    public const string VariantsFile = "variants.json";

    /// <summary>The hash index file name. Optional — a pack of hashless characters is valid.</summary>
    public const string HashesFile = "hashes.json";

    /// <summary>The directory holding portraits and icons.</summary>
    public const string ImagesDirectory = "images";

    /// <summary>The default prefix marking a disabled mod folder.</summary>
    public const string DefaultDisabledPrefix = "DISABLED_";

    /// <summary>Reports whether this build can read a pack declaring the given version.</summary>
    /// <param name="packSchemaVersion">The version from the manifest.</param>
    /// <returns><see langword="true"/> when the pack is readable.</returns>
    public static bool IsSupported(int packSchemaVersion) => SupportedVersions.Contains(packSchemaVersion);

    /// <summary>Reports whether this build can read an overlay declaring the given version.</summary>
    /// <param name="overlaySchemaVersion">The version from the overlay.</param>
    /// <returns><see langword="true"/> when the overlay is readable.</returns>
    public static bool IsOverlaySupported(int overlaySchemaVersion) =>
        SupportedOverlayVersions.Contains(overlaySchemaVersion);
}
