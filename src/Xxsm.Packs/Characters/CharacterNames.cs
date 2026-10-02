using System.Globalization;
using System.Text;
using Xxsm.Core;

namespace Xxsm.Packs.Characters;

/// <summary>A character's internal name as it was asked for and as it can actually be used.</summary>
/// <param name="Wanted">The internal name derived from what the user typed.</param>
/// <param name="Name">An internal name nothing else is using.</param>
/// <param name="CollidesWith">The existing name <paramref name="Wanted"/> clashed with, or null when free.</param>
public sealed record CharacterNameProposal(string Wanted, string Name, string? CollidesWith)
{
    /// <summary>Whether the name asked for was free, so nothing had to be adjusted.</summary>
    public bool IsFree => CollidesWith is null;
}

/// <summary>Derives a character's internal name, <c>[A-Za-z0-9_-]</c> only, from the name a user typed.</summary>
public static class CharacterNames
{
    /// <summary>How many suffixed names to try before giving up.</summary>
    public const int MaxAttempts = 10_000;

    /// <summary>The internal name used when a display name keeps nothing, such as one with no Latin letters.</summary>
    public const string Fallback = "Character";

    /// <summary>Turns a display name into a candidate internal name, dropping every other character.</summary>
    /// <remarks>Drops rather than transliterates, as the sorter's name matching does; the two must agree.</remarks>
    /// <param name="displayName">The name the user typed.</param>
    /// <returns>The slug, or <see cref="Fallback"/> when nothing usable survives. Never empty.</returns>
    public static string Slugify(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Fallback;
        }

        var slug = new StringBuilder(displayName.Length);

        foreach (var character in displayName)
        {
            if (IsAllowed(character))
            {
                slug.Append(character);
            }
        }

        return slug.Length == 0 ? Fallback : slug.ToString();
    }

    /// <summary>Whether a character may appear in an internal name.</summary>
    /// <returns><see langword="true"/> when it is <c>[A-Za-z0-9_-]</c>.</returns>
    public static bool IsAllowed(char character) =>
        character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';

    /// <summary>Finds an internal name for a display name that nothing else has taken.</summary>
    /// <param name="displayName">The name the user typed.</param>
    /// <param name="findCollision">The existing name a candidate clashes with, ignoring case, or null when
    /// free.</param>
    /// <exception cref="ModOperationException"><see cref="MaxAttempts"/> names in a row were all taken.</exception>
    public static CharacterNameProposal Propose(string? displayName, Func<string, string?> findCollision)
    {
        ArgumentNullException.ThrowIfNull(findCollision);

        var wanted = Slugify(displayName);
        var collision = findCollision(wanted);

        if (collision is null)
        {
            return new CharacterNameProposal(wanted, wanted, CollidesWith: null);
        }

        for (var attempt = 2; attempt < MaxAttempts; attempt++)
        {
            var candidate = wanted + attempt.ToString(CultureInfo.InvariantCulture);

            if (findCollision(candidate) is null)
            {
                return new CharacterNameProposal(wanted, candidate, collision);
            }
        }

        throw new ModOperationException(
            $"Could not find a free internal name for '{wanted}' after " +
            $"{MaxAttempts.ToString(CultureInfo.InvariantCulture)} tries.",
            wanted);
    }

    /// <summary>Finds an internal name free of every name in a set.</summary>
    /// <param name="displayName">The name the user typed.</param>
    /// <param name="taken">The internal names already in use.</param>
    public static CharacterNameProposal ProposeAmong(string? displayName, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var names = taken.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();

        return Propose(
            displayName,
            candidate => names.Find(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)));
    }
}
