using CommunityToolkit.Mvvm.Input;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.Services;

/// <summary>The one place an update check turns into a notice: one per check that found something new, never one per mod.</summary>
internal static class UpdateNotices
{
    /// <summary>Raises the summary notice for a finished check, when it found an update no earlier check had.</summary>
    /// <param name="notifications">The session's notices.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="report">What the check found.</param>
    /// <param name="gameName">The game whose Mods folder was checked, as the notice names it.</param>
    /// <param name="show">Opens the screen that lists them, when the caller can offer one.</param>
    /// <returns>The notice, or null when the check found nothing new worth saying.</returns>
    public static Notification? ReportUpdates(
        this INotificationService notifications,
        ITextCatalogue text,
        ModUpdateReport report,
        string gameName,
        IAsyncRelayCommand? show = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(gameName);

        if (report.Failures.Count > 0)
        {
            // Its own notice: an outage is not an update.
            notifications.Add(
                NotificationSeverity.Warning,
                text.Format(nameof(Strings.Mods_Updates_Failed), text.Mods(report.Failures.Count)),
                string.Join(
                    Environment.NewLine,
                    report.Failures.Select(failure => $"{failure.DisplayName}: {failure.Error}")));
        }

        // A mod already announced stays listed on the Mods page; a notice says only what is new.
        if (report.NewlyFound.Count == 0)
        {
            return null;
        }

        var count = report.NewlyFound.Count;

        return notifications.Add(
            NotificationSeverity.Information,
            text.ForCount(count, nameof(Strings.Mods_Updates_Notice_One), nameof(Strings.Mods_Updates_Notice), text.Mods(count), gameName),
            text[nameof(Strings.Mods_Updates_Notice_Body)],
            action: show,
            actionText: show is null ? null : text[nameof(Strings.Mods_Updates_Notice_Action)]);
    }
}
