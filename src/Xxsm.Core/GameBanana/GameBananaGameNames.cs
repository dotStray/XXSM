namespace Xxsm.Core.GameBanana;

/// <summary>Whether the game GameBanana lists a mod under is a pack's game, by its names.</summary>
public static class GameBananaGameNames
{
    /// <summary>
    /// Whether the two name the same game: their names alike in letters and digits, ignoring capitals,
    /// spaces and punctuation, or their short names alike ignoring capitals.
    /// </summary>
    /// <param name="gameBananaName">The game's name on GameBanana.</param>
    /// <param name="gameBananaShortName">GameBanana's abbreviation for it, or null.</param>
    /// <param name="displayName">The pack's display name.</param>
    /// <param name="shortName">The pack's short name, or null.</param>
    public static bool IsSame(string? gameBananaName, string? gameBananaShortName, string? displayName, string? shortName)
    {
        if (gameBananaShortName is { } theirs && shortName is { } ours
            && !string.IsNullOrWhiteSpace(theirs)
            && string.Equals(theirs.Trim(), ours.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var left = Letters(gameBananaName);

        return left.Length > 0 && string.Equals(left, Letters(displayName), StringComparison.Ordinal);
    }

    /// <summary>A name's letters and digits alone, lower-cased: "Honkai: Star Rail" is "honkaistarrail".</summary>
    public static string Letters(string? name) =>
        name is null ? string.Empty : new string([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
