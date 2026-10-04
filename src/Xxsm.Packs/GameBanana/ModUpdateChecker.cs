using Serilog;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;

namespace Xxsm.Packs.GameBanana;

/// <summary>What is known about one mod's version against its page.</summary>
/// <param name="ModFolder">The mod's own folder.</param>
/// <param name="DisplayName">What to call it in a message.</param>
/// <param name="ModId">Its GameBanana mod id.</param>
/// <param name="InstalledVersion">The version string recorded when it was installed or linked.</param>
/// <param name="LatestVersion">The version string its page carries now, when it was asked.</param>
/// <param name="LatestModified">When its page was last changed, when it was asked.</param>
/// <param name="HasUpdate">Whether the page has changed since XXSM last agreed with it.</param>
/// <param name="Error">Why this mod could not be checked, or null.</param>
public sealed record ModUpdateStatus(
    string ModFolder,
    string DisplayName,
    long ModId,
    string? InstalledVersion,
    string? LatestVersion,
    DateTimeOffset? LatestModified,
    bool HasUpdate,
    string? Error = null)
{
    /// <summary>The mod's page.</summary>
    public Uri PageUrl => new(GameBananaUrl.ForMod(ModId));

    /// <summary>The version change, written out: <c>1.2 → 1.4</c>, or just the new one.</summary>
    public string? VersionChange => (InstalledVersion, LatestVersion) switch
    {
        ({ Length: > 0 } from, { Length: > 0 } to) when !string.Equals(from, to, StringComparison.Ordinal) =>
            $"{from} → {to}",
        (_, { Length: > 0 } to) => to,
        ({ Length: > 0 } from, _) => from,
        _ => null,
    };
}

/// <summary>What one run of the update checker found.</summary>
public sealed record ModUpdateReport
{
    /// <summary>When the run finished.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Every mod that was asked about.</summary>
    public required IReadOnlyList<ModUpdateStatus> Checked { get; init; }

    /// <summary>Every mod known to have an update, including ones found by an earlier run.</summary>
    public required IReadOnlyList<ModUpdateStatus> Updates { get; init; }

    /// <summary>The updates this run found that no earlier run had marked: the ones worth a notice.</summary>
    public IReadOnlyList<ModUpdateStatus> NewlyFound { get; init; } = [];

    /// <summary>How many linked mods were not due to be checked yet.</summary>
    public required int NotDue { get; init; }

    /// <summary>How many mods could not be checked, each with its reason on its own status.</summary>
    public IReadOnlyList<ModUpdateStatus> Failures => [.. Checked.Where(status => status.Error is not null)];

    /// <summary>How many mods in the folder are linked to a GameBanana page at all.</summary>
    public required int Linked { get; init; }

    /// <summary>Whether anything has an update.</summary>
    public bool HasUpdates => Updates.Count > 0;
}

