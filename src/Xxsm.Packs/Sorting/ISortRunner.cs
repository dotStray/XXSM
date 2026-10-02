using Xxsm.Core;
using Xxsm.Packs.Merge;

namespace Xxsm.Packs.Sorting;

/// <summary>Runs an auto-sort over a whole Mods folder: previews it, applies it, undoes it.</summary>
public interface ISortRunner
{
    /// <summary>Works out what a sort would do. Changes nothing.</summary>
    /// <param name="modsDirectory">The Mods folder to sort.</param>
    /// <param name="gameData">The merged pack and overlay to sort against.</param>
    /// <param name="settings">Sorting thresholds and weights. Defaults to <see cref="SortSettings.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <exception cref="ModOperationException">The Mods folder does not exist.</exception>
    Task<SortRunPlan> PlanAsync(
        string modsDirectory,
        GameData gameData,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default);

    /// <summary>Moves the mods in the given rows as one undoable run; one failed move does not stop the rest.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="rows">The rows to apply, normally the ticked moves. Rows that would not move are
    /// ignored.</param>
    /// <param name="cancellationToken">Cancels between mods; a cancelled run is still undoable in full.</param>
    Task<SortRunResult> ApplyAsync(
        string modsDirectory,
        IReadOnlyList<SortRunRow> rows,
        CancellationToken cancellationToken = default);

    /// <summary>Remembers the unticked rows as filed by hand where they are. Moves nothing.</summary>
    /// <remarks>A mod loose or in <c>Others</c> has no character to remember, and is offered again next time.</remarks>
    /// <param name="rows">The rows that were offered as moves and not taken.</param>
    /// <param name="gameData">The merged data the plan was built against.</param>
    /// <param name="cancellationToken">Cancels between mods. Mods already remembered stay remembered.</param>
    Task<SortKeepResult> KeepAsync(
        IReadOnlyList<SortRunRow> rows,
        GameData gameData,
        CancellationToken cancellationToken = default);

    /// <summary>Puts a whole sort run back.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="runId">The run to undo. Defaults to the most recent run that has not already been undone.</param>
    /// <param name="cancellationToken">Cancels between mods.</param>
    /// <exception cref="ModOperationException">There is no such run, or there is nothing to undo.</exception>
    Task<SortUndoResult> UndoAsync(
        string modsDirectory,
        string? runId = null,
        CancellationToken cancellationToken = default);
}
