namespace Xxsm.Packs.Loading;

/// <summary>Stable codes for <see cref="PackDiagnostic"/>, which do not change when the wording does.</summary>
public static class PackDiagnosticCodes
{
    /// <summary>A variant had no internal name and was skipped.</summary>
    public const string MissingInternalName = "pack.variant.no-internal-name";

    /// <summary>A variant's internal name would not survive becoming a folder name.</summary>
    public const string InvalidInternalName = "pack.variant.invalid-internal-name";

    /// <summary>Two variants shared an internal name; the second was skipped.</summary>
    public const string DuplicateInternalName = "pack.variant.duplicate-internal-name";

    /// <summary>A variant's base character does not exist in the pack.</summary>
    public const string DanglingBaseCharacter = "pack.family.dangling-base";

    /// <summary>A variant names itself as its own base.</summary>
    public const string SelfReferencingFamily = "pack.family.self-reference";

    /// <summary>No member of a family is marked as the default.</summary>
    public const string FamilyWithoutDefault = "pack.family.no-default";

    /// <summary>More than one member of a family is marked as the default.</summary>
    public const string FamilyWithMultipleDefaults = "pack.family.multiple-defaults";

    /// <summary>A hash entry had an empty value and was ignored.</summary>
    public const string EmptyHash = "pack.hashes.empty-value";

    /// <summary>A hash entry names a variant the pack does not contain.</summary>
    public const string HashForUnknownVariant = "pack.hashes.unknown-variant";

    /// <summary>A variant has no hashes. Informational — this is an expected state.</summary>
    public const string VariantWithoutHashes = "pack.hashes.none-for-variant";

    /// <summary><c>game.json</c> and <c>manifest.json</c> disagree about the game id.</summary>
    public const string GameIdMismatch = "pack.game-id-mismatch";

    /// <summary>An overlay entry refers to a variant that no longer exists.</summary>
    public const string OverlayForUnknownVariant = "overlay.unknown-variant";

    /// <summary>A custom overlay variant is missing the one field it needs.</summary>
    public const string OverlayCustomWithoutName = "overlay.custom-without-name";
}
