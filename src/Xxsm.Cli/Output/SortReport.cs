namespace Xxsm.Cli.Output;

/// <summary>The machine-readable payload of <c>xxsm sort explain</c>.</summary>
/// <param name="Root">The mod folder that was examined.</param>
/// <param name="GameId">The game its variants came from.</param>
/// <param name="Variant">The variant it was filed to, or null for <c>Others/</c>.</param>
/// <param name="Family">The winning variant's family, or null.</param>
/// <param name="DecidedBy">Which tier decided: manual, hash, filename, name or unsorted.</param>
/// <param name="Confidence">How sure the sorter is, 0.5 to 1.0, or 0 when unresolved.</param>
/// <param name="Ambiguous">Whether candidates tied and nothing was chosen.</param>
/// <param name="DefaultVariantFallback">Whether the family was certain but the outfit was a guess.</param>
/// <param name="MemberDecidedByName">Whether the mod's own name chose the outfit within its family.</param>
/// <param name="VariantHasNoHashes">Whether the matched variant is still awaiting hashes.</param>
/// <param name="RunnerUp">The next best variant, when there was one.</param>
/// <param name="Explanation">Why the sorter decided what it did.</param>
/// <param name="ExtractedHashes">Every hash found in the mod.</param>
/// <param name="PrunedMatches">Hashes that were found but identify nobody.</param>
/// <param name="Candidates">Every variant considered, best first.</param>
public sealed record SortReport(
    string Root,
    string GameId,
    string? Variant,
    string? Family,
    string DecidedBy,
    double Confidence,
    bool Ambiguous,
    bool DefaultVariantFallback,
    bool MemberDecidedByName,
    bool VariantHasNoHashes,
    string? RunnerUp,
    string Explanation,
    IReadOnlyList<string> ExtractedHashes,
    IReadOnlyList<SortPrunedHash> PrunedMatches,
    IReadOnlyList<SortReportCandidate> Candidates);

/// <summary>One hash that was found in the mod but cannot identify anything.</summary>
/// <param name="Hash">The hash.</param>
/// <param name="Reason">Why it was pruned: ignored, fanOut or sharedShader.</param>
/// <param name="VariantCount">How many variants claim it.</param>
public sealed record SortPrunedHash(string Hash, string Reason, int VariantCount);

/// <summary>One variant the sorter weighed.</summary>
/// <param name="Variant">Its internal name.</param>
/// <param name="Family">Its family.</param>
/// <param name="Score">Its own weighted score.</param>
/// <param name="FamilyScore">The summed score of its whole family.</param>
/// <param name="MatchedHashes">The mod's hashes it claims.</param>
/// <param name="FamilyUniqueMatches">How many of those no sibling claims.</param>
public sealed record SortReportCandidate(
    string Variant,
    string Family,
    int Score,
    int FamilyScore,
    IReadOnlyList<string> MatchedHashes,
    int FamilyUniqueMatches);

/// <summary>The machine-readable payload of <c>xxsm sort index</c>.</summary>
/// <param name="GameId">The game.</param>
/// <param name="VariantCount">How many variants the merged data holds.</param>
/// <param name="HashesPendingCount">How many of those are still awaiting hashes.</param>
/// <param name="IdentifyingHashes">How many hashes can identify a variant.</param>
/// <param name="AmbiguityThreshold">The fan-out limit this index was built with.</param>
/// <param name="Pruned">Every hash that was discarded, and why.</param>
public sealed record SortIndexReport(
    string GameId,
    int VariantCount,
    int HashesPendingCount,
    int IdentifyingHashes,
    int AmbiguityThreshold,
    IReadOnlyList<SortPrunedHash> Pruned);
