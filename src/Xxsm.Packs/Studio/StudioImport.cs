namespace Xxsm.Packs.Studio;

/// <summary>What applying an import did to a draft. The draft itself is untouched; this carries the new one.</summary>
/// <param name="Draft">The draft with the import applied, or the same draft when nothing changed.</param>
/// <param name="Created">The internal names of the characters created.</param>
/// <param name="Updated">The internal names of the characters that changed.</param>
/// <param name="Skipped">One sentence per chosen row that could not be applied, saying why.</param>
public sealed record StudioImportOutcome(
    PackDraft Draft,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Skipped)
{
    /// <summary>Whether the import changed something that is not a character, such as the ignore list.</summary>
    public bool OtherChanges { get; init; }

    /// <summary>Whether the draft changed at all.</summary>
    public bool Changed => Created.Count + Updated.Count > 0 || OtherChanges;
}

/// <summary>Stable codes for what the importers notice while reading a source.</summary>
public static class StudioImportCodes
{
    /// <summary>The source held nothing the importer could use.</summary>
    public const string NothingFound = "studio.import.nothing-found";

    /// <summary>A file inside the source could not be read.</summary>
    public const string UnreadableFile = "studio.import.unreadable-file";

    /// <summary>The source was larger than the importer will read.</summary>
    public const string LimitReached = "studio.import.limit";

    /// <summary>A file was found where it cannot be tied to a character.</summary>
    public const string NoCharacterFolder = "studio.import.no-character-folder";
}
