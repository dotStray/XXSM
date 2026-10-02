using Xxsm.Core;

namespace Xxsm.Packs.Loading;

/// <summary>Reads a Game Pack from an installed directory.</summary>
public interface IGamePackLoader
{
    /// <summary>Loads the pack in a directory holding <c>manifest.json</c> and the files beside it.</summary>
    /// <returns>The loaded pack, with every recoverable problem in its diagnostics.</returns>
    /// <exception cref="PackLoadException">The manifest is missing or unparseable, its schema version is not
    /// supported, or a required file is missing.</exception>
    Task<GamePack> LoadAsync(string directory, CancellationToken cancellationToken = default);
}
