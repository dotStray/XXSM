using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels;

/// <summary>What follows every pack install: What's new, the Skipped updates review, then the clean-up offer.</summary>
/// <remarks>Each panel waits for the one before it to close; two never stack.</remarks>
public sealed class PackInstallFollowUp : ObservableObject
{
    private readonly INotificationService _notifications;
    private readonly ITextCatalogue _text;

    private (PackOperationResult Result, string Title)? _pendingReview;

    private readonly List<(PackChanges Changes, string Title)> _queued = [];

    /// <summary>Creates the sequence.</summary>
    /// <param name="whatsNew">What an update changed.</param>
    /// <param name="skippedUpdates">The review of what an update withheld from an edited character.</param>
    /// <param name="caughtUp">The offer to clear corrections an installed pack now makes itself.</param>
    /// <param name="notifications">Where the withheld changes are also noted, so dismissing loses nothing.</param>
    /// <param name="text">The interface's wording.</param>
    public PackInstallFollowUp(
        WhatsNewViewModel whatsNew,
        SkippedUpdatesViewModel skippedUpdates,
        CaughtUpCorrectionsViewModel caughtUp,
        INotificationService notifications,
        ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(whatsNew);
        ArgumentNullException.ThrowIfNull(skippedUpdates);
        ArgumentNullException.ThrowIfNull(caughtUp);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(text);

        WhatsNew = whatsNew;
        SkippedUpdates = skippedUpdates;
        CaughtUp = caughtUp;
        _notifications = notifications;
        _text = text;

        WhatsNew.PropertyChanged += OnPanelChanged;
        SkippedUpdates.PropertyChanged += OnPanelChanged;
        CaughtUp.PropertyChanged += OnPanelChanged;
    }

    /// <summary><em>What's new</em>.</summary>
    public WhatsNewViewModel WhatsNew { get; }

    /// <summary>The <em>Skipped updates</em> review.</summary>
    public SkippedUpdatesViewModel SkippedUpdates { get; }

    /// <summary>The offer to clear corrections an installed pack now makes itself.</summary>
    public CaughtUpCorrectionsViewModel CaughtUp { get; }

    /// <summary>Raised when an update's <em>What's new</em> is queued or shown from the queue.</summary>
    public event EventHandler? QueueChanged;

    /// <summary>Keeps an automatic update's <em>What's new</em> until a page that may show it asks.</summary>
    /// <param name="changes">What the update changed.</param>
    /// <param name="title">The game's name, for the heading.</param>
    public void Queue(PackChanges changes, string title)
    {
        ArgumentNullException.ThrowIfNull(changes);

        _queued.RemoveAll(item => string.Equals(item.Changes.GameId, changes.GameId, StringComparison.OrdinalIgnoreCase));
        _queued.Add((changes, title));
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether an update's <em>What's new</em> is waiting.</summary>
    /// <param name="gameId">One game, or null for any.</param>
    /// <returns>True when one is.</returns>
    public bool HasQueued(string? gameId = null) => _queued.Any(item => Matches(item.Changes, gameId));

    /// <summary>Opens the next waiting <em>What's new</em> when no panel is up: any game's, or one game's.</summary>
    /// <param name="gameId">The game whose list may open, or null for any.</param>
    /// <returns>True when one opened.</returns>
    public bool ShowQueued(string? gameId = null)
    {
        if (IsAnyPanelOpen || _queued.FindIndex(item => Matches(item.Changes, gameId)) is not (>= 0 and var index))
        {
            return false;
        }

        var (changes, title) = _queued[index];
        _queued.RemoveAt(index);
        WhatsNew.Show(changes, title);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool Matches(PackChanges changes, string? gameId) =>
        gameId is null || string.Equals(changes.GameId, gameId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether any of the panels is on screen.</summary>
    public bool IsAnyPanelOpen => WhatsNew.IsOpen || SkippedUpdates.IsOpen || CaughtUp.IsOpen;

    /// <summary>Shows what an update changed, then what it withheld, then offers the clean-up.</summary>
    /// <remarks>A comparison that cannot be made is its own notice: the pack is installed either way.</remarks>
    /// <param name="result">What the install did.</param>
    /// <param name="title">The game's name, for the panels and the notices.</param>
    /// <param name="cancellationToken">Cancels the comparison with the user's corrections.</param>
    /// <returns>A task that completes when the offer is ready, or known to be empty.</returns>
    public async Task AfterInstallAsync(PackOperationResult result, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        _pendingReview = null;

        if (_queued.RemoveAll(item => string.Equals(item.Changes.GameId, result.GameId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }

        if (result.Changes is { } changes)
        {
            WhatsNew.Show(changes, title);
        }

        // Withheld changes are noted as a notice too, so dismissing the review loses nothing.
        if (result.SkippedUpdates.Count > 0)
        {
            if (WhatsNew.IsOpen)
            {
                _pendingReview = (result, title);
            }
            else
            {
                SkippedUpdates.Show(result.SkippedUpdates, result.PackVersion, result.GameId, title);
            }

            _notifications.Add(
                NotificationSeverity.Information,
                title,
                _text.Format(nameof(Strings.SkippedUpdates_Notice), _text.Characters(result.SkippedUpdates.Count)));
        }

        if (result.Directory is not { Length: > 0 } directory)
        {
            return;
        }

        try
        {
            if (await CaughtUp.CheckAsync(result.GameId, title, directory, cancellationToken).ConfigureAwait(true)
                && !WhatsNew.IsOpen && !SkippedUpdates.IsOpen)
            {
                CaughtUp.Show();
            }
        }
        catch (Xxsm.Core.XxsmException ex)
        {
            _notifications.Add(NotificationSeverity.Warning, title, ex.Message);
        }
    }

    private void OnPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(SkippedUpdatesViewModel.IsOpen))
        {
            return;
        }

        if (ReferenceEquals(sender, WhatsNew) && !WhatsNew.IsOpen)
        {
            if (_pendingReview is { } pending)
            {
                _pendingReview = null;
                SkippedUpdates.Show(pending.Result.SkippedUpdates, pending.Result.PackVersion, pending.Result.GameId, pending.Title);
            }
            else
            {
                CaughtUp.Show();
            }
        }

        if (ReferenceEquals(sender, SkippedUpdates) && !SkippedUpdates.IsOpen)
        {
            CaughtUp.Show();
        }

        OnPropertyChanged(nameof(IsAnyPanelOpen));
    }
}
