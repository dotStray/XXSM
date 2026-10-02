using System.Text.Json.Serialization;

namespace Xxsm.Packs.Model;

/// <summary><c>hashes.json</c>: flat and pre-joined, so the app never re-derives the upstream layout.</summary>
public sealed record HashIndexFile
{
    /// <summary>Hashes never to index, on top of the fan-out pruning. A null among them is dropped.</summary>
    [JsonPropertyName("ignoredHashes")]
    public IReadOnlyList<string>? IgnoredHashes { get; init => field = value is null ? null : [.. value.OfType<string>()]; }

    /// <summary>Every hash the pack knows, one entry per variant, kind and value. A null entry is dropped.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<PackHashEntry>? Entries { get; init => field = value is null ? null : [.. value.OfType<PackHashEntry>()]; }
}

/// <summary>One hash, attributed to one variant.</summary>
public sealed record PackHashEntry
{
    /// <summary>The <see cref="PackVariant.InternalName"/> this hash belongs to.</summary>
    [JsonPropertyName("variant")]
    public string Variant { get; init; } = string.Empty;

    /// <summary>The upstream component name: empty, or <c>Face</c>, <c>Hair</c>, <c>Body</c>. Not an id.</summary>
    [JsonPropertyName("component")]
    public string? Component { get; init; }

    /// <summary>Which buffer or shader field this hash came from.</summary>
    [JsonPropertyName("kind")]
    public HashKind Kind { get; init; }

    /// <summary>The hash, lowercase hex. 8 digits for buffers, 16 for shaders.</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = string.Empty;

    /// <summary>For a texture, the upstream texture kind, such as <c>Diffuse</c>. An open set.</summary>
    [JsonPropertyName("textureKind")]
    public string? TextureKind { get; init; }

    /// <summary>For <see cref="HashKind.Texture"/>, which object-index group it came from.</summary>
    [JsonPropertyName("slot")]
    public int? Slot { get; init; }
}
