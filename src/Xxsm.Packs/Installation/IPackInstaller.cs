using Xxsm.Packs.Loading;

namespace Xxsm.Packs.Installation;

/// <summary>Installs a Game Pack from a local folder or archive into the application's pack directory.</summary>
public interface IPackInstaller
{
    /// <summary>Installs a pack from a directory or a <c>.zip</c> file.</summary>
    /// <param name="source">A pack directory, or an archive containing one.</param>
    /// <param name="overwrite">Whether to replace an already-installed pack of the same version.</param>
    /// <param name="expected">The game and version the source is meant to hold, or null when unknown.</param>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <exception cref="Xxsm.Core.PackLoadException">The source is not a readable pack or not the expected one,
    /// the version is installed and <paramref name="overwrite"/> is false, or the copy failed (the installed
    /// version is then untouched).</exception>
    Task<PackInstallResult> InstallAsync(
        string source,
        bool overwrite = false,
        ExpectedPack? expected = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every installed pack version, newest first within each game.</summary>
    Task<IReadOnlyList<InstalledPack>> ListInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds the directory of the pack in use for a game: the pinned version, else the newest.</summary>
    /// <returns>The pack directory, or null when no pack is installed for that game.</returns>
    Task<string?> FindActivePackDirectoryAsync(string gameId, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of installing a pack.</summary>
/// <param name="GameId">The game the pack describes.</param>
/// <param name="PackVersion">The installed version.</param>
/// <param name="Directory">Where it was installed.</param>
/// <param name="VariantCount">How many variants it contains.</param>
/// <param name="HashEntryCount">How many hash entries it contains.</param>
/// <param name="Diagnostics">Everything the loader noticed while verifying it.</param>
public sealed record PackInstallResult(
    string GameId,
    string PackVersion,
    string Directory,
    int VariantCount,
    int HashEntryCount,
    IReadOnlyList<PackDiagnostic> Diagnostics);

/// <summary>One installed pack version.</summary>
public sealed record InstalledPack(string GameId, string PackVersion, string Directory)
{
    /// <summary>The game's name from the pack's own <c>game.json</c>, or null when it could not be read.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>The game and version a pack is meant to be, checked before it is installed.</summary>
/// <param name="GameId">The game, compared ignoring case.</param>
/// <param name="PackVersion">The version, compared exactly.</param>
public sealed record ExpectedPack(string GameId, string PackVersion);
