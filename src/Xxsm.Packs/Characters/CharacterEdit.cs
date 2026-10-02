using Xxsm.Packs.Model;

namespace Xxsm.Packs.Characters;

/// <summary>A character to create in the user's overlay. Only <see cref="DisplayName"/> is required.</summary>
public sealed record NewCharacter
{
    /// <summary>The name the user typed. The only required field.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The internal name to use, or null to derive one; adjusted if something already has it.</summary>
    public string? InternalName { get; init; }

    /// <summary>The character this is an alternate outfit of, or null for a base character.</summary>
    public string? BaseCharacterId { get; init; }

    /// <summary>Whether this is its family's fallback; null means true for a base, false for a skin.</summary>
    public bool? IsDefaultVariant { get; init; }

    /// <summary>Extra strings the name-fallback matcher should accept.</summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>The file-name prefix and the folder its mods are filed under; null uses the internal name.</summary>
    public string? ModFilesName { get; init; }

    /// <summary>A portrait, normally a <c>file://</c> URL to an image the user chose.</summary>
    public string? Image { get; init; }

    /// <summary>Attribute values keyed by attribute id.</summary>
    public IReadOnlyDictionary<string, AttributeValue>? Attributes { get; init; }

    /// <summary>Free-text notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Whether the character starts out hidden from the grid; the sorter still files under it.</summary>
    public bool Hidden { get; init; }

    /// <summary>Hashes to record against the new character; empty is normal.</summary>
    public IReadOnlyList<PackHashEntry>? Hashes { get; init; }
}

/// <summary>An edit to an existing variant, pack or custom. Any change to a pack variant locks it.</summary>
public sealed record CharacterEdit
{
    /// <summary>The name shown in the grid.</summary>
    public EditField<string> DisplayName { get; init; }

    /// <summary>The character this is an outfit of; cleared, the skin becomes a base character.</summary>
    public EditField<string> BaseCharacterId { get; init; }

    /// <summary>Whether this is its family's fallback.</summary>
    public EditField<bool> IsDefaultVariant { get; init; }

    /// <summary>The strings the name-fallback matcher accepts.</summary>
    public EditField<IReadOnlyList<string>> Aliases { get; init; }

    /// <summary>The filename-fallback prefix and Mods sub-folder name.</summary>
    public EditField<string> ModFilesName { get; init; }

    /// <summary>The portrait. Cleared falls back to the initials tile.</summary>
    public EditField<string> Image { get; init; }

    /// <summary>Attribute values keyed by attribute id.</summary>
    public EditField<IReadOnlyDictionary<string, AttributeValue>> Attributes { get; init; }

    /// <summary>Whether the variant is hidden from the grid. Hiding deletes nothing.</summary>
    public EditField<bool> Hidden { get; init; }

    /// <summary>Free-text notes.</summary>
    public EditField<string> Notes { get; init; }

    /// <summary>Whether this edit changes anything at all.</summary>
    public bool IsEmpty =>
        !DisplayName.IsSet
        && !BaseCharacterId.IsSet
        && !IsDefaultVariant.IsSet
        && !Aliases.IsSet
        && !ModFilesName.IsSet
        && !Image.IsSet
        && !Attributes.IsSet
        && !Hidden.IsSet
        && !Notes.IsSet;
}
