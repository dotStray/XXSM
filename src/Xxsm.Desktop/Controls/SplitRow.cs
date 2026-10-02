using Avalonia;
using Avalonia.Controls;

namespace Xxsm.Desktop.Controls;

/// <summary>A row with something at each end that becomes a stack of lines when too narrow for both.</summary>
public sealed class SplitRow : Panel
{
    /// <summary>The gap between the ends while they share one line.</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SplitRow, double>(nameof(Gap), 12d);

    /// <summary>The gap between the lines once they are stacked.</summary>
    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<SplitRow, double>(nameof(LineSpacing), 8d);

    static SplitRow() => AffectsMeasure<SplitRow>(GapProperty, LineSpacingProperty);

    /// <summary>The gap between the ends while they share one line.</summary>
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>The gap between the lines once they are stacked.</summary>
    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>Whether the children still fit on one line; an unbounded width always does.</summary>
    /// <returns>True while one line holds them all.</returns>
    internal bool FitsOnOneLine(double availableWidth, IReadOnlyList<double> widths)
    {
        ArgumentNullException.ThrowIfNull(widths);

        if (widths.Count <= 1 || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return true;
        }

        var wanted = widths.Sum() + (Math.Max(0d, Gap) * (widths.Count - 1));

        return wanted <= availableWidth;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;

        if (children.Count == 0)
        {
            return default;
        }

        foreach (var child in children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        var widths = children.Select(child => child.DesiredSize.Width).ToList();

        if (FitsOnOneLine(availableSize.Width, widths))
        {
            return new Size(
                widths.Sum() + (Math.Max(0d, Gap) * (children.Count - 1)),
                children.Max(child => child.DesiredSize.Height));
        }

        var spacing = Math.Max(0d, LineSpacing);
        var height = 0d;

        foreach (var child in children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            height += child.DesiredSize.Height;
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? widths.Max() : availableSize.Width,
            height + (spacing * (children.Count - 1)));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;

        if (children.Count == 0)
        {
            return finalSize;
        }

        var widths = children.Select(child => child.DesiredSize.Width).ToList();

        if (FitsOnOneLine(finalSize.Width, widths))
        {
            var x = 0d;

            for (var i = 0; i < children.Count; i++)
            {
                var left = i == children.Count - 1 && children.Count > 1
                    ? Math.Max(x, finalSize.Width - widths[i])
                    : x;

                children[i].Arrange(new Rect(left, 0, widths[i], finalSize.Height));
                x = left + widths[i] + Math.Max(0d, Gap);
            }

            return finalSize;
        }

        var spacing = Math.Max(0d, LineSpacing);
        var y = 0d;

        for (var i = 0; i < children.Count; i++)
        {
            var width = Math.Min(widths[i], finalSize.Width);

            // The first line keeps left, the others right, so the row's right end stays at the page's right.
            var left = i == 0 ? 0 : finalSize.Width - width;

            children[i].Arrange(new Rect(left, y, width, children[i].DesiredSize.Height));
            y += children[i].DesiredSize.Height + spacing;
        }

        return finalSize;
    }
}
