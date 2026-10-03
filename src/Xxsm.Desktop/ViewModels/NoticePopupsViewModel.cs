using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Desktop.Services;

namespace Xxsm.Desktop.ViewModels;

/// <summary>The notices that pop up over the window: newest under the last, at most three at a time.</summary>
/// <remarks>Each notice raised through <see cref="INotificationService"/> pops up once; closing a pop-up leaves the
/// notice on the Notices page.</remarks>
public sealed class NoticePopupsViewModel : ViewModelBase
{
    /// <summary>How many pop-ups are up at once; one more makes the oldest leave early.</summary>
    public const int MaxShown = 3;

    /// <summary>How long a pop-up that is not an error stays, counted again whenever the pointer leaves it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    /// <summary>How long a pop-up takes to fade and fold away once it leaves.</summary>
    public static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(200);

    private readonly INotificationService _notifications;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly ObservableCollection<NoticePopupViewModel> _popups = [];

    /// <summary>Creates the layer.</summary>
    /// <param name="notifications">Where every notice comes from.</param>
    /// <param name="time">Times each pop-up's stay.</param>
    /// <param name="dispatcher">Brings a timer back onto the UI thread.</param>
    public NoticePopupsViewModel(INotificationService notifications, TimeProvider time, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _notifications = notifications;
        _time = time;
        _dispatcher = dispatcher;
        Popups = new ReadOnlyObservableCollection<NoticePopupViewModel>(_popups);
    }

    /// <summary>What is up now, oldest first: the first hangs just under the window's top-right corner.</summary>
    public ReadOnlyObservableCollection<NoticePopupViewModel> Popups { get; }

    /// <inheritdoc />
    protected override void OnActivated() =>
        ((INotifyCollectionChanged)_notifications.Notifications).CollectionChanged += OnNotificationsChanged;

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        ((INotifyCollectionChanged)_notifications.Notifications).CollectionChanged -= OnNotificationsChanged;

        foreach (var popup in _popups)
        {
            popup.StopTimer();
        }

        _popups.Clear();
    }

    /// <summary>Starts a pop-up's leaving; it is taken away once it has faded.</summary>
    internal void Close(NoticePopupViewModel popup)
    {
        if (popup.IsLeaving || !_popups.Contains(popup))
        {
            return;
        }

        popup.IsLeaving = true;
        popup.Schedule(LeaveDuration);
    }

    /// <summary>Takes away a pop-up that has finished leaving.</summary>
    internal void Remove(NoticePopupViewModel popup)
    {
        popup.StopTimer();
        _popups.Remove(popup);
    }

    /// <summary>Creates a timer whose tick runs on the UI thread.</summary>
    internal ITimer CreateTimer(Action tick) =>
        _time.CreateTimer(_ => _dispatcher.Post(tick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void OnNotificationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (var notice in e.NewItems.OfType<Notification>().Reverse())
                {
                    Show(notice);
                }

                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var notice in e.OldItems.OfType<Notification>())
                {
                    if (_popups.FirstOrDefault(p => ReferenceEquals(p.Notice, notice)) is { } popup)
                    {
                        Close(popup);
                    }
                }

                break;

            case NotifyCollectionChangedAction.Reset:
                foreach (var popup in _popups.ToList())
                {
                    Close(popup);
                }

                break;
        }
    }

    private void Show(Notification notice)
    {
        var popup = new NoticePopupViewModel(notice, this);
        _popups.Add(popup);
        popup.StartStay();

        var staying = _popups.Where(p => !p.IsLeaving).ToList();

        if (staying.Count > MaxShown)
        {
            // An error is meant to stay, so the oldest of the others goes first.
            Close(staying.FirstOrDefault(p => !p.Notice.IsError) ?? staying[0]);
        }
    }
}

/// <summary>One notice popped up over the window, with its own buttons and Dismiss.</summary>
public sealed partial class NoticePopupViewModel : ObservableObject
{
    private readonly NoticePopupsViewModel _owner;
    private ITimer? _timer;

    /// <summary>Creates a pop-up for one notice.</summary>
    internal NoticePopupViewModel(Notification notice, NoticePopupsViewModel owner)
    {
        Notice = notice;
        _owner = owner;
    }

    /// <summary>The notice it shows.</summary>
    public Notification Notice { get; }

    /// <summary>Whether the notice offers Retry.</summary>
    public bool HasRetry => Notice.RetryCommand is not null;

    /// <summary>Whether it is fading away; it is taken off the window once faded.</summary>
    [ObservableProperty]
    private bool _isLeaving;

    /// <summary>Whether the pointer is on it, which holds it up.</summary>
    [ObservableProperty]
    private bool _isPointerOver;

    /// <summary>Whether the keyboard is on one of its buttons, which holds it up.</summary>
    [ObservableProperty]
    private bool _isFocusWithin;

    /// <summary>Whether its stay is being counted down now: not held, not leaving, not an error.</summary>
    [ObservableProperty]
    private bool _isCounting;

    /// <summary>Whether it goes by itself, so it shows how long is left; an error stays.</summary>
    public bool HasCountdown => !Notice.IsError;

    private bool IsHeld => IsPointerOver || IsFocusWithin;

    /// <summary>Closes the pop-up. The notice stays on the Notices page.</summary>
    [RelayCommand]
    private void Dismiss() => _owner.Close(this);

    /// <summary>Runs the notice's own action, then closes the pop-up.</summary>
    [RelayCommand]
    private void Action()
    {
        if (Notice.ActionCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }

        _owner.Close(this);
    }

    /// <summary>Runs the notice's Retry, then closes the pop-up.</summary>
    [RelayCommand]
    private void Retry()
    {
        if (Notice.RetryCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }

        _owner.Close(this);
    }

    /// <summary>Starts the stay; an error has none and stays until closed.</summary>
    internal void StartStay()
    {
        if (!Notice.IsError && !IsHeld && !IsLeaving)
        {
            Schedule(NoticePopupsViewModel.Lifetime);
            IsCounting = true;
        }
    }

    /// <summary>Ticks once after <paramref name="due"/>.</summary>
    internal void Schedule(TimeSpan due)
    {
        _timer ??= _owner.CreateTimer(OnTick);
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Stops and releases the timer.</summary>
    internal void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    partial void OnIsLeavingChanged(bool value)
    {
        if (value)
        {
            IsCounting = false;
        }
    }

    partial void OnIsPointerOverChanged(bool value) => OnHeldChanged();

    partial void OnIsFocusWithinChanged(bool value) => OnHeldChanged();

    private void OnHeldChanged()
    {
        if (IsLeaving)
        {
            return;
        }

        if (IsHeld)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            IsCounting = false;
        }
        else
        {
            StartStay();
        }
    }

    private void OnTick()
    {
        if (IsLeaving)
        {
            _owner.Remove(this);
        }
        else if (!IsHeld && !Notice.IsError)
        {
            _owner.Close(this);
        }
    }
}
