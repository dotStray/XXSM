using System.Text.Json;
using System.Text.Json.Serialization;
using Xxsm.Core.GameBanana;

namespace Xxsm.Core.Mods;

/// <summary>The metadata XXSM keeps about one mod, in <c>.xxsm/mod.json</c> inside the mod's folder.</summary>
/// <remarks>A superset of JASM's <c>.JASM_ModConfig.json</c>. Unknown keys are kept verbatim on rewrite.</remarks>
public sealed record ModConfig
{
    /// <summary>Creates an empty configuration.</summary>
    /// <remarks>Explicit, so the JSON reader does not choose the record's copy constructor.</remarks>
    [JsonConstructor]
    public ModConfig()
    {
    }

    /// <summary>The format version of this file.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = ModConfigSchema.CurrentVersion;

    /// <summary>A stable identifier, generated once and never changed; profiles record mods by it.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The name the user gave the mod, shown instead of the folder name.</summary>
    [JsonPropertyName("customName")]
    public string? CustomName { get; init; }

    /// <summary>Who made the mod.</summary>
    [JsonPropertyName("author")]
    public string? Author { get; init; }

    /// <summary>The mod author's own version string. Free text; never parsed.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>A description, usually carried over from where the mod was downloaded.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Where the mod came from, for update checks and for crediting the author.</summary>
    [JsonPropertyName("modUrl")]
    public string? ModUrl { get; init; }

    /// <summary>The preview image, as a path relative to the mod folder.</summary>
    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; init; }

    /// <summary>Whether the user asked for no picture at all, so the folder is not searched for one.</summary>
    [JsonPropertyName("noImage")]
    public bool NoImage { get; init; }

    /// <summary>When XXSM first saw the mod.</summary>
    [JsonPropertyName("dateAdded")]
    public DateTimeOffset? DateAdded { get; init; }

    /// <summary>The character a person filed this mod under. Auto-sort never overrides it.</summary>
    [JsonPropertyName("variantOverride")]
    public string? VariantOverride { get; init; }

    /// <summary>The mod's GameBanana page, when linked; see <see cref="WithModUrl"/>.</summary>
    [JsonPropertyName("gameBanana")]
    public ModGameBananaInfo? GameBanana { get; init; }

    /// <summary>Whether the mod is linked to a GameBanana page, so its updates can be checked.</summary>
    [JsonIgnore]
    public bool IsLinkedToGameBanana => GameBanana?.ModId is > 0;

    /// <summary>Sets the mod's address, and links or unlinks its GameBanana page to match.</summary>
    /// <param name="url">The address, or null or blank for none.</param>
    /// <returns>The configuration with the address, and the link that goes with it.</returns>
    /// <remarks>
    /// A GameBanana mod address links that page with no request; the same page keeps what is recorded, a different
    /// one starts afresh. Any other address, or none, unlinks it.
    /// </remarks>
    public ModConfig WithModUrl(string? url)
    {
        var address = url?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

        if (GameBananaUrl.FindModId(address) is not { } modId)
        {
            return this with { ModUrl = address, GameBanana = null };
        }

        return this with
        {
            ModUrl = address,
            GameBanana = GameBanana?.ModId == modId ? GameBanana : new ModGameBananaInfo { ModId = modId },
        };
    }

    /// <summary>Why the last auto-sort put this mod where it is.</summary>
    [JsonPropertyName("sortResult")]
    public ModSortResult? SortResult { get; init; }

    /// <summary>User-assigned tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Free text the user wrote about this mod.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>Every key XXSM did not recognise, kept verbatim so a rewrite never drops data.</summary>
    /// <remarks>Settable, not init-only: extension data cannot bind to a generated constructor parameter.</remarks>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>Where a mod came from on GameBanana, and what was last known about it there.</summary>
public sealed record ModGameBananaInfo
{
    /// <summary>The GameBanana mod page id.</summary>
    [JsonPropertyName("modId")]
    public long? ModId { get; init; }

    /// <summary>The specific file that was downloaded.</summary>
    [JsonPropertyName("fileId")]
    public long? FileId { get; init; }

    /// <summary>The downloaded file's name.</summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    /// <summary>Upstream's last-modified timestamp, as the Unix seconds GameBanana reports.</summary>
    [JsonPropertyName("dateModifiedTs")]
    public long? DateModifiedTs { get; init; }

    /// <summary>When XXSM last asked GameBanana about this mod.</summary>
    [JsonPropertyName("lastChecked")]
    public DateTimeOffset? LastChecked { get; init; }

    /// <summary>Whether the last check found a newer file.</summary>
    [JsonPropertyName("updateAvailable")]
    public bool? UpdateAvailable { get; init; }

    /// <summary>The page's version when a check last found it newer, or null; shown after a restart.</summary>
    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; init; }
}

/// <summary>The audit trail from the last auto-sort of this mod.</summary>
public sealed record ModSortResult
{
    /// <summary>Which tier of the fallback chain decided, lowercase.</summary>
    [JsonPropertyName("decidedBy")]
    public string DecidedBy { get; init; } = string.Empty;

    /// <summary>How sure the sorter was, 0 to 1.</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    /// <summary>The variant it chose.</summary>
    [JsonPropertyName("matchedVariant")]
    public string? MatchedVariant { get; init; }

    /// <summary>The best variant it rejected.</summary>
    [JsonPropertyName("runnerUp")]
    public string? RunnerUp { get; init; }

    /// <summary>Every hash found in the mod.</summary>
    [JsonPropertyName("extractedHashes")]
    public IReadOnlyList<string>? ExtractedHashes { get; init; }

    /// <summary>When the sort ran.</summary>
    [JsonPropertyName("sortedAt")]
    public DateTimeOffset? SortedAt { get; init; }

    /// <summary>Whether the outfit was a guess: the hashes settled the family but not which member.</summary>
    [JsonPropertyName("variantIsUncertain")]
    public bool? VariantIsUncertain { get; init; }
}

/// <summary>Version rules for <c>.xxsm/mod.json</c>.</summary>
public static class ModConfigSchema
{
    /// <summary>The version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The mod folder subdirectory XXSM stores its own files in.</summary>
    public const string DirectoryName = ".xxsm";

    /// <summary>The metadata file's name inside <see cref="DirectoryName"/>.</summary>
    public const string FileName = "mod.json";

    /// <summary>Whether this build can read a file of the given version; 0, no version, is JASM's.</summary>
    /// <param name="version">The <c>schemaVersion</c> read from the file.</param>
    /// <returns><c>true</c> when the file can be read and rewritten without losing data.</returns>
    public static bool IsSupported(int version) => version <= CurrentVersion;
}
