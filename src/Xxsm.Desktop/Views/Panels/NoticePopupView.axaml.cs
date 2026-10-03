using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Xxsm.Desktop.ViewModels;

namespace Xxsm.Desktop.Views.Panels;

/// <summary>One notice popped up over the window; holds itself up while the pointer or keyboard is on it.</summary>
public partial class NoticePopupView : UserControl
{
    private NoticePopupViewModel? _popup;

    /// <summary>Creates the view.</summary>
    public NoticePopupView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_popup is not null)
        {
            _popup.PropertyChanged -= OnPopupChanged;
        }

        // A view handed a new pop-up starts whole, whatever the last one left it at.
        Transitions = null;
        Height = double.NaN;
        Opacity = 1;

        _popup = DataContext as NoticePopupViewModel;

        if (_popup is not null)
        {
            _popup.PropertyChanged += OnPopupChanged;
        }

        RunCountdown();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RunCountdown();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_popup is not null)
        {
            _popup.PropertyChanged -= OnPopupChanged;
            _popup = null;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_popup is null)
        {
            return;
        }

        if (change.Property == IsPointerOverProperty)
        {
            _popup.IsPointerOver = IsPointerOver;
        }
        else if (change.Property == IsKeyboardFocusWithinProperty)
        {
            _popup.IsFocusWithin = IsKeyboardFocusWithin;
        }
    }

    /// <summary>Fades and folds away, so the pop-ups under it slide up rather than jump.</summary>
    private void OnPopupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoticePopupViewModel.IsCounting))
        {
            RunCountdown();
            return;
        }

        if (e.PropertyName != nameof(NoticePopupViewModel.IsLeaving) || _popup is not { IsLeaving: true })
        {
            return;
        }

        // Fixed at its drawn height first: a transition cannot start from an automatic height.
        Height = Bounds.Height;
        Transitions =
        [
            new DoubleTransition { Property = HeightProperty, Duration = NoticePopupsViewModel.LeaveDuration },
            new DoubleTransition { Property = OpacityProperty, Duration = NoticePopupsViewModel.LeaveDuration },
        ];

        Dispatcher.UIThread.Post(() =>
        {
            Height = 0;
            Opacity = 0;
        });
    }

    /// <summary>Fills the bar, then empties it over the stay while the stay is counting.</summary>
    private void RunCountdown()
    {
        Countdown.Transitions = null;
        Countdown.RenderTransform = TransformOperations.Parse("scaleX(1)");

        if (_popup is not { IsCounting: true } || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        Countdown.Transitions =
        [
            new TransformOperationsTransition
            {
                Property = RenderTransformProperty,
                Duration = NoticePopupsViewModel.Lifetime,
            },
        ];

        // Emptied on the next pass, so the transition starts from the full bar just drawn.
        Dispatcher.UIThread.Post(() =>
        {
            if (_popup is { IsCounting: true })
            {
                Countdown.RenderTransform = TransformOperations.Parse("scaleX(0)");
            }
        });
    }

}
