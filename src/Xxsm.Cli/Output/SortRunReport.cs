namespace Xxsm.Cli.Output;

/// <summary>What a sort run would do, or did.</summary>
/// <param name="ModsDirectory">The Mods folder.</param>
/// <param name="GameId">The game it was sorted against.</param>
/// <param name="Applied">Whether anything was moved, or this was a preview only.</param>
/// <param name="RunId">The run's id, for undo. Null on a dry run.</param>
/// <param name="ModCount">How many mods were looked at.</param>
/// <param name="PlannedMoveCount">How many the sorter would move.</param>
/// <param name="MovedCount">How many actually moved.</param>
/// <param name="Rows">One row per mod, in the order they were found.</param>
/// <param name="Failures">Mods that could not be moved, with the reason.</param>
/// <param name="Diagnostics">Anything odd noticed while reading the folder.</param>
/// <param name="KeptRemembered">The mods <c>--keep</c> left and remembered as filed there, by name.</param>
/// <param name="KeptNotRemembered">The mods <c>--keep</c> left that are not under a character, offered
/// again next time.</param>
public sealed record SortRunReport(
    string ModsDirectory,
    string GameId,
    bool Applied,
    string? RunId,
    int ModCount,
    int PlannedMoveCount,
    int MovedCount,
    IReadOnlyList<SortRunRowReport> Rows,
    IReadOnlyList<SortRunFailureReport> Failures,
    IReadOnlyList<ScanDiagnostic> Diagnostics,
    IReadOnlyList<string> KeptRemembered,
    IReadOnlyList<string> KeptNotRemembered);

/// <summary>One mod in a sort run.</summary>
/// <param name="Mod">The mod's name.</param>
/// <param name="From">The character folder it is in now, or null when loose at the top.</param>
/// <param name="To">The character folder it belongs in.</param>
/// <param name="Action">move, alreadyFiled, manuallyFiled or unidentifiedButFiled.</param>
/// <param name="DecidedBy">Which tier decided: manual, hash, filename, name or unsorted.</param>
/// <param name="Confidence">How sure the sorter is, 0 to 1.</param>
/// <param name="VariantIsUncertain">Whether the family is settled but the outfit within it is a guess.</param>
/// <param name="Reason">Why, in plain language.</param>
/// <param name="MovedTo">The folder name it ended up with, when a clash forced a rename.</param>
public sealed record SortRunRowReport(
    string Mod,
    string? From,
    string To,
    string Action,
    string DecidedBy,
    double Confidence,
    bool VariantIsUncertain,
    string Reason,
    string? MovedTo);

/// <summary>A mod a sort run could not move.</summary>
/// <param name="Mod">The mod's name.</param>
/// <param name="Error">The reason, including the operating system's own words.</param>
public sealed record SortRunFailureReport(string Mod, string Error);

/// <summary>What undoing a sort run did.</summary>
/// <param name="RunId">The run that was undone.</param>
/// <param name="RestoredCount">How many mods were put back.</param>
/// <param name="SkippedCount">How many could not be.</param>
/// <param name="Rows">One row per mod in the run.</param>
public sealed record SortUndoReport(
    string RunId,
    int RestoredCount,
    int SkippedCount,
    IReadOnlyList<SortUndoRowReport> Rows);

/// <summary>One mod an undo tried to put back.</summary>
/// <param name="From">Where it was, relative to the Mods folder.</param>
/// <param name="To">Where it went back to, relative to the Mods folder.</param>
/// <param name="Restored">Whether it moved.</param>
/// <param name="Note">Why it did not, when it did not.</param>
public sealed record SortUndoRowReport(string From, string To, bool Restored, string? Note);

/// <summary>The sort runs recorded in a Mods folder's journal.</summary>
public sealed record SortRunsReport(
    string ModsDirectory,
    string JournalPath,
    IReadOnlyList<SortRunSummaryReport> Runs);

/// <summary>One past sort run.</summary>
/// <param name="RunId">Its id, which <c>xxsm sort undo --run</c> takes.</param>
/// <param name="At">When it ran.</param>
/// <param name="MoveCount">How many mods it moved.</param>
/// <param name="UndoneCount">How many of those have been put back since.</param>
/// <param name="FullyUndone">Whether the whole run has been undone.</param>
public sealed record SortRunSummaryReport(
    string RunId,
    DateTimeOffset At,
    int MoveCount,
    int UndoneCount,
    bool FullyUndone);
