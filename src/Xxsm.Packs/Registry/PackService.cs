using System.Globalization;
using System.Security.Cryptography;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Overlays;

namespace Xxsm.Packs.Registry;

/// <summary>The default <see cref="IPackService"/>.</summary>
public sealed class PackService(
    IRegistryClient registry,
    IPackResourceFetcher fetcher,
    IPackInstaller installer,
    IPackPreferencesStore preferences,
    IGamePackLoader loader,
    IOverlayStore overlays,
    PackUpdatePlanner planner,
    ISkippedUpdatesStore skipped,
    IPackChangesComparer changesComparer,
    IPackChangesStore changes,
    IAppPaths paths,
    ILogger logger) : IPackService
{
    private readonly IRegistryClient _registry = registry;
    private readonly IPackResourceFetcher _fetcher = fetcher;
    private readonly IPackInstaller _installer = installer;
    private readonly IPackPreferencesStore _preferences = preferences;
    private readonly IGamePackLoader _loader = loader;
    private readonly IOverlayStore _overlays = overlays;
    private readonly PackUpdatePlanner _planner = planner;
    private readonly ISkippedUpdatesStore _skipped = skipped;
    private readonly IPackChangesComparer _changesComparer = changesComparer;
    private readonly IPackChangesStore _changes = changes;
    private readonly IAppPaths _paths = paths;
    private readonly ILogger _logger = logger.ForContext<PackService>();

    /// <inheritdoc />
    public async Task<PackCatalogResult> GetCatalogAsync(
        IReadOnlyList<string>? registries = null,
        CancellationToken cancellationToken = default)
    {
        var preferences = await _preferences.ReadAsync(cancellationToken).ConfigureAwait(false);
        var sources = ResolveRegistries(registries, preferences);
        var installed = await _installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false);

        var failures = new List<RegistryFailure>();

        // Keyed by game id; later registries win ties.
        var byGame = new Dictionary<string, (RegistryPack Pack, string Registry)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fetch = await _registry.FetchAsync(source, cancellationToken).ConfigureAwait(false);

                foreach (var pack in fetch.Index.Packs)
                {
                    if (pack.GameId is { Length: > 0 })
                    {
                        byGame[pack.GameId] = (Resolve(pack, fetch.IndexUri), source);
                    }
                }
            }
            catch (PackRegistryException ex)
            {
                // Not fatal: the catalogue still comes from the other registries and what is installed.
                _logger.Warning("Registry {Registry} could not be read: {Reason}", source, ex.Message);
                failures.Add(new RegistryFailure(source, ex.Message));
            }
        }

        var games = byGame.Keys
            .Concat(installed.Select(pack => pack.GameId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(game => game, StringComparer.OrdinalIgnoreCase);

        var entries = new List<PackCatalogEntry>();

        foreach (var gameId in games)
        {
            byGame.TryGetValue(gameId, out var found);

            var versions = (found.Pack?.Versions ?? [])
                .OrderByDescending(version => version.PackVersion, PackVersionOrder.Instance)
                .Select(Judge)
                .ToList();

            var installedVersions = installed
                .Where(pack => string.Equals(pack.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                .Select(pack => pack.PackVersion)
                .ToList();

            entries.Add(new PackCatalogEntry
            {
                GameId = gameId,
                DisplayName = found.Pack?.DisplayName is { Length: > 0 } published
                    ? published
                    : installedVersions.Count > 0
                        ? InstalledDisplayName(installed, gameId) ?? gameId
                        : gameId,
                Registry = found.Registry,
                Versions = versions,
                InstalledVersions = installedVersions,
                ActiveVersion = ActivePackVersion.Choose(installedVersions, preferences.ForGame(gameId).PinnedVersion),
                Preference = preferences.ForGame(gameId),
            });
        }

        return new PackCatalogResult(entries, failures)
        {
            RemoveOldVersionsAfterDays = preferences.RemoveOldVersionsAfterDays,
            AutoUpdate = preferences.AutoUpdate,
        };
    }

    /// <inheritdoc />
    public async Task<PackOperationResult> InstallAsync(
        string gameId,
        string? packVersion = null,
        IReadOnlyList<string>? registries = null,
        bool overwrite = false,
        IProgress<PackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var catalog = await GetCatalogAsync(registries, cancellationToken).ConfigureAwait(false);

        var entry = catalog.Find(gameId)
                    ?? throw NoSuchGame(gameId, catalog);

        var target = packVersion is { Length: > 0 }
            ? entry.Versions.FirstOrDefault(version =>
                  string.Equals(version.PackVersion, packVersion, StringComparison.OrdinalIgnoreCase))
              ?? throw new PackRegistryException(
                  $"No version '{packVersion}' of the {gameId} pack is published. " +
                  $"Available: {Describe(entry)}")
            : entry.TargetVersion;

        if (target is null)
        {
            return new PackOperationResult
            {
                GameId = gameId,
                Outcome = PackOperationOutcome.NothingAvailable,
                Message = entry.Versions.Count == 0
                    ? $"No {gameId} pack is published by the registries XXSM is using."
                    : $"No published {gameId} pack can be read by this version of XXSM. {Describe(entry)}",
            };
        }

        if (!target.IsInstallable)
        {
            throw new PackRegistryException(
                $"{gameId} {target.PackVersion} cannot be installed: {target.RefusalReason}");
        }

        if (!overwrite &&
            entry.InstalledVersions.Contains(target.PackVersion, StringComparer.OrdinalIgnoreCase))
        {
            return new PackOperationResult
            {
                GameId = gameId,
                Outcome = PackOperationOutcome.AlreadyCurrent,
                PackVersion = target.PackVersion,
                Message = $"{gameId} {target.PackVersion} is already installed.",
            };
        }

        return await DownloadAndInstallAsync(entry, target, overwrite, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PackOperationResult>> UpdateAsync(
        string? gameId = null,
        IReadOnlyList<string>? registries = null,
        CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(registries, cancellationToken).ConfigureAwait(false);

        var considered = gameId is { Length: > 0 }
            ? [catalog.Find(gameId) ?? throw NoSuchGame(gameId, catalog)]
            : catalog.Entries.Where(entry => entry.IsInstalled).ToList();

        var results = new List<PackOperationResult>(considered.Count);

        foreach (var entry in considered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(await UpdateOneAsync(entry, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<PackGamePreference> PinAsync(
        string gameId, string? packVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var saved = await _preferences
            .UpdateGameAsync(
                gameId,
                current => current with { PinnedVersion = packVersion is { Length: > 0 } ? packVersion : null },
                cancellationToken)
            .ConfigureAwait(false);

        _logger.Information(
            "{GameId} is now {State}",
            gameId,
            packVersion is { Length: > 0 } ? $"pinned to {packVersion}" : "unpinned");

        return saved.ForGame(gameId);
    }

    /// <inheritdoc />
    public async Task<PackContents?> ReadContentsAsync(string gameId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        if (await _installer.FindActivePackDirectoryAsync(gameId, cancellationToken).ConfigureAwait(false) is not { } directory)
        {
            return null;
        }

        return PackContents.Of(await _loader.LoadAsync(directory, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<PackPreferences> SetAutoUpdateAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var current = await _preferences.ReadAsync(cancellationToken).ConfigureAwait(false);
        var saved = current with { StoredAutoUpdate = enabled };

        await _preferences.WriteAsync(saved, cancellationToken).ConfigureAwait(false);

        _logger.Information("Automatic pack updates are now {State}", enabled ? "on" : "off");

        return saved;
    }

    private async Task<PackOperationResult> UpdateOneAsync(
        PackCatalogEntry entry, CancellationToken cancellationToken)
    {
        // A pin is reported, not skipped silently; the automatic-updates switch has no say here.
        if (entry.Preference.PinnedVersion is { Length: > 0 } pinned &&
            entry.InstalledVersions.Contains(pinned, StringComparer.OrdinalIgnoreCase))
        {
            return new PackOperationResult
            {
                GameId = entry.GameId,
                Outcome = PackOperationOutcome.Pinned,
                PackVersion = entry.ActiveVersion,
                Message = $"{entry.GameId}: pinned to {pinned}.",
            };
        }

        var target = entry.TargetVersion;

        if (target is null || !target.IsInstallable)
        {
            return new PackOperationResult
            {
                GameId = entry.GameId,
                Outcome = PackOperationOutcome.NothingAvailable,
                PackVersion = entry.ActiveVersion,
                Message = target is null
                    ? $"{entry.GameId}: nothing newer is published."
                    : $"{entry.GameId}: {target.PackVersion} needs a newer XXSM. {target.RefusalReason}",
            };
        }

        if (!entry.UpdateAvailable)
        {
            return new PackOperationResult
            {
                GameId = entry.GameId,
                Outcome = PackOperationOutcome.AlreadyCurrent,
                PackVersion = entry.ActiveVersion,
                Message = $"{entry.GameId} {entry.ActiveVersion} is up to date.",
            };
        }

        return await DownloadAndInstallAsync(entry, target, overwrite: false, progress: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PackOperationResult> ImportAsync(
        string source,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        // Which game the file is for is known only once read, so note every game's active pack.
        var before = new Dictionary<string, (string Directory, string Version)>(StringComparer.OrdinalIgnoreCase);

        foreach (var gameId in (await _installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false))
                     .Select(p => p.GameId)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await _installer.FindActivePackDirectoryAsync(gameId, cancellationToken).ConfigureAwait(false) is { } directory)
            {
                var pack = await _loader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
                before[gameId] = (directory, pack.PackVersion);
            }
        }

        var installed = await _installer.InstallAsync(source, overwrite, cancellationToken: cancellationToken).ConfigureAwait(false);

        GamePack? currentPack = null;
        string? previousVersion = null;

        if (before.TryGetValue(installed.GameId, out var previous))
        {
            previousVersion = previous.Version;

            currentPack = await _loader.LoadAsync(previous.Directory, cancellationToken).ConfigureAwait(false);
        }

        var (plan, changes) = await ApplyOverlayAsync(installed.GameId, currentPack, installed, cancellationToken)
            .ConfigureAwait(false);

        await UseInstalledAsync(installed.GameId, installed.PackVersion, cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Imported {GameId} {Version} from {Source} into {Directory}",
            installed.GameId,
            installed.PackVersion,
            source,
            installed.Directory);

        return new PackOperationResult
        {
            GameId = installed.GameId,
            Outcome = PackOperationOutcome.Installed,
            PackVersion = installed.PackVersion,
            PreviousVersion = previousVersion,
            Directory = installed.Directory,
            Message = previousVersion is { Length: > 0 } from && !string.Equals(from, installed.PackVersion, StringComparison.Ordinal)
                ? $"{installed.GameId} updated from {from} to {installed.PackVersion}."
                : $"{installed.GameId} {installed.PackVersion} installed.",
            Changes = changes,
            SkippedUpdates = plan?.SkippedUpdates ?? [],
            AdoptionCandidates = plan?.AdoptionCandidates ?? [],
            Installed = installed,
        };
    }

    /// <summary>Makes a version installed from a file the one in use, since a file is chosen by hand.</summary>
    private async Task UseInstalledAsync(string gameId, string packVersion, CancellationToken cancellationToken)
    {
        var installed = (await _installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => string.Equals(p.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PackVersion)
            .ToList();

        var wanted = ActivePackVersion.PinToUse(installed, packVersion);
        var pinned = (await _preferences.ReadAsync(cancellationToken).ConfigureAwait(false)).ForGame(gameId).PinnedVersion;

        if (!string.Equals(wanted, pinned is { Length: > 0 } ? pinned : null, StringComparison.OrdinalIgnoreCase))
        {
            await PinAsync(gameId, wanted, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PackOperationResult> DownloadAndInstallAsync(
        PackCatalogEntry entry,
        PackCatalogVersion target,
        bool overwrite,
        IProgress<PackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!GamePackLoader.IsValidId(entry.GameId) || !GamePackLoader.IsValidVersion(target.PackVersion))
        {
            throw new PackRegistryException(
                $"The registry lists '{entry.GameId}' version '{target.PackVersion}', which cannot safely " +
                "name a file: a game id may hold only letters, digits, underscores and hyphens, and a " +
                "version only those and dots. Nothing was downloaded.");
        }

        var archive = Path.Combine(
            _paths.DownloadsDirectory,
            $"{entry.GameId}-{target.PackVersion}.zip");

        var uri = ResolveDownloadUri(entry, target);

        long? published = target.Version.SizeBytes > 0 ? target.Version.SizeBytes : null;
        progress?.Report(new PackInstallProgress(PackInstallStage.Downloading, 0, published));

        var downloaded = progress is null
            ? null
            : new Relay<(long Done, long? Total)>(step =>
                progress.Report(new PackInstallProgress(PackInstallStage.Downloading, step.Done, step.Total ?? published)));

        try
        {
            var bytes = await _fetcher
                .DownloadAsync(uri, archive, downloaded, published, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new PackInstallProgress(PackInstallStage.Checking, bytes, bytes));

            // Verified before install, never after.
            await VerifyChecksumAsync(archive, target, uri, cancellationToken).ConfigureAwait(false);

            var previous = entry.ActiveVersion is { Length: > 0 }
                ? await _installer.FindActivePackDirectoryAsync(entry.GameId, cancellationToken)
                    .ConfigureAwait(false)
                : null;

            var currentPack = previous is { Length: > 0 }
                ? await _loader.LoadAsync(previous, cancellationToken).ConfigureAwait(false)
                : null;

            progress?.Report(new PackInstallProgress(PackInstallStage.Installing, bytes, bytes));

            // The archive must be the pack the registry said it was.
            var installed = await _installer
                .InstallAsync(archive, overwrite, new ExpectedPack(entry.GameId, target.PackVersion), cancellationToken)
                .ConfigureAwait(false);

            var (plan, changes) = await ApplyOverlayAsync(entry.GameId, currentPack, installed, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information(
                "Installed {GameId} {Version} from {Uri} ({Bytes} bytes)",
                installed.GameId,
                installed.PackVersion,
                uri,
                bytes);

            return new PackOperationResult
            {
                GameId = installed.GameId,
                Outcome = PackOperationOutcome.Installed,
                PackVersion = installed.PackVersion,
                PreviousVersion = entry.ActiveVersion,
                Directory = installed.Directory,
                Message = entry.ActiveVersion is { Length: > 0 } from
                    ? $"{installed.GameId} updated from {from} to {installed.PackVersion}."
                    : $"{installed.GameId} {installed.PackVersion} installed.",
                Changes = changes,
                SkippedUpdates = plan?.SkippedUpdates ?? [],
                AdoptionCandidates = plan?.AdoptionCandidates ?? [],
            };
        }
        finally
        {
            OwnScratch.TryDeleteFile(archive, _logger);
        }
    }

    /// <summary>Runs the update planner and saves the result, so an update never changes an edited variant.</summary>
    private async Task<(PackUpdatePlan? Plan, PackChanges? Changes)> ApplyOverlayAsync(
        string gameId,
        GamePack? currentPack,
        PackInstallResult installed,
        CancellationToken cancellationToken)
    {
        var newPack = await _loader.LoadAsync(installed.Directory, cancellationToken).ConfigureAwait(false);

        // One turn with every other change to this overlay: a character edit saved meanwhile must survive.
        var plan = await _overlays.UpdateAsync(
            gameId,
            overlay =>
            {
                var planned = _planner.Plan(currentPack, newPack, overlay);
                var hasSomethingToSay = planned.SkippedUpdates.Count > 0 || planned.AdoptionCandidates.Count > 0;

                return hasSomethingToSay || File.Exists(_overlays.GetOverlayPath(gameId))
                    ? OverlayUpdate.Write(planned.Overlay, planned)
                    : OverlayUpdate.Keep(planned);
            },
            cancellationToken).ConfigureAwait(false);

        // Recorded for the review; an update that withholds nothing clears the old record.
        await _skipped
            .SaveAsync(
                new SkippedUpdateRecord
                {
                    GameId = gameId,
                    PackVersion = installed.PackVersion,
                    RecordedAt = DateTimeOffset.UtcNow,
                    Updates = plan.SkippedUpdates,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return (plan, await RecordChangesAsync(gameId, currentPack, newPack, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Works out and keeps what an update changed; a first install clears the list.</summary>
    private async Task<PackChanges?> RecordChangesAsync(
        string gameId, GamePack? currentPack, GamePack newPack, CancellationToken cancellationToken)
    {
        if (currentPack is null)
        {
            await _changes.ClearAsync(gameId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (string.Equals(currentPack.PackVersion, newPack.PackVersion, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var changes = await _changesComparer.CompareAsync(currentPack, newPack, cancellationToken).ConfigureAwait(false);
        await _changes.SaveAsync(changes, cancellationToken).ConfigureAwait(false);
        return changes;
    }

    private static async Task VerifyChecksumAsync(
        string archive, PackCatalogVersion target, Uri uri, CancellationToken cancellationToken)
    {
        // Mandatory: a pack source that leaves the checksum out cannot switch the check off.
        if (target.Version.Sha256 is not { Length: > 0 } expected)
        {
            throw new PackRegistryException(
                $"The pack source published no checksum for {target.PackVersion}, so the download cannot be " +
                "checked. Nothing has been installed.");
        }

        await using var stream = File.OpenRead(archive);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);

        if (!string.Equals(actual, expected.Replace("sha256:", null), StringComparison.OrdinalIgnoreCase))
        {
            throw new PackRegistryException(
                $"The download of {target.PackVersion} does not match the checksum the registry " +
                $"published. Expected {expected}, got {actual.ToLower(CultureInfo.InvariantCulture)}. " +
                "Nothing has been installed.");
        }
    }

    /// <summary>Where a version downloads from: https, or a file when the pack source is a folder here.</summary>
    private Uri ResolveDownloadUri(PackCatalogEntry entry, PackCatalogVersion target)
    {
        var index = entry.Registry is { Length: > 0 } registry ? _registry.ResolveIndexUri(registry) : null;
        Uri uri;

        try
        {
            uri = Uri.TryCreate(target.Version.Url, UriKind.Absolute, out var absolute)
                ? absolute
                : index is not null
                    ? new Uri(index, target.Version.Url)
                    : throw new PackRegistryException(
                        $"'{target.Version.Url}' is a relative address and there is no registry to resolve it against.");
        }
        catch (UriFormatException ex)
        {
            throw new PackRegistryException(
                $"The pack source gives '{target.Version.Url}' as the address of {target.PackVersion}, which is not " +
                "an address. Nothing was downloaded.",
                ex);
        }

        if (uri.IsFile)
        {
            return index?.IsFile == true
                ? uri
                : throw new PackRegistryException(
                    $"The pack source on the web gives a file on this computer ('{PathDisplay.Show(uri.LocalPath)}') as the address of " +
                    $"{target.PackVersion}. Nothing was downloaded.");
        }

        return Xxsm.Core.Io.UntrustedLocation.IsWebAddress(uri.OriginalString, out _)
            ? uri
            : throw new PackRegistryException(
                $"The pack source gives '{uri}' as the address of {target.PackVersion}; XXSM downloads packs only " +
                "over https. Nothing was downloaded.");
    }

    /// <summary>Rewrites a pack's relative version URLs against the index they came from.</summary>
    private static RegistryPack Resolve(RegistryPack pack, Uri indexUri) =>
        pack with
        {
            // An address that cannot be read is left as it is; installing that version then refuses it.
            Versions = [.. pack.Versions.Select(version =>
                Uri.TryCreate(version.Url, UriKind.Absolute, out _) || !Uri.TryCreate(indexUri, version.Url, out var resolved)
                    ? version
                    : version with { Url = resolved.ToString() })],
        };

    private static PackCatalogVersion Judge(RegistryPackVersion version)
    {
        if (!PackSchema.IsSupported(version.PackSchemaVersion))
        {
            return new PackCatalogVersion(
                version,
                PackAvailability.UnsupportedSchema,
                $"It is written in pack format {version.PackSchemaVersion}, and this XXSM reads " +
                $"format {string.Join(" and ", PackSchema.SupportedVersions)}. Update XXSM to use it.");
        }

        if (version.MinAppVersion is { Length: > 0 } minimum
            && AppVersionRequirement.IsOlderThan(AppInfo.ShortVersion, minimum))
        {
            return new PackCatalogVersion(
                version,
                PackAvailability.NeedsNewerApp,
                $"It needs XXSM {minimum} or newer, and this is {AppInfo.ShortVersion}.");
        }

        return new PackCatalogVersion(version, PackAvailability.Installable, null);
    }

    /// <summary>Which registries to consult: what the caller named, else the user's list, else the default.</summary>
    /// <param name="explicitly">The caller's choice: null for the configured ones, empty for none at all.</param>
    /// <param name="preferences">The user's saved registry list.</param>
    /// <returns>The registries to fetch, possibly none.</returns>
    private static IReadOnlyList<string> ResolveRegistries(
        IReadOnlyList<string>? explicitly, PackPreferences preferences)
    {
        if (explicitly is not null)
        {
            return explicitly;
        }

        return preferences.Registries.Count > 0
            ? preferences.Registries
            : [AppInfo.DefaultRegistryUrl];
    }

    /// <summary>The display name an installed pack carries for a game, if any.</summary>
    private static string? InstalledDisplayName(
        IReadOnlyList<InstalledPack> installed, string gameId) =>
        installed
            .Where(pack => string.Equals(pack.GameId, gameId, StringComparison.OrdinalIgnoreCase))
            .Select(pack => pack.DisplayName)
            .FirstOrDefault(name => name is { Length: > 0 });

    private static PackRegistryException NoSuchGame(string gameId, PackCatalogResult catalog)
    {
        var known = catalog.Entries.Select(entry => entry.GameId).ToList();

        var suffix = known.Count > 0
            ? $" The registries offer: {string.Join(", ", known)}."
            : catalog.Failures.Count > 0
                ? " No registry could be reached, so there is nothing to install from."
                : " The registries offer no packs at all.";

        return new PackRegistryException($"There is no pack for '{gameId}'.{suffix}");
    }

    private static string Describe(PackCatalogEntry entry) =>
        entry.Versions.Count == 0
            ? "It publishes no versions."
            : "Published versions: " + string.Join(
                ", ",
                entry.Versions.Select(version => version.IsInstallable
                    ? version.PackVersion
                    : $"{version.PackVersion} (unusable)"));

    /// <summary>Passes each report straight on, on its own thread, so they cannot arrive out of order.</summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
