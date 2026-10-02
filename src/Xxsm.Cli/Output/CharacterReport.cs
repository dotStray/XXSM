using Xxsm.Packs.Hashes;

namespace Xxsm.Cli.Output;

/// <summary>One character as <c>xxsm character list</c> reports it.</summary>
/// <param name="InternalName">The id, and the folder mods are filed under.</param>
/// <param name="DisplayName">The name shown in the app.</param>
/// <param name="BaseCharacterId">The character this is an outfit of, or null.</param>
/// <param name="IsDefaultVariant">Whether it is its family's fallback.</param>
/// <param name="ModFilesName">The folder name under the Mods directory.</param>
/// <param name="Origin">Where its data came from: <c>pack</c>, <c>modified</c> or <c>custom</c>.</param>
/// <param name="IsLocked">Whether pack updates are blocked for it.</param>
/// <param name="LockedFields">With <paramref name="IsLocked"/> false, what is still protected.</param>
/// <param name="HashCount">How many hashes it has.</param>
/// <param name="HashesPending">Whether it has none, so it sorts by name and filename.</param>
/// <param name="Hidden">Whether it is hidden from the grid.</param>
/// <param name="Aliases">Extra strings the name matcher accepts.</param>
public sealed record CharacterReport(
    string InternalName,
    string DisplayName,
    string? BaseCharacterId,
    bool IsDefaultVariant,
    string ModFilesName,
    string Origin,
    bool IsLocked,
    IReadOnlyList<string> LockedFields,
    int HashCount,
    bool HashesPending,
    bool Hidden,
    IReadOnlyList<string> Aliases);

/// <summary>What one Character Manager write did.</summary>
/// <param name="InternalName">The id it was written under.</param>
/// <param name="DisplayName">The name shown in the app.</param>
/// <param name="Origin">Where its data comes from now.</param>
/// <param name="Changed">Whether the overlay was written.</param>
/// <param name="RequestedInternalName">The id asked for, when it had to be adjusted.</param>
/// <param name="HashCount">How many hashes it has afterwards.</param>
/// <param name="Notes">Conflicts and oddities recorded rather than refused.</param>
public sealed record CharacterEditReport(
    string InternalName,
    string DisplayName,
    string Origin,
    bool Changed,
    string? RequestedInternalName,
    int HashCount,
    IReadOnlyList<string> Notes);

/// <summary>What deleting a custom character did.</summary>
/// <param name="InternalName">The id that was removed.</param>
/// <param name="Changed">Whether anything was removed.</param>
/// <param name="RehomedTo">Where its mods went, or null when it had none.</param>
/// <param name="RehomedMods">The mods that moved.</param>
/// <param name="Notes">Anything noticed on the way.</param>
/// <param name="RestoreRecord">The record <c>xxsm character restore</c> undoes it from, or null.</param>
public sealed record CharacterDeleteReport(
    string InternalName,
    bool Changed,
    string? RehomedTo,
    IReadOnlyList<string> RehomedMods,
    IReadOnlyList<string> Notes,
    string? RestoreRecord);

/// <summary>A mod <c>xxsm character restore</c> could not move back.</summary>
/// <param name="Path">Where the mod was expected to be.</param>
/// <param name="Reason">Why it stayed.</param>
public sealed record CharacterRestoreSkipReport(string Path, string Reason);

/// <summary>What undoing a character deletion did.</summary>
/// <param name="InternalName">The character's id.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="Complete">Whether everything came back.</param>
/// <param name="RestoredMods">Where each mod that came back now is.</param>
/// <param name="Skipped">The mods that could not come back.</param>
/// <param name="Notes">Anything noticed on the way.</param>
public sealed record CharacterRestoreReport(
    string InternalName,
    string DisplayName,
    bool Complete,
    IReadOnlyList<string> RestoredMods,
    IReadOnlyList<CharacterRestoreSkipReport> Skipped,
    IReadOnlyList<string> Notes);

/// <summary>One hash a paste or a mod yielded.</summary>
/// <param name="Hash">The hash, lowercase.</param>
/// <param name="Kind">Which buffer or texture it is, or <c>unknown</c>.</param>
/// <param name="KindWasInferred">Whether the kind was read off a section name rather than named outright.</param>
/// <param name="Component">The upstream component it belongs to, where one was given.</param>
/// <param name="Evidence">Where it came from.</param>
public sealed record ParsedHashReport(
    string Hash,
    string Kind,
    bool KindWasInferred,
    string? Component,
    string Evidence);

/// <summary>What <c>xxsm character hashes parse</c> or <c>learn</c> understood.</summary>
/// <param name="Shape">What the input turned out to be.</param>
/// <param name="Hashes">The hashes that were understood.</param>
/// <param name="CountByKind">How many of each kind, commonest first.</param>
/// <param name="Rejected">Things that looked like hashes and were not, with a reason.</param>
/// <param name="IgnoredLines">How many lines were read past without comment.</param>
/// <param name="MatchedVariantId">The character the sorter identified, or null.</param>
/// <param name="MatchedBy">Which tier decided, when one did.</param>
/// <param name="Confidence">How sure the sorter is, from 0.5 to 1.0, or 0.</param>
/// <param name="SuggestedName">A name to create a character with, for a mod that matched nothing.</param>
/// <param name="Explanation">The sorter's own account of the decision.</param>
public sealed record HashReadReport(
    string Shape,
    IReadOnlyList<ParsedHashReport> Hashes,
    IReadOnlyList<KindCountReport> CountByKind,
    IReadOnlyList<HashPasteRejection> Rejected,
    int IgnoredLines,
    string? MatchedVariantId,
    string? MatchedBy,
    double Confidence,
    string? SuggestedName,
    string? Explanation);

/// <summary>What <c>xxsm character learn</c> read, and what it wrote.</summary>
/// <param name="Read">The hashes found and what the sorter made of them.</param>
/// <param name="Written">What was recorded with <c>--add-to</c> or <c>--create</c>; otherwise null.</param>
public sealed record CharacterLearnReport(HashReadReport Read, CharacterEditReport? Written);

/// <summary>How many hashes of one kind were found.</summary>
public sealed record KindCountReport(string Kind, int Count);
