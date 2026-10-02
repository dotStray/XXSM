using Xxsm.Core.Diagnostics;
using Xxsm.Core.Mods;

namespace Xxsm.Packs.Sorting;

/// <summary>Why a mod is, or is not, going to move.</summary>
public enum SortRowAction
{
    /// <summary>The mod will move to the destination folder.</summary>
    Move,

    /// <summary>The mod is already filed where the sorter would put it.</summary>
    AlreadyFiled,

    /// <summary>A human filed this mod. Auto-sort never overrides one.</summary>
    ManuallyFiled,

    /// <summary>Unidentified, but already under a character, so left where the user put it.</summary>
    UnidentifiedButFiled,
}

/// <summary>How a row's character was found, in the three groups a person reviewing a sort asks about.</summary>
public enum SortMatchKind
{
    /// <summary>A person filed it, and that is never second-guessed (tier 0).</summary>
    Manual,

    /// <summary>The mod's own hashes identified the character (tier 1).</summary>
    Hash,

    /// <summary>A name decided some of it: the whole placement, or the outfit within a family hashes settled.</summary>
    Name,

    /// <summary>Nothing identified it, so it belongs in <c>Others</c> (tier 4).</summary>
    Others,
}

/// <summary>One row of the dry-run preview; every row can be unticked.</summary>
public sealed record SortRunRow
{
    /// <summary>The mod this row is about, as it is on disk now.</summary>
    public required InstalledMod Mod { get; init; }

    /// <summary>The full decision, with the evidence behind it.</summary>
    public required SortDecision Decision { get; init; }

    /// <summary>The character folder the mod would end up in, relative to the Mods folder.</summary>
    public required string DestinationFolderName { get; init; }

    /// <summary>What will happen to this mod.</summary>
    public required SortRowAction Action { get; init; }

    /// <summary>A short plain-language account, for the preview and the CLI.</summary>
    public required string Reason { get; init; }

    /// <summary>Whether applying the run would move this mod.</summary>
    public bool WillMove => Action == SortRowAction.Move;

    /// <summary>Whether the outfit within the family is a guess, by name or by default, not by hash.</summary>
    public bool VariantIsUncertain =>
        Decision.MemberDecidedByName || Decision.IsDefaultVariantFallback;

    /// <summary>How the character was found: by a person, by hash, by name, or not at all.</summary>
    public SortMatchKind MatchKind => Decision.DecidedBy switch
    {
        SortDecidedBy.Manual => SortMatchKind.Manual,
        SortDecidedBy.Hash when Decision.MemberDecidedByName => SortMatchKind.Name,
        SortDecidedBy.Hash => SortMatchKind.Hash,
        SortDecidedBy.Filename or SortDecidedBy.Name => SortMatchKind.Name,
        _ => SortMatchKind.Others,
    };
}

/// <summary>The dry-run preview: what a sort would do, before it does anything.</summary>
public sealed record SortRunPlan
{
    /// <summary>The Mods folder this plan is for.</summary>
    public required string ModsDirectory { get; init; }

    /// <summary>The game the plan was built against.</summary>
    public required string GameId { get; init; }

    /// <summary>Every mod that was looked at, in the order it was found.</summary>
    public required IReadOnlyList<SortRunRow> Rows { get; init; }

    /// <summary>Anything odd noticed while reading the folder.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>The rows that would actually move.</summary>
    public IReadOnlyList<SortRunRow> Moves => [.. Rows.Where(row => row.WillMove)];

    /// <summary>Whether applying this plan would change anything at all.</summary>
    public bool IsEmpty => Moves.Count == 0;

    /// <summary>The same plan narrowed to the rows whose character was found one way.</summary>
    /// <param name="kind">How the rows to keep were matched.</param>
    public SortRunPlan Matching(SortMatchKind kind) =>
        this with { Rows = [.. Rows.Where(row => row.MatchKind == kind)] };

