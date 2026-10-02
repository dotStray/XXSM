using System.Text.Json.Serialization;

namespace Xxsm.Packs.Model;

/// <summary><c>game.json</c>: the game's identity and the attribute schema the filter chips are built from.</summary>
public sealed record GameDefinition
{
    /// <summary>The game id, matching the manifest's.</summary>
    [JsonPropertyName("gameId")]
    public string GameId { get; init; } = string.Empty;

    /// <summary>The name shown to users.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>A short form for compact UI, for example <c>GI</c>.</summary>
    [JsonPropertyName("shortName")]
    public string? ShortName { get; init; }

    /// <summary>The XXMI subfolder name, for example <c>GIMI</c>. Informational; XXMI is never required.</summary>
    [JsonPropertyName("importer")]
    public string? Importer { get; init; }

    /// <summary>Pack-relative path or absolute URL to the game's icon.</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    /// <summary>A disabled mod folder's prefix, <c>DISABLED_</c> by default; <c>DISABLED</c> is also read.</summary>
    [JsonPropertyName("disabledPrefix")]
    public string? DisabledPrefix { get; init; }

    /// <summary>The attribute schema, keyed by attribute id.</summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, AttributeDefinition>? Attributes { get; init; }
}

/// <summary>One declared attribute — its label and, for enumerated ones, its values.</summary>
public sealed record AttributeDefinition
{
    /// <summary>The label shown on the filter chip.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary><c>number</c> for numeric attributes such as rarity; absent for enumerated ones.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The allowed values, for an enumerated attribute.</summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<AttributeValueDefinition>? Values { get; init; }

    /// <summary>Whether this attribute holds a number rather than an enumerated id.</summary>
    [JsonIgnore]
    public bool IsNumeric => string.Equals(Kind, "number", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One allowed value of an enumerated attribute.</summary>
public sealed record AttributeValueDefinition
{
    /// <summary>The stable id stored on variants.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The label shown to users.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>Pack-relative path or absolute URL to an icon.</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; init; }
}
