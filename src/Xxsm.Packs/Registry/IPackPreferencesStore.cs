namespace Xxsm.Packs.Registry;

/// <summary>Reads and writes <see cref="PackPreferences"/>, atomically.</summary>
public interface IPackPreferencesStore
{
    /// <summary>The file the preferences live in.</summary>
    string PreferencesPath { get; }

    /// <summary>Reads the preferences, or an empty set when the user has none.</summary>
    /// <returns>Never null.</returns>
    /// <exception cref="Xxsm.Core.PackLoadException">The file exists but cannot be read.</exception>
    Task<PackPreferences> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the preferences: temp file, then rename over the original.</summary>
    /// <exception cref="Xxsm.Core.ModOperationException">The write failed.</exception>
    Task WriteAsync(PackPreferences preferences, CancellationToken cancellationToken = default);

    /// <summary>Reads, applies a change to one game's preference, and writes the result back.</summary>
    /// <param name="gameId">The game to change.</param>
    /// <param name="change">Given the current preference, returns the new one.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    Task<PackPreferences> UpdateGameAsync(
        string gameId,
        Func<PackGamePreference, PackGamePreference> change,
        CancellationToken cancellationToken = default);
}
