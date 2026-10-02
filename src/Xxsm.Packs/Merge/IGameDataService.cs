namespace Xxsm.Packs.Merge;

/// <summary>The merged read model; nothing outside this assembly reads a pack or overlay file.</summary>
public interface IGameDataService
{
    /// <summary>Loads a game's pack and overlay and returns the merged view.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="packDirectory">The installed pack directory, or null for a game with only an overlay.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <exception cref="Xxsm.Core.PackLoadException">The pack or the overlay is unreadable.</exception>
    Task<GameData> LoadAsync(
        string gameId,
        string? packDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Merges an already-loaded pack, or none, with an already-loaded overlay.</summary>
    GameData Merge(string gameId, Loading.GamePack? pack, Model.PackOverlay overlay);
}
