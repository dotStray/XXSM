using Xxsm.Packs.Merge;

namespace Xxsm.Packs.Importing;

/// <summary>Which mod manager wrote the details being brought in.</summary>
public enum ModImportSource
{
    /// <summary>JASM, whose <c>.JASM_ModConfig.json</c> sits in each mod folder.</summary>
    Jasm,

    /// <summary>XX-Mod-Manager, whose <c>mod.json</c> sits in each mod folder.</summary>
    XxModManager,
}

/// <summary>What another mod manager knew about one mod, in XXSM's terms; null or empty for nothing.</summary>
public sealed record ImportedDetails
{
    /// <summary>The other manager's id for the mod.</summary>
    public string? SourceId { get; init; }

    /// <summary>The name the user gave the mod there.</summary>
    public string? Name { get; init; }

    /// <summary>Who made it.</summary>
    public string? Author { get; init; }

    /// <summary>The author's version string.</summary>
    public string? Version { get; init; }

    /// <summary>Its description.</summary>
    public string? Description { get; init; }

    /// <summary>Where it came from.</summary>
    public string? Url { get; init; }

    /// <summary>Its picture, relative to the mod folder, checked to exist.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Its tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Notes to carry into XXSM's notes: XX-Mod-Manager's hotkey list, written out as lines.</summary>
    public string? Notes { get; init; }

    /// <summary>When it was added there.</summary>
    public DateTimeOffset? DateAdded { get; init; }

    /// <summary>The character the other manager filed it under, as written there.</summary>
    public string? Character { get; init; }
}

/// <summary>One mod whose details could be brought in, and what that would change.</summary>
public sealed record ModImportRow
{
    /// <summary>The mod folder.</summary>
    public required string ModFolder { get; init; }

    /// <summary>The mod's name on disk, without any disabled prefix.</summary>
    public required string ModName { get; init; }

    /// <summary>Which manager's file this is.</summary>
    public required ModImportSource Source { get; init; }

    /// <summary>The file the details came from.</summary>
    public required string SourceFile { get; init; }

    /// <summary>What that file says, cleaned of placeholders.</summary>
    public required ImportedDetails Details { get; init; }

    /// <summary>Which of XXSM's fields would be filled, by name. Only a field XXSM has nothing in is filled.</summary>
    public required IReadOnlyList<string> Fills { get; init; }

    /// <summary>The character the tag names, or <c>null</c> when it names none XXSM knows.</summary>
    public MergedVariant? Character { get; init; }

    /// <summary>Whether <see cref="Character"/> was the user's choice for a tag XXSM did not know.</summary>
    public bool CharacterChosen { get; init; }

    /// <summary>Whether the user chose <c>Others</c> for this mod's tag; it is then filed and kept there.</summary>
    public bool ToOthers { get; init; }

    /// <summary>The character the mod's hashes identify when it differs from the tag's; otherwise null.</summary>
    public MergedVariant? HashCharacter { get; init; }

    /// <summary>Whether to file the mod under <see cref="Character"/>; false lets the hashes decide.</summary>
    public bool FollowTag { get; init; } = true;

    /// <summary>Why this mod cannot be brought in, or <c>null</c>.</summary>
    public string? Problem { get; init; }

    /// <summary>Whether the tag and the hashes disagree about the character.</summary>
    public bool Disagrees => Character is not null && HashCharacter is not null;

    /// <summary>Whether bringing it in would change anything.</summary>
    public bool ChangesAnything => Problem is null && Fills.Count > 0;
}

/// <summary>A character tag that names no character XXSM knows.</summary>
/// <param name="Tag">The tag as written.</param>
/// <param name="ModCount">How many mods carry it.</param>
public sealed record UnmatchedCharacterTag(string Tag, int ModCount);

/// <summary>What an import would do.</summary>
public sealed record ModImportPlan
{
    /// <summary>The Mods folder.</summary>
    public required string ModsDirectory { get; init; }

    /// <summary>One row per mod with another manager's details, in path order.</summary>
    public required IReadOnlyList<ModImportRow> Rows { get; init; }

    /// <summary>Character tags that match no character, most-used first.</summary>
    public required IReadOnlyList<UnmatchedCharacterTag> UnmatchedTags { get; init; }

    /// <summary>The rows that would change something.</summary>
    public IReadOnlyList<ModImportRow> Changes => [.. Rows.Where(row => row.ChangesAnything)];

    /// <summary>The rows whose tag and hashes disagree.</summary>
    public IReadOnlyList<ModImportRow> Disagreements => [.. Rows.Where(row => row.Disagrees)];

    /// <summary>How many mods came from each manager.</summary>
    public int CountFrom(ModImportSource source) => Rows.Count(row => row.Source == source);
}

/// <summary>What an import did.</summary>
/// <param name="Written">Mods whose details were written.</param>
/// <param name="Unchanged">Mods that had nothing new to bring in.</param>
/// <param name="Failures">Mods that could not be written, each with why.</param>
public sealed record ModImportResult(
    IReadOnlyList<string> Written,
    int Unchanged,
    IReadOnlyList<(string ModFolder, string Error)> Failures);
