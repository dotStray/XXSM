namespace Xxsm.Core.Mods;

/// <summary>Walks a mod folder and collects everything it says about which character it is for.</summary>
public interface IModSignalExtractor
{
    /// <summary>Scans one mod folder.</summary>
    /// <param name="root">The mod folder.</param>
    /// <param name="bounds">The limits to work within; <see cref="ModScanBounds.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What the folder says about itself.</returns>
    /// <exception cref="ModOperationException">The folder is missing or cannot be listed. A file that cannot be read is
    /// a diagnostic.</exception>
    Task<ModSignals> ExtractAsync(
        string root, ModScanBounds? bounds = null, CancellationToken cancellationToken = default);
}
