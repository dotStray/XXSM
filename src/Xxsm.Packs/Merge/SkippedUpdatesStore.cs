using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Merge;

/// <summary>What the last pack update withheld from a game, kept so the review can be opened again.</summary>
public sealed record SkippedUpdateRecord
{
    /// <summary>The game the changes were withheld from.</summary>
    public required string GameId { get; init; }

    /// <summary>The pack version that wanted to make them.</summary>
    public string? PackVersion { get; init; }

    /// <summary>When they were withheld.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>One entry per variant, each with the fields that were withheld.</summary>
    public IReadOnlyList<SkippedUpdate> Updates { get; init; } = [];
}

/// <summary>Keeps the <em>Skipped updates</em> review's contents between runs, one file per game.</summary>
public interface ISkippedUpdatesStore
{
    /// <summary>Where a game's record lives, whether or not it exists.</summary>
    string GetRecordPath(string gameId);

    /// <summary>Reads what the last update withheld.</summary>
    /// <returns>The record, or null when the last update withheld nothing.</returns>
    /// <exception cref="ModOperationException">The file could not be read, with the operating system's
    /// words.</exception>
    Task<SkippedUpdateRecord?> ReadAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Records what an update withheld, replacing the old record. A failure is logged, not thrown.</summary>
    /// <param name="record">What was withheld. An empty <see cref="SkippedUpdateRecord.Updates"/> clears it.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    Task SaveAsync(SkippedUpdateRecord record, CancellationToken cancellationToken = default);

    /// <summary>Forgets a game's record, because there is nothing left to review.</summary>
    /// <returns><see langword="true"/> when there was a record to forget.</returns>
    Task<bool> ClearAsync(string gameId, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="ISkippedUpdatesStore"/>.</summary>
public sealed class SkippedUpdatesStore : GameRecordStore<SkippedUpdateRecord>, ISkippedUpdatesStore
{
    /// <summary>Creates the store.</summary>
    public SkippedUpdatesStore(IAppPaths paths, ILogger logger)
        : base(
            paths,
            "skipped-updates",
            PackJsonContext.Default.SkippedUpdateRecord,
            "a skipped-updates record",
            (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<SkippedUpdatesStore>())
    {
    }

    /// <inheritdoc />
    public async Task SaveAsync(SkippedUpdateRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (await WriteAsync(record.GameId, record, cancellationToken).ConfigureAwait(false) is { } destination)
        {
            Logger.Information(
                "Recorded {Count} withheld character update(s) for {GameId} at {Path}",
                record.Updates.Count,
                record.GameId,
                destination);
        }
    }

    /// <inheritdoc />
    protected override bool IsNothing(SkippedUpdateRecord record) => record.Updates.Count == 0;
}
