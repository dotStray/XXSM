using Serilog;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Studio;

/// <summary><em>Try it</em>: runs the real sorter in dry-run against a folder of mods using a draft.</summary>
public interface IPackTrial
{
    /// <summary>The draft as the application would see it installed; rows the loader would drop are left out.</summary>
    GameData ToGameData(PackDraft draft);

    /// <summary>Works out where every mod in a folder would go with this draft installed. Moves nothing.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="modsDirectory">The folder of mods to try it against.</param>
    /// <param name="settings">Sorting thresholds. Defaults to <see cref="SortSettings.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The folder does not exist.</exception>
    Task<SortRunPlan> PlanAsync(
        PackDraft draft,
        string modsDirectory,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IPackTrial"/>: the draft as a loaded pack, handed to the sort runner.</summary>
public sealed class PackTrial(
    IStudioDraftStore store,
    IGameDataService gameData,
    ISortRunner runner,
    ILogger logger) : IPackTrial
{
    private readonly IStudioDraftStore _store = store;
    private readonly IGameDataService _gameData = gameData;
    private readonly ISortRunner _runner = runner;
    private readonly ILogger _logger = logger.ForContext<PackTrial>();

    /// <inheritdoc />
    public GameData ToGameData(PackDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var pack = MergedVariantDifferences.UsablePack(draft, _store.GetDraftDirectory(draft.GameId));

        return _gameData.Merge(draft.GameId, pack, new PackOverlay { GameId = draft.GameId });
    }

    /// <inheritdoc />
    public async Task<SortRunPlan> PlanAsync(
        PackDraft draft,
        string modsDirectory,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var plan = await _runner.PlanAsync(modsDirectory, ToGameData(draft), settings, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information(
            "Tried Studio draft {GameId} against {ModsDirectory}: {Rows} mods, {Moves} would move",
            draft.GameId,
            modsDirectory,
            plan.Rows.Count,
            plan.Moves.Count);

        return plan;
    }
}
