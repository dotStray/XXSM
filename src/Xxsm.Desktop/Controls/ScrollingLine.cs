using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Xxsm.Desktop.Controls;

/// <summary>A row that stays one line and scrolls sideways, with an arrow at each end with more there.</summary>
[TemplatePart("PART_Line", typeof(ScrollViewer))]
[TemplatePart("PART_BackEdge", typeof(Border))]
[TemplatePart("PART_ForwardEdge", typeof(Border))]
[TemplatePart("PART_Back", typeof(Button))]
[TemplatePart("PART_Forward", typeof(Button))]
public sealed class ScrollingLine : ContentControl
{
    /// <summary>How far one notch of a mouse wheel moves the row, the step Avalonia's own scrolling takes.</summary>
    private const double WheelStep = 50;

    /// <summary>How much of the visible width one arrow press moves: most, so a half-seen item stays seen.</summary>
    private const double ArrowStep = 0.75;

    private ScrollViewer? _line;
    private Border? _backEdge;
    private Border? _forwardEdge;
    private Button? _back;
    private Button? _forward;

    /// <summary>Creates the row.</summary>
    public ScrollingLine()
    {
        AddHandler(PointerWheelChangedEvent, OnWheel);
    }

    /// <summary>The scrolling part, once the template is applied.</summary>
    public ScrollViewer? Line => _line;

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_line is not null)
        {
            _line.ScrollChanged -= OnScrolled;
        }

        if (_back is not null)
        {
            _back.Click -= OnArrow;
        }

        if (_forward is not null)
        {
            _forward.Click -= OnArrow;
        }

        _line = e.NameScope.Find<ScrollViewer>("PART_Line");
        _backEdge = e.NameScope.Find<Border>("PART_BackEdge");
        _forwardEdge = e.NameScope.Find<Border>("PART_ForwardEdge");
        _back = e.NameScope.Find<Button>("PART_Back");
        _forward = e.NameScope.Find<Button>("PART_Forward");

        if (_line is not null)
        {
            _line.ScrollChanged += OnScrolled;
        }

        if (_back is not null)
        {
            _back.Click += OnArrow;
        }

        if (_forward is not null)
        {
            _forward.Click += OnArrow;
        }
    }

    /// <summary>A mouse wheel over the row scrolls it sideways.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || _line is null || e.Delta.Y == 0 || e.Delta.X != 0)
        {
            return;
        }

        e.Handled = Scroll(-e.Delta.Y * WheelStep);
    }

    /// <summary>An arrow moves the row most of its own width that way.</summary>
    private void OnArrow(object? sender, RoutedEventArgs e)
    {
        if (_line is null)
        {
            return;
        }

        var direction = ReferenceEquals(sender, _back) ? -1 : 1;
        Scroll(direction * _line.Viewport.Width * ArrowStep);
        e.Handled = true;
    }

    /// <summary>Shows each arrow only while something is out of sight on its side.</summary>
    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_line is null)
        {
            return;
        }

        var furthest = Math.Max(0, _line.Extent.Width - _line.Viewport.Width);

        if (_backEdge is not null)
        {
            _backEdge.IsVisible = _line.Offset.X > 0.5;
        }

        if (_forwardEdge is not null)
        {
            _forwardEdge.IsVisible = _line.Offset.X < furthest - 0.5;
        }
    }

    /// <summary>Moves the row by <paramref name="by"/> pixels, within its ends. Says whether it moved.</summary>
    private bool Scroll(double by)
    {
        if (_line is null)
        {
            return false;
        }

        var furthest = Math.Max(0, _line.Extent.Width - _line.Viewport.Width);
        var x = Math.Clamp(_line.Offset.X + by, 0, furthest);

        if (x == _line.Offset.X)
        {
            return false;
        }

        _line.Offset = _line.Offset.WithX(x);
        return true;
    }
}
