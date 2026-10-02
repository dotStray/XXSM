namespace Xxsm.Core.Mods;

/// <summary>A name a mod could be given, and whether anything already has it.</summary>
public sealed record ModNameProposal
{
    /// <summary>The name that was asked for.</summary>
    public required string Wanted { get; init; }

    /// <summary>A free name: <see cref="Wanted"/> itself, or it with a number added.</summary>
    public required string Name { get; init; }

    /// <summary>Whether <see cref="Wanted"/> was free, so nothing had to be suggested.</summary>
    public required bool IsFree { get; init; }

    /// <summary>The existing name <see cref="Wanted"/> clashes with, perhaps differing by case; null if free.</summary>
    public string? CollidesWith { get; init; }
}

/// <summary>XXSM's one naming rule: <c>Foo</c>, then <c>Foo (2)</c>, <c>Foo (3)</c>.</summary>
public static class ModNames
{
    /// <summary>How many suffixed names to try before giving up.</summary>
    public const int MaxAttempts = 10_000;

    /// <summary>Finds a name none of <paramref name="taken"/> is using, ignoring case.</summary>
    /// <param name="wanted">The name the user asked for.</param>
    /// <param name="taken">The names already in use.</param>
    /// <returns>The name asked for and one that is free, which may be the same name.</returns>
    public static ModNameProposal ProposeAmong(string wanted, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);

        var names = taken.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();

        return Propose(
            wanted,
            candidate => names.Find(name => Io.PathComparer.AreNamesEqual(name, candidate)));
    }

    /// <summary>Finds a free name, adding <c> (2)</c>, <c> (3)</c> … until one is.</summary>
    /// <param name="wanted">The name the user asked for.</param>
    /// <param name="findCollision">Returns the existing name a candidate clashes with, spelt as it is, or null when
    /// free.</param>
    /// <returns>The name asked for and one that is free, which may be the same name.</returns>
    /// <exception cref="ArgumentException"><paramref name="wanted"/> is empty.</exception>
    /// <exception cref="ModOperationException"><see cref="MaxAttempts"/> names in a row were all taken.</exception>
    public static ModNameProposal Propose(string wanted, Func<string, string?> findCollision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wanted);
        ArgumentNullException.ThrowIfNull(findCollision);

        var firstCollision = findCollision(wanted);

        if (firstCollision is null)
        {
            return new ModNameProposal { Wanted = wanted, Name = wanted, IsFree = true };
        }

        for (var attempt = 2; attempt < MaxAttempts; attempt++)
        {
            // The suffix goes on the end, so a DISABLED_ prefix stays where it is.
            var candidate =
                $"{wanted} ({attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)})";

            if (findCollision(candidate) is null)
            {
                return new ModNameProposal
                {
                    Wanted = wanted,
                    Name = candidate,
                    IsFree = false,
                    CollidesWith = firstCollision,
                };
            }
        }

        throw new ModOperationException(
            $"Could not find a free name for '{wanted}' after " +
            $"{MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} tries.",
            wanted);
    }
}
