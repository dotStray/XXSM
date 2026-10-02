using System.Globalization;

namespace Xxsm.Desktop.Resources;

/// <summary>How the window writes a date: <c>27-Sep-2026</c>, with <c>14:05</c> for a time; local, English.</summary>
internal static class TextDates
{
    /// <summary>A day, <c>dd-MMM-yyyy</c>.</summary>
    /// <returns>For example <c>27-Sep-2026</c>.</returns>
    public static string Date(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

    /// <summary>A day and a time, <c>dd-MMM-yyyy HH:mm</c>.</summary>
    /// <returns>For example <c>27-Sep-2026 14:05</c>.</returns>
    public static string DateAndTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture);
}
