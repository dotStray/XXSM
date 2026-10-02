namespace Xxsm.Core.Settings;

/// <summary>Reads and writes <see cref="AppSettings"/>, <c>&lt;config&gt;/settings.json</c>.</summary>
public interface IAppSettingsStore
{
    /// <summary>The file the settings live in.</summary>
    string SettingsPath { get; }

    /// <summary>Reads the settings, or every default when there is no file.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Never null.</returns>
    /// <exception cref="SettingsLoadException">The file exists but cannot be read; never read as defaults.</exception>
    Task<AppSettings> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the settings through a temporary file renamed over the original.</summary>
    /// <param name="settings">What to save.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">The write failed.</exception>
    Task WriteAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Reads, applies a change, and writes the result back, keeping keys it does not know.</summary>
    /// <param name="change">Given the current settings, returns the new ones.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The settings as saved.</returns>
    Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> change,
        CancellationToken cancellationToken = default);

    /// <summary>Reads, applies a change to one game's settings, and writes the result back.</summary>
    /// <param name="gameId">The game to change.</param>
    /// <param name="change">Given the current settings for that game, returns the new ones.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The settings as saved.</returns>
    Task<AppSettings> UpdateGameAsync(
        string gameId,
        Func<GameSettings, GameSettings> change,
        CancellationToken cancellationToken = default);

    /// <summary>The Mods folder for a game: the one given, else the one saved.</summary>
    /// <param name="gameId">The game, or null to skip the saved one.</param>
    /// <param name="explicitPath">What the caller was told to use, or null.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The folder to use.</returns>
    /// <exception cref="SettingsLoadException">Neither was available. The message names both ways to fix
    /// it.</exception>
    Task<string> ResolveModsDirectoryAsync(
        string? gameId,
        string? explicitPath,
        CancellationToken cancellationToken = default);
}
