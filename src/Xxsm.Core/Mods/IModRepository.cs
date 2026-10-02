namespace Xxsm.Core.Mods;

/// <summary>Reads a Mods folder into the character folders and mods it contains.</summary>
public interface IModRepository
{
    /// <summary>Scans a Mods folder.</summary>
    /// <param name="modsDirectory">The game's Mods folder.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Everything found, and anything odd noticed on the way.</returns>
    /// <exception cref="ModOperationException">The folder is missing or cannot be listed. A bad entry inside is a
    /// diagnostic.</exception>
    Task<ModsInventory> ScanAsync(string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Reads one mod folder; its character folder is its parent's name.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The mod, with its metadata.</returns>
    /// <exception cref="ModOperationException">The folder does not exist.</exception>
    Task<InstalledMod> ReadModAsync(string modFolder, CancellationToken cancellationToken = default);
}
