using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Xxsm.Desktop.Controls;

/// <summary>A text box as wide as its placeholder in its own font, whatever is typed into it.</summary>
public sealed class PlaceholderWidthTextBox : TextBox
{
    /// <summary>Room for the caret after the last letter, so the placeholder's end is never clipped.</summary>
    private const double CaretRoom = 4;

    static PlaceholderWidthTextBox() =>
        AffectsMeasure<PlaceholderWidthTextBox>(PlaceholderTextProperty, FontSizeProperty, FontFamilyProperty,
            FontWeightProperty, FontStyleProperty, PaddingProperty, BorderThicknessProperty);

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>The width the placeholder needs, padding and border included.</summary>
    public double PlaceholderWidth
    {
        get
        {
            using var layout = new TextLayout(PlaceholderText ?? string.Empty,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, foreground: null);

            return Math.Ceiling(layout.WidthIncludingTrailingWhitespace + Padding.Left + Padding.Right
                + BorderThickness.Left + BorderThickness.Right + CaretRoom);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(MinWidth, Math.Min(PlaceholderWidth, availableSize.Width));
        var size = base.MeasureOverride(availableSize.WithWidth(width));
        return new Size(width, size.Height);
    }
}
