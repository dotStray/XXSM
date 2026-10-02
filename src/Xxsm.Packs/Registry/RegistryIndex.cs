using System.Text.Json.Serialization;

namespace Xxsm.Packs.Registry;

/// <summary>A registry's <c>index.json</c>: the Game Packs it offers and every version of each.</summary>
public sealed record RegistryIndex
{
    private readonly IReadOnlyList<RegistryPack> _packs = [];

    /// <summary>The registry format version. Unrelated to a pack's schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>When the registry was last generated.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The packs on offer, one entry per game.</summary>
    [JsonPropertyName("packs")]
    public IReadOnlyList<RegistryPack> Packs
    {
        get => _packs;

        // A null entry is dropped, so one broken line does not stop the rest being listed.
        init => _packs = value is null ? [] : [.. value.Where(pack => pack is not null)];
    }
}

/// <summary>One game's entry in a registry.</summary>
public sealed record RegistryPack
{
    private readonly IReadOnlyList<RegistryPackVersion> _versions = [];

    /// <summary>The game this pack describes. The key everything else joins on.</summary>
    [JsonPropertyName("gameId")]
    public required string GameId { get; init; }

    /// <summary>The name to show. Falls back to the game id when absent.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>A short label such as <c>GI</c>, for narrow columns.</summary>
    [JsonPropertyName("shortName")]
    public string? ShortName { get; init; }

    /// <summary>The 3DMigoto importer this game uses, such as <c>GIMI</c>.</summary>
    [JsonPropertyName("importer")]
    public string? Importer { get; init; }

    /// <summary>A path, relative to the index, of an icon for the game.</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    /// <summary>Every published version, in whatever order the registry lists them.</summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<RegistryPackVersion> Versions
    {
        get => _versions;

        // A version with no checksum is kept, so installing it can say why it is refused.
        init => _versions = value is null
            ? []
            : [.. value.Where(version => version is { PackVersion.Length: > 0, Url.Length: > 0 })];
    }
}

/// <summary>One downloadable version of a pack.</summary>
public sealed record RegistryPackVersion
{
    /// <summary>The pack's own version, such as <c>2026.09.01</c>.</summary>
    [JsonPropertyName("packVersion")]
    public required string PackVersion { get; init; }

    /// <summary>The pack format this version is written in, checked before anything is downloaded.</summary>
    [JsonPropertyName("packSchemaVersion")]
    public int PackSchemaVersion { get; init; } = 1;

    /// <summary>The oldest XXSM that can use it, or null when any version can.</summary>
    [JsonPropertyName("minAppVersion")]
    public string? MinAppVersion { get; init; }

    /// <summary>Where to download it: absolute, or relative to the index's own location.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>The expected SHA-256 of the archive, as hex. Verified before install.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    /// <summary>The archive's size, for the progress display. Advisory only.</summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    /// <summary>What changed, to show beside the version.</summary>
    [JsonPropertyName("changelog")]
    public string? Changelog { get; init; }
}
