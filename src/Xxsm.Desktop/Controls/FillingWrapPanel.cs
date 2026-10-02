using Avalonia;
using Avalonia.Controls;

namespace Xxsm.Desktop.Controls;

/// <summary>A wrapping panel whose items share the leftover width, up to a width they are not grown past.</summary>
public sealed class FillingWrapPanel : Panel
{
    /// <summary>The narrowest an item may be. The panel fits as many of these across as it can.</summary>
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<FillingWrapPanel, double>(nameof(MinItemWidth), 120d);

    /// <summary>The widest an item may be grown to. Unset, an item grows to fill its share of the row.</summary>
    public static readonly StyledProperty<double> MaxItemWidthProperty =
        AvaloniaProperty.Register<FillingWrapPanel, double>(nameof(MaxItemWidth), double.PositiveInfinity);

    /// <summary>The gap between items in a row.</summary>
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<FillingWrapPanel, double>(nameof(ItemSpacing), 12d);

    /// <summary>The gap between rows.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FillingWrapPanel, double>(nameof(RowSpacing), 12d);

    /// <summary>Whether fewer items than fit share the whole row rather than keeping a full row's width.</summary>
    public static readonly StyledProperty<bool> FillShortRowProperty =
        AvaloniaProperty.Register<FillingWrapPanel, bool>(nameof(FillShortRow));

    static FillingWrapPanel() =>
        AffectsMeasure<FillingWrapPanel>(
            MinItemWidthProperty, MaxItemWidthProperty, ItemSpacingProperty, RowSpacingProperty,
            FillShortRowProperty);

    /// <summary>Whether fewer items than fit share the whole row; false keeps a full row's item width.</summary>
    public bool FillShortRow
    {
        get => GetValue(FillShortRowProperty);
        set => SetValue(FillShortRowProperty, value);
    }

    /// <summary>The narrowest an item may be.</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>The widest an item may grow; equal to the minimum, items keep that width.</summary>
    public double MaxItemWidth
    {
        get => GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    /// <summary>The gap between items in a row.</summary>
    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>The gap between rows.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>How many items fit across a given width, and how wide each one is.</summary>
    /// <param name="availableWidth">The width to divide.</param>
    /// <param name="itemCount">How many items there are.</param>
    internal (int Columns, double ItemWidth) Measure(double availableWidth, int itemCount)
    {
        var minimum = Math.Max(1d, MinItemWidth);
        var spacing = Math.Max(0d, ItemSpacing);

        if (itemCount <= 0 || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            // Unconstrained width means measuring, not laying out: report the intended size.
            return (Math.Max(1, itemCount), minimum);
        }

        var columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (minimum + spacing)));

        if (FillShortRow)
        {
            columns = Math.Min(columns, itemCount);
        }

        var width = (availableWidth - (spacing * (columns - 1))) / columns;

        var maximum = Math.Max(minimum, MaxItemWidth);

        if (width > maximum)
        {
            width = maximum;
        }

        // A single column narrower than the minimum still fits, rather than scrolling the page sideways.
        return (columns, Math.Max(1d, width));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;

        if (children.Count == 0)
        {
            return default;
        }

        var (columns, itemWidth) = Measure(availableSize.Width, children.Count);
        var rowSpacing = Math.Max(0d, RowSpacing);

        var height = 0d;
        var rowHeight = 0d;

        for (var i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);

            if ((i + 1) % columns == 0 || i == children.Count - 1)
            {
                height += rowHeight + (height > 0 ? rowSpacing : 0);
                rowHeight = 0;
            }
        }

        var width = double.IsInfinity(availableSize.Width)
            ? (itemWidth * columns) + (Math.Max(0d, ItemSpacing) * (columns - 1))
            : availableSize.Width;

        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;

        if (children.Count == 0)
        {
            return finalSize;
        }

        var (columns, itemWidth) = Measure(finalSize.Width, children.Count);
        var spacing = Math.Max(0d, ItemSpacing);
        var rowSpacing = Math.Max(0d, RowSpacing);

        var y = 0d;
        var rowHeight = 0d;

        for (var i = 0; i < children.Count; i++)
        {
            var column = i % columns;
            var x = column * (itemWidth + spacing);

            rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            children[i].Arrange(new Rect(x, y, itemWidth, children[i].DesiredSize.Height));

            if (column == columns - 1 || i == children.Count - 1)
            {
                y += rowHeight + rowSpacing;
                rowHeight = 0;
            }
        }

        return finalSize;
    }
}
