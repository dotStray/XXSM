using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Xxsm.Desktop.Services;

/// <summary>How much a notice matters.</summary>
public enum NotificationSeverity
{
    /// <summary>Something finished. No action needed.</summary>
    Information,

    /// <summary>Something is degraded but XXSM carried on.</summary>
    Warning,

    /// <summary>Something the user asked for did not happen.</summary>
    Error,
}

/// <summary>One entry in the notifications panel.</summary>
public sealed partial class Notification : ObservableObject
{
    /// <summary>A short headline.</summary>
    public required string Title { get; init; }

    /// <summary>The detail, including the operating system's own words where there are any.</summary>
    public required string Message { get; init; }

    /// <summary>How much it matters.</summary>
    public required NotificationSeverity Severity { get; init; }

    /// <summary>When it happened.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Runs the failed operation again, when there is one to run; never retried unasked.</summary>
    public IAsyncRelayCommand? RetryCommand { get; init; }

    /// <summary>One thing the notice offers about what it reports, such as an undo, or null.</summary>
    public IAsyncRelayCommand? ActionCommand { get; init; }

    /// <summary>What the button for <see cref="ActionCommand"/> says. Null when there is none.</summary>
    public string? ActionText { get; init; }

    /// <summary>Whether there is an action to offer.</summary>
    public bool HasAction => ActionCommand is not null && ActionText is { Length: > 0 };

    /// <summary>Whether the user has seen it. Drives the badge on the rail.</summary>
    [ObservableProperty]
    private bool _isRead;

    /// <summary>Whether this is an ordinary "it worked" notice. Severity is drawn as a word, not a colour.</summary>
    public bool IsInformation => Severity == NotificationSeverity.Information;

    /// <summary>Whether something is degraded but XXSM carried on.</summary>
    public bool IsWarning => Severity == NotificationSeverity.Warning;

    /// <summary>Whether something the user asked for did not happen.</summary>
    public bool IsError => Severity == NotificationSeverity.Error;
}

/// <summary>The notifications panel's backing store. Whether something failed is decided elsewhere.</summary>
public interface INotificationService : INotifyPropertyChanged
{
    /// <summary>Everything raised this session, newest first.</summary>
    ReadOnlyObservableCollection<Notification> Notifications { get; }

    /// <summary>How many have not been read. The number on the rail's badge.</summary>
    int UnreadCount { get; }

    /// <summary>Raises a notice.</summary>
    /// <param name="severity">How much it matters.</param>
    /// <param name="title">A short headline.</param>
    /// <param name="message">The detail, in the OS's own words where there are any.</param>
    /// <param name="retry">Runs the operation again, when it can be.</param>
    /// <param name="action">One thing to offer about what happened, shown only with its text.</param>
    /// <param name="actionText">What the action's button says.</param>
    /// <returns>The notice, so a caller can dismiss it later.</returns>
    Notification Add(
        NotificationSeverity severity,
        string title,
        string message,
        IAsyncRelayCommand? retry = null,
        IAsyncRelayCommand? action = null,
        string? actionText = null);

    /// <summary>Removes one notice.</summary>
    void Dismiss(Notification notification);

    /// <summary>Removes every notice.</summary>
    void Clear();

    /// <summary>Marks everything read, clearing the badge.</summary>
    void MarkAllRead();
}

/// <summary>The default <see cref="INotificationService"/>. In memory, for one session.</summary>
public sealed partial class NotificationService : ObservableObject, INotificationService
{
    private readonly ObservableCollection<Notification> _notifications = [];

    /// <summary>Creates the service.</summary>
    /// <param name="time">Supplies the timestamps, so tests are not at the mercy of a clock.</param>
    public NotificationService(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        Notifications = new ReadOnlyObservableCollection<Notification>(_notifications);
    }

    private readonly TimeProvider _time;

    /// <inheritdoc />
    public ReadOnlyObservableCollection<Notification> Notifications { get; }

    /// <inheritdoc />
    [ObservableProperty]
    private int _unreadCount;

    /// <inheritdoc />
    public Notification Add(
        NotificationSeverity severity,
        string title,
        string message,
        IAsyncRelayCommand? retry = null,
        IAsyncRelayCommand? action = null,
        string? actionText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);

        var notification = new Notification
        {
            Severity = severity,
            Title = title,
            Message = message,
            At = _time.GetUtcNow(),
            RetryCommand = retry,
            ActionCommand = action,
            ActionText = actionText,
        };

        // Counted before it is inserted: an open page marks it read as it arrives.
        UnreadCount++;
        _notifications.Insert(0, notification);

        return notification;
    }

    /// <inheritdoc />
    public void Dismiss(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_notifications.Remove(notification) && !notification.IsRead)
        {
            UnreadCount = Math.Max(0, UnreadCount - 1);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _notifications.Clear();
        UnreadCount = 0;
    }

    /// <inheritdoc />
    public void MarkAllRead()
    {
        foreach (var notification in _notifications)
        {
            notification.IsRead = true;
        }

        UnreadCount = 0;
    }
}
