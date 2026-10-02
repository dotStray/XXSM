using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Xxsm.Core.GameBanana;

/// <summary>Reads a GameBanana mod page address, as pasted from a browser, and says which mod it names.</summary>
/// <remarks>A <c>/dl/&lt;id&gt;</c> address names a file, not a mod, and is refused; see <see
/// cref="IsFileAddress"/>.</remarks>
public static partial class GameBananaUrl
{
    /// <summary>The site's host, without a scheme.</summary>
    public const string Host = "gamebanana.com";

    /// <summary>The longest text this will look at, in characters.</summary>
    public const int LengthLimit = 4096;

    /// <summary>Whether an address names a single downloadable file rather than a mod page.</summary>
    /// <param name="text">The address to look at. Null or blank is false.</param>
    /// <returns>True for a <c>gamebanana.com/dl/&lt;id&gt;</c> address.</returns>
    public static bool IsFileAddress(string? text) =>
        text is { Length: > 0 } && text.Length <= LengthLimit && FileAddress().IsMatch(text);

    /// <summary>Reads the mod id out of a GameBanana mod page address.</summary>
    /// <param name="text">The address, with or without a scheme. Surrounding whitespace is ignored.</param>
    /// <param name="modId">The mod id, or 0 when this returns false.</param>
    /// <returns>True when <paramref name="text"/> is a GameBanana mod address and nothing else.</returns>
    public static bool TryParseModId([NotNullWhen(true)] string? text, out long modId)
    {
        modId = 0;

        if (text is null || text.Length > LengthLimit)
        {
            return false;
        }

        var match = ModAddress().Match(text.Trim());

        return match.Success
            && match.Index == 0
            && long.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out modId)
            && modId > 0;
    }

    /// <summary>Finds the first mod id in a piece of text, which may be an address with prose around it.</summary>
    /// <param name="text">Anything: a pasted address, a sentence containing one, or null.</param>
    /// <returns>The mod id, or null when the text holds no GameBanana mod address.</returns>
    public static long? FindModId(string? text)
    {
        if (text is null || text.Length > LengthLimit)
        {
            return null;
        }

        var match = ModAddress().Match(text);

        return match.Success
            && long.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var modId)
            && modId > 0
                ? modId
                : null;
    }

    /// <summary>The canonical page address for a mod id, <c>https://gamebanana.com/mods/541886</c>.</summary>
    /// <param name="modId">The mod id.</param>
    /// <returns>The address.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modId"/> is not positive.</exception>
    public static string ForMod(long modId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(modId);

        return $"https://{Host}/mods/{modId.ToString(CultureInfo.InvariantCulture)}";
    }

    // The /download/ form carries the mod id, not a file id.
    [GeneratedRegex(
        @"(?:https?://)?(?:[a-z0-9-]+\.)*gamebanana\.com/mods/(?:download/)?([0-9]{1,18})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex ModAddress();

    [GeneratedRegex(
        @"(?:https?://)?(?:[a-z0-9-]+\.)*gamebanana\.com/dl/([0-9]{1,18})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex FileAddress();
}
