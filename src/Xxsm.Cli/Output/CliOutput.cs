using System.Globalization;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Output;

/// <summary>Small helpers for writing human-readable CLI output.</summary>
internal static class CliOutput
{
    /// <summary>Writes a two-column table with the labels aligned.</summary>
    /// <param name="rows">Label and value pairs. An empty label writes a blank line.</param>
    public static void WriteRows(IReadOnlyList<(string Label, string Value)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var width = rows.Max(r => r.Label.Length);

        foreach (var (label, value) in rows)
        {
            if (label.Length == 0)
            {
                Console.Out.WriteLine();
                continue;
            }

            Console.Out.WriteLine($"{label.PadRight(width)}  {value}");
        }
    }

    /// <summary>Writes a message to standard error, where it cannot corrupt JSON output.</summary>
    public static void WriteError(string message) => Console.Error.WriteLine(message);

    /// <summary>Formats a byte count for a person, in powers of 1024, never rounding a real file to zero.</summary>
    /// <param name="bytes">How many bytes.</param>
    /// <returns>For example <c>812 bytes</c>, <c>1.4 MB</c>.</returns>
    public static string Bytes(long bytes)
    {
        string[] units = ["KB", "MB", "GB", "TB"];

        if (bytes < 1024)
        {
            return EnglishCount.Plural((int)Math.Max(bytes, 0), "byte", "bytes");
        }

        double value = bytes;
        var unit = -1;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>An enum member name as the JSON spells it: <c>AlreadyCurrent</c> as <c>alreadyCurrent</c>.</summary>
    public static string Camel(string value) =>
        value is { Length: > 0 } ? char.ToLowerInvariant(value[0]) + value[1..] : value;
}
