using System.Globalization;
using System.Text.RegularExpressions;

namespace Xxsm.Packs.Registry;

/// <summary>How a pack version reads to a person: <c>2026.09.25.01</c> as <c>26.9.25.1</c>. Display only.</summary>
public static partial class PackVersionText
{
    /// <summary>The version as a person reads it.</summary>
    /// <param name="version">The stored version, or null.</param>
    /// <returns>The short form for a dated version; anything else unchanged; empty for null.</returns>
    public static string Display(string? version)
    {
        if (version is not { Length: > 0 })
        {
            return string.Empty;
        }

        var match = Dated().Match(version);

        if (!match.Success)
        {
            return version;
        }

        var parts = new List<string>(4)
        {
            match.Groups["year"].Value[2..],
            Number(match.Groups["month"].Value),
            Number(match.Groups["day"].Value),
        };

        if (match.Groups["build"].Success)
        {
            parts.Add(Number(match.Groups["build"].Value));
        }

        return string.Join('.', parts);
    }

    /// <summary>The stored form of a version a person typed: <c>26.9.27.1</c> back to <c>2026.09.27.01</c>.</summary>
    /// <param name="typed">What was typed.</param>
    /// <returns>The full dated form for a short date with a real month and day; anything else, trimmed.</returns>
    public static string ToStored(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        var trimmed = typed.Trim();
        var match = Short().Match(trimmed);

        if (!match.Success)
        {
            return trimmed;
        }

        var month = int.Parse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture);

        if (month is < 1 or > 12 || day is < 1 or > 31)
        {
            return trimmed;
        }

        var stored = string.Create(
            CultureInfo.InvariantCulture, $"20{match.Groups["year"].Value}.{month:00}.{day:00}");

        if (match.Groups["build"].Success)
        {
            var build = int.Parse(match.Groups["build"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            stored += string.Create(CultureInfo.InvariantCulture, $".{build:00}");
        }

        return stored;
    }

    private static string Number(string digits) =>
        int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<year>\d{4})\.(?<month>\d{2})\.(?<day>\d{2})(?:\.(?<build>\d{1,3}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Dated();

    [GeneratedRegex(@"^(?<year>\d{2})\.(?<month>\d{1,2})\.(?<day>\d{1,2})(?:\.(?<build>\d{1,3}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Short();
}
