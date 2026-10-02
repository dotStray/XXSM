using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Xxsm.Desktop.Converters;

/// <summary>Binds one radio button to one member of an enum; unchecking is ignored on purpose.</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.Equals(parameter);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : BindingOperations.DoNothing;
}
