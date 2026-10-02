using System.Text.Json.Serialization;
using Xxsm.Core.Io;
using Xxsm.Packs.Registry;

namespace Xxsm.Packs.Installation;

/// <summary>Changes what is installed of each game's pack by hand: the version in use, removal, restore.</summary>
/// <remarks>Nothing is deleted: removals go to the trash, with a record so a later process can restore.</remarks>
public interface IInstalledPacks
{
    /// <summary>Makes one installed version the one in use; any but the newest is pinned.</summary>
    Task<PackGamePreference> UseVersionAsync(string gameId, string packVersion, CancellationToken cancellationToken = default);

    /// <summary>Moves one installed version to the trash; if it was in use, the newest left is used.</summary>
    Task<PackRemoval> RemoveVersionAsync(string gameId, string packVersion, CancellationToken cancellationToken = default);

    /// <summary>Moves every installed version of a game's pack to the trash, and optionally the corrections.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="withCorrections">Whether the user's corrections and characters for the game go too.</param>
    /// <param name="cancellationToken">Cancels the removal before anything has moved.</param>
    /// <returns>What went to the trash, for <see cref="RestoreAsync"/>.</returns>
    /// <exception cref="Xxsm.Core.ModOperationException">No pack is installed, or something could not be moved;
    /// whatever had moved is put back first.</exception>
    Task<PackRemoval> RemoveAsync(string gameId, bool withCorrections = false, CancellationToken cancellationToken = default);

    /// <summary>Puts back what a removal trashed, and its pin; never over anything that is there now.</summary>
    /// <param name="removal">What a removal returned, or its record read back.</param>
    /// <param name="cancellationToken">Cancels the restore between items.</param>
    Task<PackRestoreResult> RestoreAsync(PackRemoval removal, CancellationToken cancellationToken = default);

    /// <summary>Marks an installed version to keep, or takes the mark off; a kept one is never pruned.</summary>
    Task<PackGamePreference> KeepVersionAsync(string gameId, string packVersion, bool keep, CancellationToken cancellationToken = default);

    /// <summary>Lists installed versions old enough to go: replaced long enough, not in use, newest or kept.</summary>
    Task<IReadOnlyList<OldPackVersion>> FindOldVersionsAsync(string? gameId = null, CancellationToken cancellationToken = default);

    /// <summary>Moves every version <see cref="FindOldVersionsAsync"/> lists to the trash, each restorable.</summary>
    /// <param name="gameId">One game, or null for every game.</param>
    /// <param name="cancellationToken">Cancels between versions.</param>
    Task<PackPruneResult> RemoveOldVersionsAsync(string? gameId = null, CancellationToken cancellationToken = default);

    /// <summary>Lists working folders a crashed install left behind and nothing has touched for an hour.</summary>
    IReadOnlyList<string> FindLeftovers();

    /// <summary>Moves every folder <see cref="FindLeftovers"/> lists to the trash.</summary>
    Task<PackLeftoversResult> RemoveLeftoversAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the user has an overlay or its backup for a game, which removal can take along.</summary>
    /// <returns>True when there is something of the user's to keep or remove.</returns>
    bool HasCorrections(string gameId);

    /// <summary>Reads a removal's record back, for <c>xxsm pack restore</c>.</summary>
    /// <param name="recordPath">The record, as the removal reported it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The file is missing or is not such a record.</exception>
    Task<PackRemoval> ReadRemovalAsync(string recordPath, CancellationToken cancellationToken = default);
}

/// <summary>An installed version old enough to go to the trash on its own.</summary>
/// <param name="GameId">The game.</param>
/// <param name="DisplayName">The game's name, for saying what went.</param>
/// <param name="PackVersion">The version.</param>
/// <param name="ReplacedAt">When the version after it was installed: the day it stopped being the newest.</param>
public sealed record OldPackVersion(string GameId, string? DisplayName, string PackVersion, DateTimeOffset ReplacedAt);

/// <summary>What removing old versions did.</summary>
/// <param name="Removed">Each version that went to the trash, restorable with <see
/// cref="IInstalledPacks.RestoreAsync"/>.</param>
/// <param name="Failed">Each version that could not go, with the reason in the operating system's words.</param>
public sealed record PackPruneResult(IReadOnlyList<PackRemoval> Removed, IReadOnlyList<PackPruneFailure> Failed)
{
    /// <summary>The days an old version was allowed, for saying why these went.</summary>
    public int AfterDays { get; init; }
}

/// <summary>What <see cref="IInstalledPacks.RemoveLeftoversAsync"/> moved to the trash.</summary>
/// <param name="Removed">Each folder that went, by its old path.</param>
/// <param name="Failed">Each folder that could not go, and why.</param>
public sealed record PackLeftoversResult(IReadOnlyList<string> Removed, IReadOnlyList<PackLeftoverFailure> Failed);

/// <summary>A working folder that could not be moved to the trash.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Message">Why, in the operating system's words.</param>
public sealed record PackLeftoverFailure(string Path, string Message);

/// <summary>An old version that could not be moved to the trash.</summary>
public sealed record PackPruneFailure(string GameId, string PackVersion, string Message);

/// <summary>What a removal of installed packs moved to the trash, kept so it can be put back.</summary>
public sealed record PackRemoval
{
    /// <summary>The game.</summary>
    public required string GameId { get; init; }

    /// <summary>The game's name, for saying what went.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The one version removed, or null when the whole pack was.</summary>
    public string? PackVersion { get; init; }

    /// <summary>When it was removed.</summary>
    public required DateTimeOffset RemovedAt { get; init; }

    /// <summary>Every folder and file moved to the trash: pack versions, and the corrections when they went.</summary>
    public required IReadOnlyList<TrashResult> Trashed { get; init; }

    /// <summary>Whether the user's corrections for the game went too.</summary>
    public bool CorrectionsRemoved { get; init; }

    /// <summary>The version the game was pinned to before, which the removal cleared, or null.</summary>
    public string? PinnedVersion { get; init; }

    /// <summary>Where this record is kept, for <c>xxsm pack restore</c>; null when it could not be written.</summary>
    [JsonIgnore]
    public string? RecordPath { get; init; }
}

/// <summary>What putting a removal back did.</summary>
public sealed record PackRestoreResult
{
    /// <summary>The game.</summary>
    public required string GameId { get; init; }

    /// <summary>Everything that came back, where it is now.</summary>
    public required IReadOnlyList<string> Restored { get; init; }

    /// <summary>What stayed in the trash, and why in a sentence.</summary>
    public required IReadOnlyList<PackRestoreSkip> Skipped { get; init; }

    /// <summary>Whether everything came back.</summary>
    public bool IsComplete => Skipped.Count == 0;
}

/// <summary>An item a restore left in the trash.</summary>
/// <param name="Path">Where it was to go back to.</param>
/// <param name="Reason">Why it stayed, in the operating system's words when it had some.</param>
public sealed record PackRestoreSkip(string Path, string Reason);
