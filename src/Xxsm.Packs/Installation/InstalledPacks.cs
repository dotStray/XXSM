using System.Globalization;
using System.Text.Json;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Overlays;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Installation;

/// <summary>The default <see cref="IInstalledPacks"/>; each version folder goes to the trash on its own.</summary>
public sealed class InstalledPacks(
    IPackInstaller installer,
    IPackPreferencesStore preferences,
    IOverlayStore overlays,
    ITrashService trash,
    IAppPaths paths,
    TimeProvider time,
    ILogger logger) : IInstalledPacks
{
    private const string RecordsDirectoryName = "removed-packs";

    private readonly IPackInstaller _installer = installer;
    private readonly IPackPreferencesStore _preferences = preferences;
    private readonly IOverlayStore _overlays = overlays;
    private readonly ITrashService _trash = trash;
    private readonly IAppPaths _paths = paths;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<InstalledPacks>();

    /// <inheritdoc />
    public async Task<PackGamePreference> UseVersionAsync(
        string gameId, string packVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);

        var installed = await InstalledAsync(gameId, cancellationToken).ConfigureAwait(false);
        var version = Find(installed, gameId, packVersion);
        var pin = ActivePackVersion.PinToUse([.. installed.Select(p => p.PackVersion)], version.PackVersion);

        var saved = await _preferences
            .UpdateGameAsync(gameId, current => current with { PinnedVersion = pin }, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information(
            "{GameId} now uses pack {Version}{Held}",
            gameId,
            version.PackVersion,
            pin is null ? ", the newest, following updates" : ", held there");

        return saved.ForGame(gameId);
    }

    /// <inheritdoc />
    public async Task<PackRemoval> RemoveVersionAsync(
        string gameId, string packVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);

        var installed = await InstalledAsync(gameId, cancellationToken).ConfigureAwait(false);
        var version = Find(installed, gameId, packVersion);
        var pinned = await PinnedAsync(gameId, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var trashed = await TrashAllAsync([version.Directory]).ConfigureAwait(false);
        RemoveGameFolderIfEmpty(gameId);

        var clearedPin = string.Equals(pinned, version.PackVersion, StringComparison.OrdinalIgnoreCase) ? pinned : null;

        if (clearedPin is not null)
        {
            await _preferences
                .UpdateGameAsync(gameId, current => current with { PinnedVersion = null }, cancellationToken)
                .ConfigureAwait(false);
        }

        var removal = new PackRemoval
        {
            GameId = gameId,
            DisplayName = version.DisplayName,
            PackVersion = version.PackVersion,
            RemovedAt = _time.GetUtcNow(),
            Trashed = trashed,
            PinnedVersion = clearedPin,
        };

        _logger.Information(
            "Removed {GameId} pack {Version}: moved {Directory} to the trash", gameId, version.PackVersion, version.Directory);

        return await RecordAsync(removal).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackRemoval> RemoveAsync(
        string gameId, bool withCorrections = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var installed = await InstalledAsync(gameId, cancellationToken).ConfigureAwait(false);

        if (installed.Count == 0)
        {
            throw new ModOperationException(
                $"No pack for '{gameId}' is installed, so there is nothing to remove.",
                Path.Combine(_paths.PacksDirectory, gameId));
        }

        var pinned = await PinnedAsync(gameId, cancellationToken).ConfigureAwait(false);

        List<string> targets = [.. installed.Select(p => p.Directory)];

        if (withCorrections)
        {
            targets.AddRange(new[] { _overlays.GetOverlayPath(gameId), _overlays.GetBackupPath(gameId) }.Where(File.Exists));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var trashed = await TrashAllAsync(targets).ConfigureAwait(false);
        RemoveGameFolderIfEmpty(gameId);

        if (pinned is not null)
        {
            await _preferences
                .UpdateGameAsync(gameId, current => current with { PinnedVersion = null }, cancellationToken)
                .ConfigureAwait(false);
        }

        var removal = new PackRemoval
        {
            GameId = gameId,
            DisplayName = installed[0].DisplayName,
            RemovedAt = _time.GetUtcNow(),
            Trashed = trashed,
            CorrectionsRemoved = withCorrections && trashed.Count > installed.Count,
            PinnedVersion = pinned,
        };

        _logger.Information(
            "Removed the {GameId} pack: {Count} version(s) moved to the trash{Corrections}",
            gameId,
            installed.Count,
            removal.CorrectionsRemoved ? ", and the user's corrections with them" : string.Empty);

        return await RecordAsync(removal).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackRestoreResult> RestoreAsync(PackRemoval removal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removal);

        var restored = new List<string>();
        var skipped = new List<PackRestoreSkip>();

        foreach (var item in removal.Trashed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The trash refuses only the exact path: on a case-sensitive disk, look for other capitals too.
            if (PathComparer.TryResolveExisting(item.OriginalPath, out var existing))
            {
                skipped.Add(new PackRestoreSkip(
                    item.OriginalPath,
                    $"'{PathDisplay.Show(existing)}' is there now, so what was removed stays in the trash rather than going over it."));
                continue;
            }

            try
            {
                restored.Add(await _trash.RestoreAsync(item, cancellationToken).ConfigureAwait(false));
                _logger.Information("Put back {Path} from the trash", item.OriginalPath);
            }
            catch (ModOperationException ex)
            {
                skipped.Add(new PackRestoreSkip(item.OriginalPath, ex.Message));
            }
        }

        if (removal.PinnedVersion is { Length: > 0 } pinned
            && await PinnedAsync(removal.GameId, cancellationToken).ConfigureAwait(false) is null
            && (await InstalledAsync(removal.GameId, cancellationToken).ConfigureAwait(false))
                .Any(p => string.Equals(p.PackVersion, pinned, StringComparison.OrdinalIgnoreCase)))
        {
            await _preferences
                .UpdateGameAsync(removal.GameId, current => current with { PinnedVersion = pinned }, cancellationToken)
                .ConfigureAwait(false);
        }

        // Only a record in XXSM's own folder: the path can come from the command line.
        if (skipped.Count == 0
            && removal.RecordPath is { Length: > 0 } record
            && UntrustedLocation.IsSameOrUnderExactly(Path.Combine(_paths.StateDirectory, RecordsDirectoryName), record)
            && File.Exists(record))
        {
            // File.Delete on purpose: XXSM's own bookkeeping, and what it describes is back.
            File.Delete(record);
        }

        return new PackRestoreResult { GameId = removal.GameId, Restored = restored, Skipped = skipped };
    }

    /// <inheritdoc />
    public async Task<PackGamePreference> KeepVersionAsync(
        string gameId, string packVersion, bool keep, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);

        var installed = await InstalledAsync(gameId, cancellationToken).ConfigureAwait(false);
        var version = Find(installed, gameId, packVersion).PackVersion;

        var saved = await _preferences
            .UpdateGameAsync(
                gameId,
                current => current with
                {
                    KeptVersions = keep
                        ? [.. current.KeptVersions.Where(v => !string.Equals(v, version, StringComparison.OrdinalIgnoreCase)), version]
                        : [.. current.KeptVersions.Where(v => !string.Equals(v, version, StringComparison.OrdinalIgnoreCase))],
                },
                cancellationToken)
            .ConfigureAwait(false);

        _logger.Information("{GameId} pack {Version} {Kept}", gameId, version, keep ? "is kept" : "is no longer kept");

        return saved.ForGame(gameId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OldPackVersion>> FindOldVersionsAsync(
        string? gameId = null, CancellationToken cancellationToken = default)
    {
        var preferences = await _preferences.ReadAsync(cancellationToken).ConfigureAwait(false);
        var days = preferences.RemoveOldVersionsAfterDays;

        if (days <= 0)
        {
            return [];
        }

        var cutoff = _time.GetUtcNow() - TimeSpan.FromDays(days);
        var due = new List<OldPackVersion>();

        var games = (await _installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => gameId is null || string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.GameId, StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            var versions = game.ToList();
            var choice = preferences.ForGame(game.Key);
            var inUse = ActivePackVersion.Choose([.. versions.Select(p => p.PackVersion)], choice.PinnedVersion);

            for (var i = versions.Count - 1; i >= 1; i--)
            {
                var version = versions[i];

                if (string.Equals(version.PackVersion, inUse, StringComparison.OrdinalIgnoreCase)
                    || choice.Keeps(version.PackVersion))
                {
                    continue;
                }

                // Never earlier than the day this version arrived.
                var replacedAt = InstalledAt(versions[i - 1]);

                if (replacedAt <= cutoff && InstalledAt(version) <= cutoff)
                {
                    due.Add(new OldPackVersion(version.GameId, version.DisplayName, version.PackVersion, replacedAt));
                }
            }
        }

        return due;
    }

    /// <inheritdoc />
    public async Task<PackPruneResult> RemoveOldVersionsAsync(
        string? gameId = null, CancellationToken cancellationToken = default)
    {
        var due = await FindOldVersionsAsync(gameId, cancellationToken).ConfigureAwait(false);
        var removed = new List<PackRemoval>();
        var failed = new List<PackPruneFailure>();

        foreach (var old in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                removed.Add(await RemoveVersionAsync(old.GameId, old.PackVersion, cancellationToken).ConfigureAwait(false));
            }
            catch (ModOperationException ex)
            {
                _logger.Warning(ex, "Could not move the old {GameId} pack {Version} to the trash", old.GameId, old.PackVersion);
                failed.Add(new PackPruneFailure(old.GameId, old.PackVersion, ex.Message));
            }
        }

        if (due.Count > 0)
        {
            _logger.Information(
                "Old pack versions: {Removed} moved to the trash, {Failed} could not be", removed.Count, failed.Count);
        }

        var days = (await _preferences.ReadAsync(cancellationToken).ConfigureAwait(false)).RemoveOldVersionsAfterDays;

        return new PackPruneResult(removed, failed) { AfterDays = days };
    }

    /// <summary>How long a working folder must be left alone before it counts as left behind.</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public IReadOnlyList<string> FindLeftovers()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - LeftoverAge;
        var found = new List<string>();

        void Take(IEnumerable<string> folders)
        {
            found.AddRange(folders.Where(folder => Directory.GetLastWriteTimeUtc(folder) <= cutoff).Select(PathComparer.Normalize));
        }

        try
        {
            if (Directory.Exists(_paths.PacksDirectory))
            {
                foreach (var game in Directory.EnumerateDirectories(_paths.PacksDirectory))
                {
                    Take(Directory.EnumerateDirectories(game).Where(folder => PackInstaller.IsWorkingFolder(Path.GetFileName(folder))));
                }
            }

            foreach (var name in new[] { PackInstaller.ImportStagingFolder, Xxsm.Core.Archives.ModArchiveReader.StagingFolderName })
            {
                var staging = Path.Combine(_paths.CacheDirectory, name);

                if (Directory.Exists(staging))
                {
                    Take(Directory.EnumerateDirectories(staging));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not look through the packs and cache folders for folders an install left behind");
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<PackLeftoversResult> RemoveLeftoversAsync(CancellationToken cancellationToken = default)
    {
        var removed = new List<string>();
        var failed = new List<PackLeftoverFailure>();

        foreach (var folder in FindLeftovers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var trashed = await _trash.TrashAsync(folder, _paths.DataDirectory, cancellationToken).ConfigureAwait(false);
                removed.Add(folder);

                _logger.Information(
                    "Moved a folder an interrupted install left behind to the trash: {From} -> {To}", folder, trashed.TrashedPath);
            }
            catch (Exception ex) when (ex is ModOperationException or IOException or UnauthorizedAccessException)
            {
                _logger.Warning(ex, "Could not move {Folder}, left behind by an interrupted install, to the trash", folder);
                failed.Add(new PackLeftoverFailure(folder, ex.Message));
            }
        }

        return new PackLeftoversResult(removed, failed);
    }

    /// <inheritdoc />
    public bool HasCorrections(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        return File.Exists(_overlays.GetOverlayPath(gameId)) || File.Exists(_overlays.GetBackupPath(gameId));
    }

    /// <inheritdoc />
    public async Task<PackRemoval> ReadRemovalAsync(string recordPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);

        var path = Path.GetFullPath(recordPath);

        try
        {
            await using var stream = File.OpenRead(path);
            var removal = await JsonSerializer
                .DeserializeAsync(stream, PackJsonContext.Default.PackRemoval, cancellationToken)
                .ConfigureAwait(false);

            return removal is { GameId.Length: > 0, Trashed: not null }
                ? removal with { RecordPath = path }
                : throw new ModOperationException($"'{PathDisplay.Show(path)}' is not a record of a removed pack.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ModOperationException($"Could not read the removed-pack record '{PathDisplay.Show(path)}': {ex.Message}", path, ex);
        }
    }

    /// <summary>When a version was installed: its folder's last change, never written after install.</summary>
    private static DateTimeOffset InstalledAt(InstalledPack pack) =>
        new(Directory.GetLastWriteTimeUtc(pack.Directory), TimeSpan.Zero);

    private async Task<List<InstalledPack>> InstalledAsync(string gameId, CancellationToken cancellationToken) =>
    [
        .. (await _installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase)),
    ];

    private static InstalledPack Find(List<InstalledPack> installed, string gameId, string packVersion) =>
        installed.FirstOrDefault(p => string.Equals(p.PackVersion, packVersion, StringComparison.OrdinalIgnoreCase))
        ?? throw new ModOperationException(
            installed.Count == 0
                ? $"No pack for '{gameId}' is installed."
                : $"{gameId} {packVersion} is not installed. Installed: {string.Join(", ", installed.Select(p => p.PackVersion))}.",
            gameId);

    private async Task<string?> PinnedAsync(string gameId, CancellationToken cancellationToken) =>
        (await _preferences.ReadAsync(cancellationToken).ConfigureAwait(false)).ForGame(gameId).PinnedVersion is { Length: > 0 } pinned
            ? pinned
            : null;

    /// <summary>Moves each path to the trash or none: a failure puts back the rest. Not cancellable.</summary>
    private async Task<List<TrashResult>> TrashAllAsync(IReadOnlyList<string> paths)
    {
        var trashed = new List<TrashResult>();

        try
        {
            foreach (var path in paths)
            {
                trashed.Add(await _trash.TrashAsync(path, _paths.DataDirectory, CancellationToken.None).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is ModOperationException or IOException or UnauthorizedAccessException)
        {
            foreach (var item in Enumerable.Reverse(trashed))
            {
                try
                {
                    await _trash.RestoreAsync(item, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ModOperationException restoreError)
                {
                    _logger.Error(restoreError, "Could not put {Path} back after a removal failed; it is in the trash at {Trashed}", item.OriginalPath, item.TrashedPath);
                }
            }

            throw;
        }

        return trashed;
    }

    /// <summary>Takes away a game's folder once its last version has gone; anything left in it keeps it.</summary>
    private void RemoveGameFolderIfEmpty(string gameId)
    {
        var folder = Path.Combine(_paths.PacksDirectory, gameId);

        if (PathComparer.TryResolveExisting(folder, out var existing)
            && Directory.Exists(existing)
            && !Directory.EnumerateFileSystemEntries(existing).Any())
        {
            Directory.Delete(existing);
        }
    }

    /// <summary>Keeps the removal where a later process can find it; a failed write is logged, not fatal.</summary>
    private async Task<PackRemoval> RecordAsync(PackRemoval removal)
    {
        var directory = Path.Combine(_paths.StateDirectory, RecordsDirectoryName, removal.GameId);
        var stem = removal.RemovedAt.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                   + (removal.PackVersion is { Length: > 0 } version ? "-" + version : string.Empty);
        var path = Path.Combine(directory, stem + ".json");

        try
        {
            Directory.CreateDirectory(directory);

            // Two removals in one millisecond each keep their own record.
            for (var n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.json"));
            }

            // Not cancellable: the pack has already gone to the trash, and this is how it comes back.
            await AtomicFile.WriteAsync(
                    path,
                    (stream, token) => JsonSerializer.SerializeAsync(stream, removal, PackJsonContext.Default.PackRemoval, token),
                    AtomicWriteOptions.DurableOnly,
                    CancellationToken.None)
                .ConfigureAwait(false);

            _logger.Information("Wrote the restore record for the {GameId} removal to {Path}", removal.GameId, path);

            return removal with { RecordPath = path };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not write the restore record for the {GameId} removal at {Path}", removal.GameId, path);
            return removal;
        }
    }
}
