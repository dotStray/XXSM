using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xxsm.Packs.Model;

/// <summary>The user's local edits, merged over a Game Pack at load. Unknown keys are kept on rewrite.</summary>
public sealed record PackOverlay
{
    private readonly int _overlaySchemaVersion = 1;

    /// <summary>Creates an empty overlay.</summary>
    /// <remarks>Explicit so the JSON reader does not pick the record's copy constructor.</remarks>
    [JsonConstructor]
    public PackOverlay()
    {
    }

    /// <summary>The overlay format version; a file without the key reads as 1.</summary>
    [JsonPropertyName("overlaySchemaVersion")]
    public int OverlaySchemaVersion
    {
        get => _overlaySchemaVersion;
        init => _overlaySchemaVersion = value == 0 ? 1 : value;
    }

    /// <summary>The game this overlay applies to.</summary>
    [JsonPropertyName("gameId")]
    public string GameId { get; init; } = string.Empty;

    /// <summary>The pack version it was authored against. Informational.</summary>
    [JsonPropertyName("basePackVersion")]
    public string? BasePackVersion { get; init; }

    /// <summary>Per-variant edits, keyed by internal name.</summary>
    [JsonPropertyName("variants")]
    public IReadOnlyDictionary<string, OverlayVariant>? Variants { get; init; }

    /// <summary>Hash additions, removals and per-variant replace modes.</summary>
    [JsonPropertyName("hashes")]
    public OverlayHashes? Hashes { get; init; }

    /// <summary>Additional hashes to prune from the identifying index.</summary>
    [JsonPropertyName("ignoredHashes")]
    public IReadOnlyList<string>? IgnoredHashes { get; init; }

    /// <summary>Pack versions whose adoption offer was dismissed, per custom variant; not offered again.</summary>
    [JsonPropertyName("declinedAdoptions")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? DeclinedAdoptions { get; init; }

    /// <summary>Every top-level key XXSM did not recognise, kept verbatim so a rewrite drops nothing.</summary>
    /// <remarks>Settable on purpose: extension data cannot bind to a constructor parameter.</remarks>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>One variant's overlay entry. A custom one needs only <see cref="Origin"/> and a display name.</summary>
public sealed record OverlayVariant
{
    /// <summary>Creates an empty entry.</summary>
    /// <remarks>Explicit so the JSON reader does not pick the record's copy constructor.</remarks>
    [JsonConstructor]
    public OverlayVariant()
    {
    }

    /// <summary><c>modified</c> for an edited pack variant, <c>custom</c> for a user-created one.</summary>
    [JsonPropertyName("origin")]
    public VariantOrigin? Origin { get; init; }

    /// <summary>Whether pack updates are blocked for this variant; true by default for modified and custom.</summary>
    [JsonPropertyName("locked")]
    public bool? Locked { get; init; }

    /// <summary>With <see cref="Locked"/> false, the only fields protected from pack updates.</summary>
    [JsonPropertyName("lockedFields")]
    public IReadOnlyList<string>? LockedFields { get; init; }

    /// <summary>Overrides the display name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>Overrides the base character link. An explicit null detaches a skin.</summary>
    [JsonPropertyName("baseCharacterId")]
    public string? BaseCharacterId { get; init; }

    /// <summary>Overrides whether this is the family's default.</summary>
    [JsonPropertyName("isDefaultVariant")]
    public bool? IsDefaultVariant { get; init; }

    /// <summary>Overrides the aliases the name matcher accepts.</summary>
    [JsonPropertyName("aliases")]
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>Overrides the filename-fallback prefix.</summary>
    [JsonPropertyName("modFilesName")]
    public string? ModFilesName { get; init; }

    /// <summary>Overrides the portrait. Often a <c>file://</c> URL to the user's own image.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>Overrides the release date.</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; init; }

    /// <summary>Overrides or adds attribute values.</summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, AttributeValue>? Attributes { get; init; }

    /// <summary>Hides the variant from the grid. Hiding deletes nothing on disk.</summary>
    [JsonPropertyName("hidden")]
    public bool? Hidden { get; init; }

    /// <summary>Free-text notes.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>When a custom variant was created.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Every key XXSM did not recognise, preserved verbatim on rewrite.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>The JSON property names this entry carried when read from a file; null when built in code.</summary>
    /// <remarks>Tells an absent key (keep the pack's value) from an explicit null (clear the field).</remarks>
    [JsonIgnore]
    public IReadOnlySet<string>? SpecifiedFields { get; init; }

    /// <summary>Whether the overlay sets a field, as opposed to leaving it to the pack.</summary>
    /// <param name="jsonPropertyName">The JSON name, for example <c>displayName</c>.</param>
    /// <param name="value">The deserialised value of that property.</param>
    public bool IsSpecified(string jsonPropertyName, object? value) =>
        SpecifiedFields is { } specified ? specified.Contains(jsonPropertyName) : value is not null;
}

/// <summary>Hash edits carried by the overlay.</summary>
public sealed record OverlayHashes
{
    /// <summary>The default merge mode. <c>merge</c> unions; <c>replace</c> discards the pack's.</summary>
    [JsonPropertyName("mode")]
    public HashMergeMode? Mode { get; init; }

    /// <summary>Per-variant overrides of <see cref="Mode"/>; <c>replace</c> drops the pack's hashes for it.</summary>
    [JsonPropertyName("variantModes")]
    public IReadOnlyDictionary<string, HashMergeMode>? VariantModes { get; init; }

    /// <summary>The variant's own mode in <see cref="VariantModes"/>, else <see cref="Mode"/>, else merge.</summary>
    /// <param name="internalName">The variant, matched ignoring case.</param>
    public HashMergeMode ModeFor(string internalName)
    {
        foreach (var (key, value) in VariantModes ?? new Dictionary<string, HashMergeMode>())
        {
            if (string.Equals(key, internalName, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return Mode ?? HashMergeMode.Merge;
    }

    /// <summary>Hashes to union in.</summary>
    [JsonPropertyName("add")]
    public IReadOnlyList<PackHashEntry>? Add { get; init; }

    /// <summary>Hashes to subtract, applied after <see cref="Add"/>.</summary>
    [JsonPropertyName("remove")]
    public IReadOnlyList<PackHashEntry>? Remove { get; init; }
}

/// <summary>Where a variant's data came from, and therefore whether updates may touch it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VariantOrigin>))]
public enum VariantOrigin
{
    /// <summary>Untouched pack data. Never written to the overlay.</summary>
    [JsonStringEnumMemberName("pack")]
    Pack = 0,

    /// <summary>A pack variant the user has edited. Locked by default.</summary>
    [JsonStringEnumMemberName("modified")]
    Modified,

    /// <summary>Created by the user; no pack counterpart. Locked by definition.</summary>
    [JsonStringEnumMemberName("custom")]
    Custom,
}

/// <summary>How an overlay's hashes combine with the pack's.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HashMergeMode>))]
public enum HashMergeMode
{
    /// <summary>Union the overlay's hashes with the pack's. The default.</summary>
    [JsonStringEnumMemberName("merge")]
    Merge = 0,

    /// <summary>Use only the overlay's hashes; exclude the pack's for this variant.</summary>
    [JsonStringEnumMemberName("replace")]
    Replace,
}
