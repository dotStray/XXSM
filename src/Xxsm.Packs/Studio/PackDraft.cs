using System.Text.Json;
using System.Text.Json.Serialization;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>A Game Pack being authored in Pack Studio: its files, every row kept, plus Studio's notes.</summary>
/// <remarks>Immutable, so an undo step is the previous instance.</remarks>
public sealed record PackDraft
{
    /// <summary>What will become <c>manifest.json</c>.</summary>
    public required PackManifest Manifest { get; init; }

    /// <summary>What will become <c>game.json</c>, including the attribute schema.</summary>
    public required GameDefinition Game { get; init; }

    /// <summary>Every entry in one flat list, as <c>variants.json</c> holds it.</summary>
    public required IReadOnlyList<PackVariant> Variants { get; init; }

    /// <summary>What will become <c>hashes.json</c>. Never null; empty is normal.</summary>
    public required HashIndexFile Hashes { get; init; }

    /// <summary>Studio's own notes about the draft: where it came from and what was imported.</summary>
    public required StudioDraftInfo Info { get; init; }

    /// <summary>The game this draft describes.</summary>
    public string GameId => Manifest.GameId;
}

/// <summary><c>studio.json</c>: what Pack Studio remembers about a draft beyond the pack. Never exported.</summary>
public sealed record StudioDraftInfo
{
    /// <summary>Creates an empty record.</summary>
    /// <remarks>Explicit so the JSON reader does not pick the record's copy constructor.</remarks>
    [JsonConstructor]
    public StudioDraftInfo()
    {
    }

    /// <summary>The format version of this file.</summary>
    [JsonPropertyName("studioSchemaVersion")]
    public int StudioSchemaVersion { get; init; } = 1;

    /// <summary>When the draft was started.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The Mods folder given in the new-game wizard, offered as the folder for <em>Try it</em>.</summary>
    [JsonPropertyName("modsDirectory")]
    public string? ModsDirectory { get; init; }

    /// <summary>The installed pack this draft was opened from, when it was.</summary>
    [JsonPropertyName("openedFrom")]
    public StudioPackOrigin? OpenedFrom { get; init; }

    /// <summary>The version this draft was last exported as, so the next export can propose a newer one.</summary>
    [JsonPropertyName("lastExportedVersion")]
    public string? LastExportedVersion { get; init; }

    /// <summary>Every import applied to this draft, oldest first, so a repeat import shows what changed.</summary>
    [JsonPropertyName("imports")]
    public IReadOnlyList<StudioImportRecord>? Imports { get; init; }

    /// <summary>Keys this version of XXSM did not recognise, kept on rewrite.</summary>
    /// <remarks>Settable on purpose: extension data cannot bind to a constructor parameter.</remarks>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>The installed pack a draft was opened from.</summary>
/// <param name="GameId">The game.</param>
/// <param name="PackVersion">The installed version that was opened.</param>
public sealed record StudioPackOrigin(
    [property: JsonPropertyName("gameId")] string GameId,
    [property: JsonPropertyName("packVersion")] string PackVersion);

/// <summary>One import applied to a draft.</summary>
public sealed record StudioImportRecord
{
    /// <summary>Which importer ran — see <see cref="StudioImportKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The folder, archive or file it read.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>When it was applied.</summary>
    [JsonPropertyName("importedAt")]
    public DateTimeOffset? ImportedAt { get; init; }

    /// <summary>The internal names it created or changed.</summary>
    [JsonPropertyName("variants")]
    public IReadOnlyList<string>? Variants { get; init; }
}

/// <summary>The names <see cref="StudioImportRecord.Kind"/> takes.</summary>
public static class StudioImportKinds
{
    /// <summary>A model-importer assets repository: folders of <c>hash.json</c>.</summary>
    public const string HashAssets = "hashAssets";

    /// <summary>A populated Mods folder.</summary>
    public const string ModsFolder = "modsFolder";

    /// <summary>A folder of portraits.</summary>
    public const string Images = "images";

    /// <summary>A CSV or TSV spreadsheet.</summary>
    public const string Spreadsheet = "spreadsheet";

    /// <summary>A user's overlay, promoted into the draft.</summary>
    public const string Overlay = "overlay";
}

/// <summary>One draft, as the Studio page lists it.</summary>
/// <param name="GameId">The game, which is also the draft's folder name.</param>
/// <param name="DisplayName">The game's name, or its folder name when the draft cannot be read.</param>
/// <param name="PackVersion">The version the draft will export as, or empty when it cannot be read.</param>
/// <param name="Directory">Where the draft lives.</param>
/// <param name="VariantCount">How many variants it holds.</param>
/// <param name="LastSavedAt">When any of its files last changed.</param>
/// <param name="Error">Why the draft could not be read, or null; a broken draft is listed, not hidden.</param>
/// <param name="Icon">The game's icon, as a draft-relative path, or null.</param>
public sealed record StudioDraftSummary(
    string GameId,
    string DisplayName,
    string PackVersion,
    string Directory,
    int VariantCount,
    DateTimeOffset? LastSavedAt,
    string? Error,
    string? Icon = null);

/// <summary>What a save wrote.</summary>
/// <param name="Directory">The draft's folder.</param>
/// <param name="FilesWritten">The files that changed; empty when nothing did, and nothing was touched.</param>
public sealed record StudioSaveResult(string Directory, IReadOnlyList<string> FilesWritten);
