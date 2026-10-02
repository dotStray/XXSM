namespace Xxsm.Core.Mods;

/// <summary>Switches a set of mods on and off in one go, journalled, and undoes the whole set later.</summary>
public interface IModSwitcher
{
    /// <summary>Where the journal for a Mods folder lives. Nothing is created.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <returns>The absolute path of <c>.xxsm/switch-log.jsonl</c>.</returns>
    string GetJournalPath(string modsDirectory);

    /// <summary>Switches the given mods, journalling the run; one mod that fails does not stop the rest.</summary>
    /// <param name="modsDirectory">The Mods folder every mod is inside.</param>
    /// <param name="switches">The switches to make. One already in the asked-for state is left alone.</param>
    /// <param name="source">What asked for them, recorded in the journal.</param>
    /// <param name="label">A profile's name, recorded in the journal; null otherwise.</param>
    /// <param name="cancellationToken">Cancels between mods; those already switched stay switched and
    /// journalled.</param>
    /// <returns>What happened to each switch, and the run id to undo them by.</returns>
    /// <exception cref="ModOperationException">A mod is not inside the Mods folder, or the journal could not be
    /// written.</exception>
    Task<ModSwitchRunResult> ApplyAsync(
        string modsDirectory,
        IReadOnlyList<ModSwitch> switches,
        ModSwitchSource source,
        string? label,
        CancellationToken cancellationToken = default);

    /// <summary>Puts a whole run back, never overwriting; a mod moved since is reported and left.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="runId">The run to undo; the most recent not already undone when null.</param>
    /// <param name="cancellationToken">Cancels between mods.</param>
    /// <returns>What was put back, and what could not be.</returns>
    /// <exception cref="ModOperationException">There is no such run, or nothing is left to undo.</exception>
    Task<ModSwitchUndoResult> UndoAsync(
        string modsDirectory,
        string? runId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Summarises the runs in the journal, newest first.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One entry per run, newest first; empty when nothing was journalled.</returns>
    /// <exception cref="ModOperationException">The journal exists but could not be read.</exception>
    Task<IReadOnlyList<ModSwitchRunSummary>> ListRunsAsync(
        string modsDirectory, CancellationToken cancellationToken = default);
}