    /// <summary>The same plan narrowed to the rows that concern one character's family, in either direction.</summary>
    /// <param name="gameData">The merged data the plan was built against.</param>
    /// <param name="internalName">The character to narrow to. Its whole family is included.</param>
    /// <returns>The rows whose mod is filed under this family or would move into it; all else
    /// unchanged.</returns>
    public SortRunPlan ForCharacter(Merge.GameData gameData, string internalName)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);

        var variant = gameData.Find(internalName);

        var family = new HashSet<string>(
            variant is null
                ? [internalName]
                : gameData.GetFamily(variant.FamilyId) is { Count: > 0 } members
                    ? members.Select(member => member.ModFilesName)
                    : [variant.ModFilesName],
            StringComparer.OrdinalIgnoreCase);

        return this with
        {
            Rows =
            [
                .. Rows.Where(row =>
                    family.Contains(row.DestinationFolderName)
                    || (row.Mod.VariantFolderName is { Length: > 0 } current && family.Contains(current))),
            ],
        };
    }

    /// <summary>The same plan narrowed to the mods no character owns, outside <c>Others</c>: Unsorted.</summary>
    public SortRunPlan ForUnsorted(Merge.GameData gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        return this with { Rows = [.. Rows.Where(row => UnsortedMods.Contains(row.Mod, gameData))] };
    }

    /// <summary>The same plan narrowed to the mods in <c>Others</c>: what the grid's Others tile holds.</summary>
    public SortRunPlan ForOthers(Merge.GameData gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);

        return this with { Rows = [.. Rows.Where(row => UnsortedMods.IsInOthers(row.Mod, gameData))] };
    }
}

/// <summary>A row the user chose not to move, whose filing could not be saved.</summary>
/// <param name="Row">The row.</param>
/// <param name="Error">Why, in the operating system's own words.</param>
public sealed record SortKeepFailure(SortRunRow Row, string Error);

/// <summary>What keeping the rows a user unticked did (<see cref="ISortRunner.KeepAsync"/>).</summary>
public sealed record SortKeepResult
{
    /// <summary>The mods now remembered as filed by hand where they are; auto-sort leaves them alone.</summary>
    public required IReadOnlyList<SortRunRow> Remembered { get; init; }

    /// <summary>The mods that stayed put but could not be remembered, not being under a character.</summary>
    public required IReadOnlyList<SortRunRow> NotRemembered { get; init; }

    /// <summary>The mods whose filing could not be saved, with the reason on each.</summary>
    public required IReadOnlyList<SortKeepFailure> Failures { get; init; }
}

/// <summary>What one applied row did.</summary>
/// <param name="Row">The row that was applied.</param>
/// <param name="Result">What the file operation did, or null when the move failed.</param>
/// <param name="Error">Why it failed, or null when it succeeded.</param>
public sealed record SortRunOutcome(SortRunRow Row, ModOperationResult? Result, string? Error)
{
    /// <summary>Whether the mod moved.</summary>
    public bool Succeeded => Result is { Changed: true };
}

/// <summary>The result of applying a sort run.</summary>
public sealed record SortRunResult
{
    /// <summary>The id every move in this run was journalled under. Undo takes this.</summary>
    public required string RunId { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>One outcome per row that was handed in.</summary>
    public required IReadOnlyList<SortRunOutcome> Outcomes { get; init; }

    /// <summary>How many mods moved.</summary>
    public int MovedCount => Outcomes.Count(outcome => outcome.Succeeded);

    /// <summary>How many failed, with the reason on each.</summary>
    public IReadOnlyList<SortRunOutcome> Failures => [.. Outcomes.Where(o => o.Error is not null)];
}

/// <summary>What one undone move did.</summary>
/// <param name="From">Where the mod was put back from, relative to the Mods folder.</param>
/// <param name="To">Where it was put back to, relative to the Mods folder.</param>
/// <param name="Restored">Whether it moved back.</param>
/// <param name="Note">Why it did not, when it did not.</param>
public sealed record SortUndoOutcome(string From, string To, bool Restored, string? Note);

/// <summary>The result of undoing a sort run.</summary>
public sealed record SortUndoResult
{
    /// <summary>The run that was undone.</summary>
    public required string RunId { get; init; }

    /// <summary>One outcome per move in that run, newest move first.</summary>
    public required IReadOnlyList<SortUndoOutcome> Outcomes { get; init; }

    /// <summary>How many mods were put back.</summary>
    public int RestoredCount => Outcomes.Count(outcome => outcome.Restored);

    /// <summary>The moves that could not be put back, with the reason on each.</summary>
    public IReadOnlyList<SortUndoOutcome> Skipped => [.. Outcomes.Where(o => !o.Restored)];
}
