using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Xxsm.Core;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels;

/// <summary>When the app looks for newer packs, and a newer XXSM, by itself.</summary>
/// <param name="StartOnLaunch">Whether the check starts with the window; tests call it directly.</param>
/// <param name="Interval">How long between checks while the app stays open.</param>
public sealed record UpdateSchedule(bool StartOnLaunch, TimeSpan Interval)
{
    /// <summary>At start, then every 24 hours.</summary>
    public static UpdateSchedule Daily { get; } = new(true, TimeSpan.FromHours(24));

    /// <summary>Never by itself; only when a watcher's <c>CheckAsync</c> is called.</summary>
    public static UpdateSchedule Manual { get; } = new(false, TimeSpan.FromHours(24));
}

/// <summary>Looks for newer packs at start and daily; installs them if updates are automatic, else says so.</summary>
/// <remarks>A pinned game is never touched; an unreachable source is only logged.</remarks>
public sealed partial class PackUpdateWatcher(
    IPackService packs,
    PackInstallFollowUp followUp,
    INotificationService notifications,
    ITextCatalogue text,
    UpdateSchedule schedule,
    TimeProvider time,
    ILogger logger,
    Func<CancellationToken, Task> reloadGames,
    Action showPacks) : ObservableObject, IDisposable
{
    private readonly IPackService _packs = packs;
    private readonly PackInstallFollowUp _followUp = followUp;
    private readonly INotificationService _notifications = notifications;
    private readonly ITextCatalogue _text = text;
    private readonly UpdateSchedule _schedule = schedule;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<PackUpdateWatcher>();
    private readonly Func<CancellationToken, Task> _reloadGames = reloadGames;
    private readonly Action _showPacks = showPacks;

    // Versions already announced, so a daily check does not repeat itself.
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Notification> _readyNotices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    /// <summary>How many installed games have a newer pack waiting for <em>Update</em>: the Packs badge.</summary>
    [ObservableProperty]
    private int _waitingCount;

    /// <summary>Starts looking: now, then every interval. Once; later calls do nothing.</summary>
    public void Start()
    {
        if (!_schedule.StartOnLaunch || _loop is not null)
        {
            return;
        }

        _loop = RunAsync(_lifetime.Token);
    }

    /// <summary>Looks once: installs every newer pack with automatic updates on, else says which have one.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var catalog = await _packs.GetCatalogAsync(cancellationToken: cancellationToken).ConfigureAwait(true);

        foreach (var failure in catalog.Failures)
        {
            _logger.Information("The pack update check could not read {Registry}: {Message}", failure.Registry, failure.Message);
        }

        Recount(catalog);

        var due = Due(catalog);

        if (catalog.AutoUpdate)
        {
            foreach (var entry in due)
            {
                await InstallAsync(entry.GameId, entry.DisplayName, cancellationToken).ConfigureAwait(true);
            }

            if (due.Count > 0)
            {
                await _reloadGames(cancellationToken).ConfigureAwait(true);
            }

            return;
        }

        foreach (var entry in due)
        {
            Announce(entry);
        }
    }

    /// <summary>Brings the badge in line with a catalogue read from the sources, and clears stale notices.</summary>
    /// <remarks>A catalogue with no source, or read offline, never says a game stopped waiting.</remarks>
    /// <param name="catalog">The catalogue as last read.</param>
    public void Recount(PackCatalogResult catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (!catalog.Entries.Any(entry => entry.Registry is not null))
        {
            return;
        }

        _waiting.Clear();
        _waiting.UnionWith(Due(catalog).Select(entry => entry.GameId));

        foreach (var gameId in _readyNotices.Keys.Where(gameId => !_waiting.Contains(gameId)).ToList())
        {
            _notifications.Dismiss(_readyNotices[gameId]);
            _readyNotices.Remove(gameId);
        }

        WaitingCount = _waiting.Count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static List<PackCatalogEntry> Due(PackCatalogResult catalog) =>
        [.. catalog.Entries.Where(entry => entry.IsInstalled && entry.UpdateAvailable && entry.Preference.UpdatesAllowed)];

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (XxsmException ex)
            {
                _logger.Warning(ex, "The pack update check failed");
            }
            catch (Exception ex)
            {
                // A bug of XXSM's: logged in full, and the daily check goes on.
                _logger.Error(ex, "The pack update check failed on an error nothing expected");
            }

            try
            {
                await Task.Delay(_schedule.Interval, _time, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Announce(PackCatalogEntry entry)
    {
        if (entry.TargetVersion is not { } target || !_announced.Add($"{entry.GameId}@{target.PackVersion}"))
        {
            return;
        }

        if (_readyNotices.Remove(entry.GameId, out var older))
        {
            _notifications.Dismiss(older);
        }

        var gameId = entry.GameId;
        var name = entry.DisplayName;

        _readyNotices[gameId] = _notifications.Add(
            NotificationSeverity.Information,
            name,
            _text.Format(nameof(Strings.PackUpdates_Ready), PackVersionText.Display(target.PackVersion)),
            action: new AsyncRelayCommand(ct => UpdateFromNoticeAsync(gameId, name, ct)),
            actionText: _text[nameof(Strings.PackUpdates_Update)]);
    }

    private async Task UpdateFromNoticeAsync(string gameId, string name, CancellationToken cancellationToken)
    {
        await InstallAsync(gameId, name, cancellationToken).ConfigureAwait(true);
        await _reloadGames(cancellationToken).ConfigureAwait(true);

        _showPacks();
    }

    private async Task InstallAsync(string gameId, string name, CancellationToken cancellationToken)
    {
        PackOperationResult result;

        try
        {
            result = await _packs.InstallAsync(gameId, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (XxsmException ex)
        {
            _notifications.Add(
                NotificationSeverity.Warning, name, _text.Format(nameof(Strings.PackUpdates_Failed), ex.Message));
            return;
        }

        if (_readyNotices.Remove(gameId, out var ready))
        {
            _notifications.Dismiss(ready);
        }

        _waiting.Remove(gameId);
        WaitingCount = _waiting.Count;

        _notifications.Add(
            NotificationSeverity.Information,
            name,
            _text.Format(
                nameof(Strings.Packs_Updated_Notice),
                name,
                PackVersionText.Display(result.PreviousVersion),
                PackVersionText.Display(result.PackVersion)));

        if (result.Changes is { } changes)
        {
            _followUp.Queue(changes, name);
        }

        if (result.SkippedUpdates.Count > 0)
        {
            _notifications.Add(
                NotificationSeverity.Information,
                name,
                _text.Format(nameof(Strings.SkippedUpdates_Notice), _text.Characters(result.SkippedUpdates.Count)));
        }
    }
}
