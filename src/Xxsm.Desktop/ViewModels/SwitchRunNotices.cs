using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>The notice a profile or randomiser run leaves, with an Undo for that run by its id.</summary>
public sealed class SwitchRunNotices(
    IModSwitcher switcher,
    INotificationService notifications,
    ViewModelWorkRunner work,
    ITextCatalogue text,
    Func<CancellationToken, Task> rescan)
{
    private readonly IModSwitcher _switcher = switcher;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _work = work;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _rescan = rescan;

    /// <summary>Says what a run did, with an Undo if it changed anything, and a notice per mod not switched.</summary>
    /// <param name="modsDirectory">The Mods folder the run was in.</param>
    /// <param name="result">What the run did.</param>
    /// <param name="title">The notice's headline: the profile's name, or the randomiser's.</param>
    /// <returns>The summary notice.</returns>
    public Notification Report(string modsDirectory, ModSwitchRunResult result, string title)
    {
        ArgumentNullException.ThrowIfNull(result);

        Notification? notice = null;

        var undo = result.ChangedCount > 0
            ? new AsyncRelayCommand(async () =>
            {
                if (await UndoAsync(modsDirectory, result.RunId).ConfigureAwait(true) && notice is not null)
                {
                    _notifications.Dismiss(notice);
                }
            })
            : null;

        var done = _text.Format(
            nameof(Strings.SwitchRun_Done), _text.Mods(result.EnabledCount), _text.Mods(result.DisabledCount));

        notice = _notifications.Add(
            result.Failures.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
            title,
            result.Failures.Count > 0
                ? done + " " + _text.ForCount(result.Failures.Count, nameof(Strings.SwitchRun_Failures_One), nameof(Strings.SwitchRun_Failures), _text.Mods(result.Failures.Count))
                : done,
            action: undo,
            actionText: undo is null ? null : _text[nameof(Strings.Notifications_Undo)]);

        foreach (var failure in result.Failures)
        {
            _notifications.Add(NotificationSeverity.Error, failure.ModName, failure.Error!);
        }

        return notice;
    }

    private Task<bool> UndoAsync(string modsDirectory, string runId) => _work.RunAsync(
        _text[nameof(Strings.Notifications_Undo)],
        async ct =>
        {
            var undone = await _switcher.UndoAsync(modsDirectory, runId, ct).ConfigureAwait(true);

            await _rescan(ct).ConfigureAwait(true);

            _notifications.Add(
                undone.NotRestored.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                _text[nameof(Strings.Notifications_Undo)],
                undone.NotRestored.Count > 0
                    ? _text.ForCount(undone.NotRestored.Count, nameof(Strings.SwitchRun_Undone_WithSkips_One), nameof(Strings.SwitchRun_Undone_WithSkips),
                        _text.Mods(undone.RestoredCount),
                        _text.Mods(undone.NotRestored.Count))
                    : _text.Format(nameof(Strings.SwitchRun_Undone), _text.Mods(undone.RestoredCount)));

            foreach (var skipped in undone.NotRestored)
            {
                _notifications.Add(NotificationSeverity.Warning, skipped.From, skipped.Reason!);
            }
        },
        CancellationToken.None);
}
