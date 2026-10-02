using System.Text.Json.Serialization;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Sorting;

/// <summary>Which tier of the sorter's fallback chain produced an answer; the <c>decidedBy</c> values.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SortDecidedBy>))]
public enum SortDecidedBy
{
    /// <summary>Tier 0 — a human already filed this mod. Auto-sort never overrides one.</summary>
    Manual,

    /// <summary>Tier 1 — hash scoring.</summary>
    Hash,

    /// <summary>Tier 2 — an asset filename starting with a variant's <c>modFilesName</c>.</summary>
    Filename,

    /// <summary>Tier 3 — a normalised name match.</summary>
    Name,

    /// <summary>Tier 4 — nothing identified it. The mod belongs in <c>Others/</c>.</summary>
    Unsorted,
}

/// <summary>One variant the sorter considered, with the evidence for it.</summary>
/// <param name="VariantId">The variant's internal name.</param>
/// <param name="FamilyId">The family it belongs to.</param>
/// <param name="Score">Its own weighted hash score.</param>
/// <param name="FamilyScore">The summed score of every member of its family.</param>
/// <param name="MatchedHashes">The hashes of the mod that this variant claims, sorted.</param>
/// <param name="FamilyUniqueMatches">How many of those exactly one member of its family claims.</param>
public sealed record SortCandidate(
    string VariantId,
    string FamilyId,
    int Score,
    int FamilyScore,
    IReadOnlyList<string> MatchedHashes,
    int FamilyUniqueMatches);

/// <summary>What the sorter decided about one mod folder, and why. Touches nothing on disk.</summary>
public sealed record SortDecision
{
    /// <summary>The mod folder this is about.</summary>
    public required string Root { get; init; }

    /// <summary>The variant the mod belongs to, or null when it is unresolved and belongs in <c>Others/</c>.</summary>
    public string? VariantId { get; init; }

    /// <summary>The winning variant's family, or null when unresolved.</summary>
    public string? FamilyId { get; init; }

    /// <summary>Which tier decided.</summary>
    public required SortDecidedBy DecidedBy { get; init; }

    /// <summary>How sure the sorter is, from 0.5 to 1.0, or 0 when nothing was resolved.</summary>
    /// <remarks>score / (score + runner-up); capped when the outfit is a fallback or chosen by name.</remarks>
    public required double Confidence { get; init; }

    /// <summary>Whether two or more families tied; the row goes to <c>Others</c> unless the user picks one.</summary>
    public required bool IsAmbiguous { get; init; }

    /// <summary>Whether the family resolved to its default member for want of a family-unique hash.</summary>
    public required bool IsDefaultVariantFallback { get; init; }

    /// <summary>Whether the mod's own name picked the outfit after the hashes settled only the family.</summary>
    public required bool MemberDecidedByName { get; init; }

    /// <summary>Whether the matched variant has no hashes, so it was found by name or file name.</summary>
    public required bool MatchedVariantHasNoHashes { get; init; }

    /// <summary>The runner-up variant, for the audit trail. Null when there was none.</summary>
    public string? RunnerUpVariantId { get; init; }

    /// <summary>The variants considered, best first, for the "did you mean…?" choices.</summary>
    public required IReadOnlyList<SortCandidate> Candidates { get; init; }

    /// <summary>Every hash found in the mod, sorted. Recorded even when nothing matched.</summary>
    public required IReadOnlyList<string> ExtractedHashes { get; init; }

    /// <summary>Extracted hashes the pack has but pruned as non-identifying, with the reason.</summary>
    public required IReadOnlyList<PrunedHash> PrunedMatches { get; init; }

    /// <summary>A short human-readable account of the decision, for the CLI and the log.</summary>
    public required string Explanation { get; init; }

    /// <summary>Whether the mod was filed against a variant.</summary>
    public bool IsResolved => VariantId is { Length: > 0 };
}
