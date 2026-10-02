using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Merge;

/// <summary>Keeps each game's <em>What's new</em> list, what its last pack update changed, between runs.</summary>
public interface IPackChangesStore
{
    /// <summary>Where a game's list lives, whether or not it exists.</summary>
    string GetRecordPath(string gameId);

    /// <summary>Reads what the game's last update changed.</summary>
    /// <returns>The list, or null when no update has been installed since the first install.</returns>
    /// <exception cref="ModOperationException">The file could not be read, with the operating system's
    /// words.</exception>
    Task<PackChanges?> ReadAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Records what an update changed, replacing the previous list. A failure is logged, not thrown.</summary>
    Task SaveAsync(PackChanges changes, CancellationToken cancellationToken = default);

    /// <summary>Forgets a game's list.</summary>
    /// <returns><see langword="true"/> when there was a list to forget.</returns>
    Task<bool> ClearAsync(string gameId, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IPackChangesStore"/>.</summary>
public sealed class PackChangesStore : GameRecordStore<PackChanges>, IPackChangesStore
{
    /// <summary>Creates the store.</summary>
    public PackChangesStore(IAppPaths paths, ILogger logger)
        : base(
            paths,
            "pack-changes",
            PackJsonContext.Default.PackChanges,
            "a What's new list",
            (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PackChangesStore>())
    {
    }

    /// <inheritdoc />
    public async Task SaveAsync(PackChanges changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (await WriteAsync(changes.GameId, changes, cancellationToken).ConfigureAwait(false) is { } destination)
        {
            Logger.Information(
                "Recorded what {GameId} {From} to {To} changed at {Path}",
                changes.GameId,
                changes.FromVersion,
                changes.ToVersion,
                destination);
        }
    }
}
