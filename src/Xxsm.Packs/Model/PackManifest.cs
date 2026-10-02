using System.Text.Json.Serialization;

namespace Xxsm.Packs.Model;

/// <summary><c>manifest.json</c>: what the pack is, where its data came from, which clients can read it.</summary>
public sealed record PackManifest
{
    /// <summary>The pack format version; one outside <see cref="PackSchema.SupportedVersions"/> is refused.</summary>
    [JsonPropertyName("packSchemaVersion")]
    public int PackSchemaVersion { get; init; }

    /// <summary>The game this pack describes, for example <c>genshin</c>.</summary>
    [JsonPropertyName("gameId")]
    public string GameId { get; init; } = string.Empty;

    /// <summary>The pack's own version. Date-based by convention, so it sorts lexically.</summary>
    [JsonPropertyName("packVersion")]
    public string PackVersion { get; init; } = string.Empty;

    /// <summary>When the pack was generated.</summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset? GeneratedAt { get; init; }

    /// <summary>What produced it — the CI builder, or Pack Studio.</summary>
    [JsonPropertyName("builder")]
    public string? Builder { get; init; }

    /// <summary><c>official</c> or <c>user</c>. Informational: it never gates loading, updating or sorting.</summary>
    [JsonPropertyName("authoredBy")]
    public string? AuthoredBy { get; init; }

    /// <summary>The oldest XXSM version that can use this pack.</summary>
    [JsonPropertyName("minAppVersion")]
    public string? MinAppVersion { get; init; }

    /// <summary>Advertised counts. Advisory — the loader trusts the files, not these.</summary>
    [JsonPropertyName("counts")]
    public IReadOnlyDictionary<string, int>? Counts { get; init; }

    /// <summary>Where the data came from, for attribution and for diagnosing staleness.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<PackSource>? Sources { get; init; }
}

/// <summary>One upstream source a pack was built from.</summary>
public sealed record PackSource
{
    /// <summary>What this source supplied, for example <c>hashes</c> or <c>images</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The source's URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>The exact commit used, when the source is a repository.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; init; }

    /// <summary>The source's licence.</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }
}
