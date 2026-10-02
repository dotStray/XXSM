using Avalonia.Data.Converters;
using Xxsm.Core.Io;

namespace Xxsm.Desktop.Converters;

/// <summary>Converters for showing a stored path to the user.</summary>
public static class PathConverters
{
    /// <summary>A stored path in the form this system writes (<c>\</c> on Windows); empty for null.</summary>
    public static FuncValueConverter<string?, string> Display { get; } = new(PathDisplay.Show);
}
