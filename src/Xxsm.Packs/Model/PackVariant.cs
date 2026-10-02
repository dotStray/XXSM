using System.Text.Json.Serialization;

namespace Xxsm.Packs.Model;

/// <summary>One entry in <c>variants.json</c>: a single moddable identity, a base or an outfit.</summary>
public sealed record PackVariant
{
    /// <summary>The stable id, the upstream folder name: <c>[A-Za-z0-9_-]+</c>, compared ignoring case.</summary>
    [JsonPropertyName("internalName")]
    public string InternalName { get; init; } = string.Empty;

    /// <summary>The name shown to users.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The base character this is an outfit of, or null when this is itself a base.</summary>
    [JsonPropertyName("baseCharacterId")]
    public string? BaseCharacterId { get; init; }

    /// <summary>Whether this is the family's fallback when a skin cannot be resolved; one per family.</summary>
    [JsonPropertyName("isDefaultVariant")]
    public bool? IsDefaultVariant { get; init; }

    /// <summary>Extra names the name-fallback matcher accepts for this variant. A null among them is dropped.</summary>
    [JsonPropertyName("aliases")]
    public IReadOnlyList<string>? Aliases { get; init => field = value is null ? null : [.. value.OfType<string>()]; }

    /// <summary>The file-name prefix of this variant's <c>.ib</c>, <c>.buf</c> and <c>.dds</c> files.</summary>
    [JsonPropertyName("modFilesName")]
    public string? ModFilesName { get; init; }

    /// <summary>Pack-relative path or absolute URL to the portrait. Missing is not an error.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>The in-game release date, where the pack knows it.</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; init; }

    /// <summary>Attribute values, keyed by the attribute ids declared in <c>game.json</c>.</summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, AttributeValue>? Attributes { get; init; }

    /// <summary>Whether the variant is hidden from the grid.</summary>
    [JsonPropertyName("hidden")]
    public bool? Hidden { get; init; }

    /// <summary>Free-text notes carried by the pack.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>Advisory: the roster knows this variant but no hashes are published for it yet.</summary>
    [JsonPropertyName("hashesPending")]
    public bool? HashesPending { get; init; }
}
