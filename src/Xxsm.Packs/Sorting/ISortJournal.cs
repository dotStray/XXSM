using Xxsm.Core;

namespace Xxsm.Packs.Sorting;

/// <summary>The append-only record of every mod a sort run moved; an undo appends its own lines.</summary>
public interface ISortJournal
{
    /// <summary>Where the journal for a Mods folder lives.</summary>
    string GetJournalPath(string modsDirectory);

    /// <summary>Appends lines to the journal.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="entries">The lines to append, in order.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">The journal could not be written.</exception>
    Task AppendAsync(
        string modsDirectory,
        IReadOnlyList<SortJournalEntry> entries,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the whole journal.</summary>
    /// <returns>Every readable line, oldest first; empty when there is no journal. A damaged line is
    /// skipped.</returns>
    Task<IReadOnlyList<SortJournalEntry>> ReadAsync(
        string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Summarises the runs in the journal, newest first.</summary>
    Task<IReadOnlyList<SortRunSummary>> ListRunsAsync(
        string modsDirectory, CancellationToken cancellationToken = default);
}

/// <summary>One past sort run, as the notifications panel offers it back to the user.</summary>
/// <param name="RunId">The run's id.</param>
/// <param name="At">When it ran.</param>
/// <param name="MoveCount">How many mods it moved.</param>
/// <param name="UndoneCount">How many of those have since been put back.</param>
public sealed record SortRunSummary(string RunId, DateTimeOffset At, int MoveCount, int UndoneCount)
{
    /// <summary>Whether every move in the run has been undone.</summary>
    public bool IsFullyUndone => MoveCount > 0 && UndoneCount >= MoveCount;

    /// <summary>How many moves are still in place and could be undone.</summary>
    public int UndoableCount => Math.Max(0, MoveCount - UndoneCount);
}
