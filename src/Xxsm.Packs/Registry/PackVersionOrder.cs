using System.Globalization;

namespace Xxsm.Packs.Registry;

/// <summary>Orders pack versions, oldest first: dot-separated parts compared as numbers where they are.</summary>
public sealed class PackVersionOrder : IComparer<string>
{
    /// <summary>The one instance.</summary>
    public static PackVersionOrder Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = x.Split('.');
        var right = y.Split('.');

        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var compared = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                           && long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var b)
                ? a.CompareTo(b)
                : string.CompareOrdinal(left[i], right[i]);

            if (compared != 0)
            {
                return compared;
            }
        }

        var byLength = left.Length.CompareTo(right.Length);

        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }
}
