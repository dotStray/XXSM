using Xxsm.Packs.Loading;

namespace Xxsm.Packs.Studio;

/// <summary>The few kinds a pack's problems fall into, to narrow a long Problems list. Stable ids.</summary>
public static class PackProblemKinds
{
    /// <summary>A character with no hashes yet, which sorts by name until it has some.</summary>
    public const string NoHashes = "no-hashes";

    /// <summary>A hash on too many characters, or on more than one family, to identify anyone.</summary>
    public const string SharedHashes = "shared-hashes";

    /// <summary>Any other trouble with a hash: malformed, empty, or naming nobody.</summary>
    public const string Hashes = "hashes";

    /// <summary>A missing, unreadable, oversized or misplaced portrait.</summary>
    public const string Pictures = "pictures";

    /// <summary>Names and internal names, and the folders mods would be filed in.</summary>
    public const string Names = "names";

    /// <summary>Outfits and families: dangling links, defaults, outfits of outfits.</summary>
    public const string Outfits = "outfits";

    /// <summary>Attributes the game declares and the values characters give them.</summary>
    public const string Attributes = "attributes";

    /// <summary>The game's own details: its id, name and version.</summary>
    public const string Game = "game";

    /// <summary>Anything a newer check reports that this list does not know yet.</summary>
    public const string Other = "other";

    /// <summary>Every kind, in the order a filter should offer them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [NoHashes, SharedHashes, Hashes, Pictures, Names, Outfits, Attributes, Game, Other];

    private static readonly Dictionary<string, string> ByCode = new(StringComparer.Ordinal)
    {
        [PackDiagnosticCodes.VariantWithoutHashes] = NoHashes,

        [PackValidationCodes.HashFansOut] = SharedHashes,
        [PackValidationCodes.HashSharedBetweenFamilies] = SharedHashes,

        [PackValidationCodes.HashMalformed] = Hashes,
        [PackValidationCodes.IgnoredHashMalformed] = Hashes,
        [PackValidationCodes.IgnoredHashUnused] = Hashes,
        [PackDiagnosticCodes.EmptyHash] = Hashes,
        [PackDiagnosticCodes.HashForUnknownVariant] = Hashes,

        [PackValidationCodes.PortraitMissing] = Pictures,
        [PackValidationCodes.PortraitFileMissing] = Pictures,
        [PackValidationCodes.PortraitTooLarge] = Pictures,
        [PackValidationCodes.PortraitOutsidePack] = Pictures,

        [PackValidationCodes.VariantWithoutDisplayName] = Names,
        [PackValidationCodes.FolderNameUnusable] = Names,
        [PackValidationCodes.FolderNameShared] = Names,
        [PackValidationCodes.FolderIsOthers] = Names,
        [PackValidationCodes.NamesIndistinct] = Names,
        [PackDiagnosticCodes.MissingInternalName] = Names,
        [PackDiagnosticCodes.InvalidInternalName] = Names,
        [PackDiagnosticCodes.DuplicateInternalName] = Names,

        [PackDiagnosticCodes.DanglingBaseCharacter] = Outfits,
        [PackDiagnosticCodes.SelfReferencingFamily] = Outfits,
        [PackDiagnosticCodes.FamilyWithoutDefault] = Outfits,
        [PackDiagnosticCodes.FamilyWithMultipleDefaults] = Outfits,
        [PackValidationCodes.OutfitOfOutfit] = Outfits,

        [PackValidationCodes.AttributeIdInvalid] = Attributes,
        [PackValidationCodes.AttributeValueDuplicate] = Attributes,
        [PackValidationCodes.AttributeUndeclared] = Attributes,
        [PackValidationCodes.AttributeValueUndeclared] = Attributes,
        [PackValidationCodes.AttributeNotNumber] = Attributes,

        [PackValidationCodes.GameIdInvalid] = Game,
        [PackValidationCodes.GameWithoutName] = Game,
        [PackValidationCodes.PackVersionMissing] = Game,
        [PackDiagnosticCodes.GameIdMismatch] = Game,
    };

    /// <summary>The kind a problem belongs to.</summary>
    /// <param name="code">The problem's code, a validation or a diagnostic code.</param>
    /// <returns>One of <see cref="All"/>; <see cref="Other"/> for a code this list does not know.</returns>
    public static string Of(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return ByCode.GetValueOrDefault(code, Other);
    }

    /// <summary>Whether a string is one of the kinds, however it is capitalised.</summary>
    /// <returns>The kind as <see cref="All"/> spells it, or null.</returns>
    public static string? Find(string? kind) =>
        All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
}
