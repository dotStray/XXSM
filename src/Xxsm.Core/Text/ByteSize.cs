using System.Globalization;

namespace Xxsm.Core.Text;

/// <summary>Writes byte counts, transfer rates and short durations the way a person reads them.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>Formats a byte count in powers of 1024, one decimal place below ten.</summary>
    /// <param name="bytes">How many bytes. A negative count reads as zero.</param>
    /// <returns>For example <c>812 B</c>, <c>1.4 MB</c>, <c>38 MB</c>.</returns>
    public static string Describe(long bytes)
    {
        if (bytes < 1024)
        {
            return Math.Max(bytes, 0).ToString(CultureInfo.CurrentCulture) + " B";
        }

        double value = bytes;
        var unit = -1;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>Formats a transfer rate.</summary>
    /// <param name="bytesPerSecond">The rate. Anything not finite or below zero reads as zero.</param>
    /// <returns>For example <c>1.4 MB/s</c>.</returns>
    public static string DescribeRate(double bytesPerSecond) =>
        Describe(double.IsFinite(bytesPerSecond) ? (long)Math.Max(bytesPerSecond, 0) : 0) + "/s";

    /// <summary>Formats a short duration as a clock, which needs no singular and no plural.</summary>
    /// <param name="duration">How long. A negative duration reads as zero.</param>
    /// <returns><c>0:09</c>, <c>4:31</c> or <c>1:04:20</c>: hours only when there are any.</returns>
    public static string DescribeDuration(TimeSpan duration)
    {
        var total = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;

        return total.TotalHours >= 1
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}")
            : string.Create(CultureInfo.CurrentCulture, $"{total.Minutes}:{total.Seconds:00}");
    }
}
