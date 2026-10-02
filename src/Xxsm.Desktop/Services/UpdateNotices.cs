using CommunityToolkit.Mvvm.Input;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.Services;

/// <summary>The one place an update check turns into a notice: one summary per check, never one per mod.</summary>
internal static class UpdateNotices
{
    /// <summary>Raises the summary notice for a finished check, when there is anything to say.</summary>
    /// <param name="notifications">The session's notices.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="report">What the check found.</param>
    /// <param name="show">Opens the screen that lists them, when the caller can offer one.</param>
    /// <returns>The notice, or null when the check found nothing worth saying.</returns>
    public static Notification? ReportUpdates(
        this INotificationService notifications,
        ITextCatalogue text,
        ModUpdateReport report,
        IAsyncRelayCommand? show = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(report);

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

        if (!report.HasUpdates)
        {
            return null;
        }

        return notifications.Add(
            NotificationSeverity.Information,
            text.ForCount(report.Updates.Count, nameof(Strings.Mods_Updates_Notice_One), nameof(Strings.Mods_Updates_Notice), text.Mods(report.Updates.Count)),
            text[nameof(Strings.Mods_Updates_Notice_Body)],
            action: show,
            actionText: show is null ? null : text[nameof(Strings.Mods_Updates_Notice_Action)]);
    }
}