/// <summary>Compares installed mods against their GameBanana pages. Never downloads anything.</summary>
public interface IModUpdateChecker
{
    /// <summary>Asks GameBanana about every linked mod that is due; one failing mod does not stop the rest.</summary>
    /// <param name="modsDirectory">The game's Mods folder.</param>
    /// <param name="force">True to ignore the interval and the cache, though never sooner than
    /// <see cref="ModUpdateChecker.RecheckFloor"/>.</param>
    /// <param name="progress">Told each mod's name as it is asked about.</param>
    /// <param name="cancellationToken">Cancels the run. Whatever was already written stays written.</param>
    /// <exception cref="GameBananaDisabledException">GameBanana support is switched off.</exception>
    /// <exception cref="Xxsm.Core.ModOperationException">The Mods folder could not be read.</exception>
    Task<ModUpdateReport> CheckAsync(
        string modsDirectory,
        bool force = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the marks left by previous runs, without the network; works with GameBanana off.</summary>
    Task<IReadOnlyList<ModUpdateStatus>> ReadAsync(
        string modsDirectory, CancellationToken cancellationToken = default);

    /// <summary>Skips the update: clears the mark and accepts the page as it is now, so only a later change marks it again.</summary>
    Task<bool> AcceptAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Marks a linked mod as having an update without asking GameBanana: a tool to see the mark.</summary>
    /// <param name="modFolder">The mod's own folder. It must already be linked to a page.</param>
    /// <param name="latestVersion">What to show as the newer version, or null for none.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <returns>False when the mod is not linked to a GameBanana page.</returns>
    /// <exception cref="GameBananaDeveloperToolsOffException"><c>gameBanana.developerTools</c> is not switched
    /// on.</exception>
    Task<bool> PretendAsync(
        string modFolder, string? latestVersion, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModUpdateChecker"/>.</summary>
public sealed class ModUpdateChecker(
    IModRepository mods,
    IModConfigStore configs,
    IGameBananaClient client,
    IAppSettingsStore settings,
    ILogger logger,
    TimeProvider? time = null) : IModUpdateChecker
{
    /// <summary>The least time between two questions about one mod, even when the user presses Check now.</summary>
    public static TimeSpan RecheckFloor { get; } = TimeSpan.FromMinutes(30);

    private readonly IModRepository _mods = mods;
    private readonly IModConfigStore _configs = configs;
    private readonly IGameBananaClient _client = client;
    private readonly IAppSettingsStore _settings = settings;
    private readonly ILogger _logger = logger.ForContext<ModUpdateChecker>();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ModUpdateReport> CheckAsync(
        string modsDirectory,
        bool force = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.GameBanana.Enabled)
        {
            throw new GameBananaDisabledException();
        }

        var linked = await LinkedAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
        var interval = settings.GameBanana.CheckInterval;
        var now = _time.GetUtcNow();

        var checkedMods = new List<ModUpdateStatus>();
        var updates = new List<ModUpdateStatus>();
        var newlyFound = new List<ModUpdateStatus>();
        var notDue = 0;

        foreach (var mod in linked)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = mod.Config?.GameBanana;
            var modId = info?.ModId ?? 0;

            // A check dated in the future is no check, or the mod would never be asked again.
            if (info?.LastChecked is { } last && last <= now && now - last < (force ? RecheckFloor : interval))
            {
                notDue++;

                // A mod a previous run marked is still an update, though this run did not ask.
                if (info.UpdateAvailable == true)
                {
                    updates.Add(new ModUpdateStatus(
                        mod.Path,
                        mod.DisplayName,
                        modId,
                        mod.Config?.Version,
                        info.LatestVersion,
                        null,
                        true));
                }

                continue;
            }

            progress?.Report(mod.DisplayName);

            ModUpdateStatus status;

            try
            {
                var page = await _client.GetModAsync(modId, force, cancellationToken).ConfigureAwait(false);
                var hasUpdate = IsNewer(info, mod.Config?.Version, page);

                status = new ModUpdateStatus(
                    mod.Path,
                    mod.DisplayName,
                    modId,
                    mod.Config?.Version,
                    page.Version,
                    page.DateModified,
                    hasUpdate);

                await MarkAsync(mod.Path, hasUpdate, page.Version, page.DateModifiedTs, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GameBananaDisabledException)
            {
                break;
            }
            catch (Exception ex) when (ex is Xxsm.Core.XxsmException or IOException or UnauthorizedAccessException
                                           or HttpRequestException)
            {
                // One mod that cannot be checked is that mod's line in the report, not the end of the run.
                status = new ModUpdateStatus(
                    mod.Path, mod.DisplayName, modId, mod.Config?.Version, null, null, false, ex.Message);

                _logger.Warning(ex, "Could not check mod {ModId} at {Path} for updates", modId, mod.Path);
            }

            checkedMods.Add(status);

            if (status.HasUpdate)
            {
                updates.Add(status);

                if (info?.UpdateAvailable != true)
                {
                    newlyFound.Add(status);
                }
            }
        }

        _logger.Information(
            "Checked {Checked} of {Linked} linked mod(s) for updates: {Updates} with a newer version, " +
            "{NotDue} not due, {Failed} could not be asked",
            checkedMods.Count,
            linked.Count,
            updates.Count,
            notDue,
            checkedMods.Count(status => status.Error is not null));

        return new ModUpdateReport
        {
            At = _time.GetUtcNow(),
            Checked = checkedMods,
            Updates = updates,
            NewlyFound = newlyFound,
            NotDue = notDue,
            Linked = linked.Count,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModUpdateStatus>> ReadAsync(
        string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var linked = await LinkedAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

        return
        [
            .. linked.Select(mod => new ModUpdateStatus(
                mod.Path,
                mod.DisplayName,
                mod.Config?.GameBanana?.ModId ?? 0,
                mod.Config?.Version,
                mod.Config?.GameBanana?.LatestVersion,
                null,
                mod.Config?.GameBanana?.UpdateAvailable == true)),
        ];
    }

    /// <inheritdoc />
    public async Task<bool> AcceptAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        var existing = await _configs.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);

        if (existing?.GameBanana is not { } info || info.UpdateAvailable != true)
        {
            return false;
        }

        // Now becomes the baseline: the page as it is is accepted, and only a later change marks it again.
        var now = _time.GetUtcNow();
        await _configs.UpdateAsync(
            modFolder,
            config => config with
            {
                GameBanana = (config.GameBanana ?? new ModGameBananaInfo()) with
                {
                    UpdateAvailable = false,
                    LatestVersion = null,
                    LastChecked = now,
                    DateModifiedTs = now.ToUnixTimeSeconds(),
                },
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information("Accepted the installed version of the mod at {Path} as current", modFolder);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> PretendAsync(
        string modFolder, string? latestVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.GameBanana.DeveloperTools)
        {
            throw new GameBananaDeveloperToolsOffException();
        }

        var existing = await _configs.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);

        if (existing?.GameBanana?.ModId is not > 0)
        {
            return false;
        }

        // Not LastChecked: this was not a check.
        await _configs.UpdateAsync(
            modFolder,
            config => config with
            {
                GameBanana = (config.GameBanana ?? new ModGameBananaInfo()) with
                {
                    UpdateAvailable = true,
                    LatestVersion = latestVersion is { Length: > 0 } ? latestVersion : null,
                },
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Marked the mod at {Path} as having an update, without asking GameBanana (developer tools)",
            modFolder);

        return true;
    }

    /// <summary>Whether a page has moved on from what was recorded, by timestamp first; no baseline is no.</summary>
    internal static bool IsNewer(ModGameBananaInfo? info, string? installedVersion, GameBananaMod page)
    {
        if (info?.DateModifiedTs is { } recorded && page.DateModifiedTs is { } current)
        {
            return current > recorded;
        }

        return installedVersion is { Length: > 0 } installed
            && page.Version is { Length: > 0 } latest
            && !string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
    }

    private async Task MarkAsync(
        string modFolder,
        bool hasUpdate,
        string? latestVersion,
        long? pageModifiedTs,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await _configs.UpdateAsync(
                modFolder,
                config => config with
                {
                    GameBanana = (config.GameBanana ?? new ModGameBananaInfo()) with
                    {
                        LastChecked = now,
                        UpdateAvailable = hasUpdate,
                        LatestVersion = hasUpdate ? latestVersion : null,

                        // Linked by address alone: the first check is the baseline.
                        DateModifiedTs = config.GameBanana?.DateModifiedTs ?? pageModifiedTs,
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Xxsm.Core.ModOperationException ex)
        {
            _logger.Warning(ex, "Checked the mod at {Path} but could not record the result", modFolder);
        }
    }

    private async Task<IReadOnlyList<InstalledMod>> LinkedAsync(
        string modsDirectory, CancellationToken cancellationToken)
    {
        var inventory = await _mods.ScanAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

        return
        [
            .. inventory.VariantFolders
                .SelectMany(folder => folder.Mods)
                .Concat(inventory.UnfiledMods)
                .Where(mod => mod.Config?.GameBanana?.ModId is > 0)
                .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
