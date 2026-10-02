using Xxsm.Core.Mods;
using Xxsm.Packs.Downloads;

namespace Xxsm.Packs.GameBanana;

/// <summary>One installed mod that came from the GameBanana page being asked about.</summary>
/// <param name="ModFolder">The mod's own folder.</param>
/// <param name="FolderName">That folder's name, as it is on disk.</param>
/// <param name="DisplayName">What the mod is called.</param>
/// <param name="VariantFolderName">The character folder it is filed under, or null when loose at the top.</param>
public sealed record InstalledCopy(
    string ModFolder, string FolderName, string DisplayName, string? VariantFolderName);

/// <summary>What XXSM already has of one GameBanana mod.</summary>
/// <param name="Download">A running download of it, or a finished one with its archive, for the same game;
/// otherwise null.</param>
/// <param name="Installed">Every installed mod that records this page, in folder order.</param>
public sealed record ModCopies(DownloadJob? Download, IReadOnlyList<InstalledCopy> Installed)
{
    /// <summary>Nothing: go ahead and download it.</summary>
    public static ModCopies None { get; } = new(null, []);

    /// <summary>Whether a download of it is running or ready to install.</summary>
    public bool HasDownload => Download is not null;

    /// <summary>Whether it is installed at least once.</summary>
    public bool IsInstalled => Installed.Count > 0;
}

/// <summary>Answers "do I already have this mod?" before a GameBanana address is downloaded.</summary>
/// <remarks>Reads only the download list and each mod's <c>.xxsm/mod.json</c>.</remarks>
public interface IModCopies
{
    /// <summary>Looks for the mod on the download list and in an inventory already scanned.</summary>
    /// <param name="modId">The GameBanana mod id.</param>
    /// <param name="gameId">The game being installed into. A download for another game is not offered.</param>
    /// <param name="inventory">What is in the Mods folder, or null to look only at the download list.</param>
    /// <param name="cancellationToken">Cancels reading the download list.</param>
    /// <returns>What there is, or <see cref="ModCopies.None"/>.</returns>
    Task<ModCopies> FindAsync(
        long modId, string? gameId, ModsInventory? inventory, CancellationToken cancellationToken = default);

    /// <summary>Scans a Mods folder and looks for the mod in it and on the download list.</summary>
    /// <param name="modId">The GameBanana mod id.</param>
    /// <param name="gameId">The game being installed into.</param>
    /// <param name="modsDirectory">The game's Mods folder.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>What there is, or <see cref="ModCopies.None"/>.</returns>
    /// <exception cref="Xxsm.Core.ModOperationException">The Mods folder could not be read.</exception>
    Task<ModCopies> FindAsync(
        long modId, string? gameId, string modsDirectory, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModCopies"/>.</summary>
public sealed class ModCopyFinder(IDownloadManager downloads, IModRepository mods) : IModCopies
{
    private readonly IDownloadManager _downloads = downloads;
    private readonly IModRepository _mods = mods;

    /// <inheritdoc />
    public async Task<ModCopies> FindAsync(
        long modId, string? gameId, ModsInventory? inventory, CancellationToken cancellationToken = default)
    {
        if (modId <= 0)
        {
            return ModCopies.None;
        }

        var jobs = await _downloads.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Newest first; running beats ready.
        var ours = jobs.Where(job => job.Record.ModId == modId && IsForGame(job, gameId)).ToList();
        var download = ours.FirstOrDefault(job => job.IsRunning)
                       ?? ours.FirstOrDefault(job => !job.IsRunning && job.Record.HasArchive);

        var installed = inventory is null
            ? []
            : inventory.AllMods
                .Where(mod => mod.Config?.GameBanana?.ModId == modId)
                .Select(mod => new InstalledCopy(mod.Path, mod.FolderName, mod.DisplayName, mod.VariantFolderName))
                .ToList();

        return download is null && installed.Count == 0
            ? ModCopies.None
            : new ModCopies(download, installed);
    }

    /// <inheritdoc />
    public async Task<ModCopies> FindAsync(
        long modId, string? gameId, string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var inventory = await _mods.ScanAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

        return await FindAsync(modId, gameId, inventory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a download was started for this game; one with no game recorded counts.</summary>
    private static bool IsForGame(DownloadJob job, string? gameId) =>
        gameId is null
        || job.Record.GameId is null
        || string.Equals(job.Record.GameId, gameId, StringComparison.OrdinalIgnoreCase);
}
