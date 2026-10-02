using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;

namespace Xxsm.Packs.Registry;

/// <summary>The pack side of the application: what is on offer, what is installed, and moving between them.</summary>
public interface IPackService
{
    /// <summary>Builds the catalogue from every configured registry, plus what is installed.</summary>
    /// <param name="registries">Registries to consult instead of the configured ones; null for the user's list,
    /// or the default when they have none.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The catalogue; unreachable registries are in its failures, not thrown.</returns>
    Task<PackCatalogResult> GetCatalogAsync(
        IReadOnlyList<string>? registries = null,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads, verifies and installs one pack version.</summary>
    /// <param name="gameId">The game to install.</param>
    /// <param name="packVersion">The version, or null for their pin, else the newest this build can read.</param>
    /// <param name="registries">Registries to consult, or null for the configured ones.</param>
    /// <param name="overwrite">Whether to reinstall a version already on disk.</param>
    /// <param name="progress">Told each step and how much has downloaded; null when nobody is watching.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <exception cref="PackRegistryException">No such game or version, the version cannot be read by this build,
    /// or the download failed its checksum.</exception>
    Task<PackOperationResult> InstallAsync(
        string gameId,
        string? packVersion = null,
        IReadOnlyList<string>? registries = null,
        bool overwrite = false,
        IProgress<PackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Installs a pack from a zip or a folder on this computer, as an update like any other.</summary>
    /// <param name="source">The pack zip or folder.</param>
    /// <param name="overwrite">Whether to reinstall a version already on disk.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <exception cref="Xxsm.Core.PackLoadException">The source is not a pack this build can read.</exception>
    /// <exception cref="Xxsm.Core.ModOperationException">That version is already installed, or the install
    /// failed.</exception>
    Task<PackOperationResult> ImportAsync(
        string source,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    /// <summary>Updates one game, or every game that has a pack installed.</summary>
    /// <param name="gameId">The game, or null for all installed games.</param>
    /// <param name="registries">Registries to consult, or null for the configured ones.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>One result per game, including those left alone and why.</returns>
    Task<IReadOnlyList<PackOperationResult>> UpdateAsync(
        string? gameId = null,
        IReadOnlyList<string>? registries = null,
        CancellationToken cancellationToken = default);

    /// <summary>Pins a game to one pack version, or removes its pin.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="packVersion">The version to pin to, or null to unpin.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<PackGamePreference> PinAsync(
        string gameId, string? packVersion, CancellationToken cancellationToken = default);

    /// <summary>What the pack a game uses now holds, read from the installed pack itself.</summary>
    /// <returns>Its contents, or <see langword="null"/> when no pack is installed for it.</returns>
    /// <exception cref="Xxsm.Core.PackLoadException">The installed pack cannot be read.</exception>
    Task<Loading.PackContents?> ReadContentsAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Turns automatic pack updates on or off, for every game.</summary>
    /// <param name="enabled">Whether the app installs a newer pack by itself when it finds one.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<PackPreferences> SetAutoUpdateAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>What an install or update did.</summary>
public enum PackOperationOutcome
{
    /// <summary>The pack was downloaded, verified and installed.</summary>
    Installed,

    /// <summary>That version was already installed and nothing needed doing.</summary>
    AlreadyCurrent,

    /// <summary>The user has pinned this game, so a newer version was not installed.</summary>
    Pinned,

    /// <summary>The registry offers nothing this build can install.</summary>
    NothingAvailable,
}

/// <summary>The result of installing or updating one game's pack.</summary>
public sealed record PackOperationResult
{
    /// <summary>The game.</summary>
    public required string GameId { get; init; }

    /// <summary>What happened.</summary>
    public required PackOperationOutcome Outcome { get; init; }

    /// <summary>The version now active, or null when nothing is installed.</summary>
    public string? PackVersion { get; init; }

    /// <summary>The version that was active before, when this was an update.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>Where it was installed.</summary>
    public string? Directory { get; init; }

    /// <summary>A sentence explaining the outcome, for the CLI and the notifications panel.</summary>
    public required string Message { get; init; }

    /// <summary>What this update changed from the version it replaced; null for a first install.</summary>
    public PackChanges? Changes { get; init; }

    /// <summary>Changes the new pack would have made to a variant the user edited, and so withheld.</summary>
    public IReadOnlyList<SkippedUpdate> SkippedUpdates { get; init; } = [];

    /// <summary>Custom variants the new pack has caught up with, offered for adoption.</summary>
    public IReadOnlyList<string> AdoptionCandidates { get; init; } = [];

    /// <summary>What the installer reported, when this operation installed something.</summary>
    public PackInstallResult? Installed { get; init; }

    /// <summary>Whether anything was actually installed.</summary>
    public bool Changed => Outcome == PackOperationOutcome.Installed;
}
