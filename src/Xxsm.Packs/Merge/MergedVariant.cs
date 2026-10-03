using Xxsm.Packs.Model;

namespace Xxsm.Packs.Merge;

/// <summary>One moddable identity: the pack's data with the overlay applied and every field derived.</summary>
public sealed record MergedVariant
{
    /// <summary>The stable id. Also the folder name under the Mods directory.</summary>
    public required string InternalName { get; init; }

    /// <summary>The name shown to users. Never empty — it falls back to the internal name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The base character this is an outfit of, or null when it is itself a base.</summary>
    public string? BaseCharacterId { get; init; }

    /// <summary>The family: its base character's id, or its own when it is a base.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Whether this is the family's fallback when an outfit cannot be told apart.</summary>
    public required bool IsDefaultVariant { get; init; }

    /// <summary>Extra strings the name-fallback matcher accepts. Never null.</summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    /// <summary>The character's folder in the Mods folder and the file-name prefix; valid as a folder name.</summary>
    public required string ModFilesName { get; init; }

    /// <summary>The portrait: an absolute path, a <c>file://</c> or <c>https://</c> URL, or null.</summary>
    public string? Image { get; init; }

    /// <summary>The in-game release date, where it is known.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Attribute values keyed by attribute id. Never null; empty is normal.</summary>
    public required IReadOnlyDictionary<string, AttributeValue> Attributes { get; init; }

    /// <summary>Whether the user has hidden this variant from the grid.</summary>
    public required bool Hidden { get; init; }

    /// <summary>Free-text notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Where this variant's data came from.</summary>
    public required VariantOrigin Origin { get; init; }

    /// <summary>Whether pack updates are blocked for this variant; true by default once edited or created.</summary>
    public required bool IsLocked { get; init; }

    /// <summary>With <see cref="IsLocked"/> false, the fields still protected from pack updates. Never null.</summary>
    public required IReadOnlyList<string> LockedFields { get; init; }

    /// <summary>This variant's hashes, after the overlay's additions, removals and mode.</summary>
    public required IReadOnlyList<PackHashEntry> Hashes { get; init; }

    /// <summary>When this variant was created, for a user-created one.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Whether this variant has any hashes at all.</summary>
    public bool HasHashes => Hashes.Count > 0;

    /// <summary>Whether the variant has no hashes. The pack's advisory flag does not count.</summary>
    public bool IsHashesPending => Hashes.Count == 0;

    /// <summary>Whether this variant is an alternate outfit of another.</summary>
    public bool IsSkin => BaseCharacterId is { Length: > 0 };

    /// <summary>Whether the user has edited or created this variant, for the Custom badge.</summary>
    public bool IsCustomised => Origin != VariantOrigin.Pack;
}
