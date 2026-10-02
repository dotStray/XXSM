using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Xxsm.Desktop.Converters;

/// <summary>Turns an icon's name into its <c>Icon.&lt;name&gt;</c> geometry, or nothing for an unknown name.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    /// <summary>The prefix the icon geometries are keyed under.</summary>
    public const string ResourceKeyPrefix = "Icon.";

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name || name.Length is 0 || Application.Current is not { } application)
        {
            return null;
        }

        return application.Resources.TryGetResource(
            ResourceKeyPrefix + name, application.ActualThemeVariant, out var found)
            ? found as Geometry
            : null;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. An icon never converts back.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("An icon name is one-way.");
}
