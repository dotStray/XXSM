namespace Xxsm.Core.Mods;

/// <summary>Reads and writes a mod's <c>.xxsm/mod.json</c>.</summary>
public interface IModConfigStore
{
    /// <summary>Where a mod folder's metadata file lives. Nothing is created.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <returns>The absolute path of <c>.xxsm/mod.json</c> inside it.</returns>
    string GetConfigPath(string modFolder);

    /// <summary>Where the backup written before each rewrite lives.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <returns>The absolute path of the <c>.bak</c>.</returns>
    string GetBackupPath(string modFolder);

    /// <summary>Reads a mod's metadata.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The metadata, or null when the mod has none yet.</returns>
    /// <exception cref="ModOperationException">The file exists but cannot be read or is not valid JSON; never read as
    /// absent.</exception>
    Task<ModConfig?> ReadAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Writes a mod's metadata, atomically, keeping a backup of what was there.</summary>
    /// <param name="modFolder">The mod folder. Must exist.</param>
    /// <param name="config">The metadata to write.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">The folder is missing, or the write failed.</exception>
    Task WriteAsync(string modFolder, ModConfig config, CancellationToken cancellationToken = default);

    /// <summary>Reads, changes and rewrites a mod's metadata in one step, keeping keys it does not know.</summary>
    /// <param name="modFolder">The mod folder. Must exist.</param>
    /// <param name="update">Applied to the existing metadata, or to a new one with only an id and a date added.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="ModOperationException">The folder is missing, the existing file could not be read, or the write
    /// failed.</exception>
    Task<ModConfig> UpdateAsync(
        string modFolder,
        Func<ModConfig, ModConfig> update,
        CancellationToken cancellationToken = default);
}
