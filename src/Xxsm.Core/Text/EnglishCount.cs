using System.Globalization;

namespace Xxsm.Core.Text;

/// <summary>An English count and its noun, for logs, Core messages and the CLI, which are not translated.</summary>
public static class EnglishCount
{
    /// <summary>The count and the singular or plural noun: <c>1 variant</c>, <c>134 variants</c>.</summary>
    public static string Plural(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
