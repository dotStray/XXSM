using CommunityToolkit.Mvvm.Input;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Updates;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Asks at start and daily whether a newer XXSM is out, and says so with a link to the download.</summary>
/// <remarks>It never downloads anything. Off when the settings say so; an unreachable GitHub is only logged.</remarks>
public sealed class AppUpdateWatcher(
    IAppUpdateChecker checker,
    IAppSettingsStore settings,
    INotificationService notifications,
    ViewModelWorkRunner runner,
    IUrlLauncher urls,
    ITextCatalogue text,
    UpdateSchedule schedule,
    TimeProvider time,
    ILogger logger) : IDisposable
{
    private readonly IAppUpdateChecker _checker = checker;
    private readonly IAppSettingsStore _settings = settings;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _runner = runner;
    private readonly IUrlLauncher _urls = urls;
    private readonly ITextCatalogue _text = text;
    private readonly UpdateSchedule _schedule = schedule;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<AppUpdateWatcher>();

    // Versions already announced, so a daily check does not repeat itself.
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private Notification? _notice;
    private Task? _loop;

    /// <summary>Starts asking: now, then every interval. Once; later calls do nothing.</summary>
    public void Start()
    {
        if (!_schedule.StartOnLaunch || _loop is not null)
        {
            return;
        }

        _loop = RunAsync(_lifetime.Token);
    }

    /// <summary>Asks once, unless the settings turn it off, and announces a newer version the first time it is
    /// seen.</summary>
    /// <returns>What GitHub said, or null when the check is off.</returns>
    /// <exception cref="AppUpdateException">GitHub could not be asked.</exception>
    public async Task<AppUpdateResult?> CheckAsync(CancellationToken cancellationToken)
    {
        var current = await _settings.ReadAsync(cancellationToken).ConfigureAwait(true);
        if (current.UpdateCheckOff)
        {
            return null;
        }

        var result = await _checker.CheckAsync(cancellationToken).ConfigureAwait(true);
        _logger.Information(
            "The newest XXSM is {Latest}; this is {Running}", result.Latest, result.Running);

        if (result.IsNewer && _announced.Add(result.Latest))
        {
            Announce(result);
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void Announce(AppUpdateResult result)
    {
        if (_notice is { } older)
        {
            _notifications.Dismiss(older);
        }

        var page = result.Page.AbsoluteUri;

        _notice = _notifications.Add(
            NotificationSeverity.Information,
            AppInfo.DisplayName,
            _text.Format(nameof(Strings.AppUpdate_Ready), result.Latest, result.Running),
            action: new AsyncRelayCommand(ct => _runner.RunAsync(
                _text[nameof(Strings.AppUpdate_Download)], token => _urls.OpenAsync(page, token), ct)),
            actionText: _text[nameof(Strings.AppUpdate_Download)]);
    }

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
                _logger.Information("The XXSM update check could not ask GitHub: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                // A bug of XXSM's: logged in full, and the daily check goes on.
                _logger.Error(ex, "The XXSM update check failed on an error nothing expected");
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
}
