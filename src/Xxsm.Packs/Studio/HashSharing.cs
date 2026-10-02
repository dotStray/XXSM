using Xxsm.Core.Hashes;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Studio;

/// <summary>Which hashes a draft's characters share, and who carries a hash, by the validator's rule.</summary>
public static class HashSharing
{
    /// <summary>Every set of families that shares hashes, with the hashes they share.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="settings">The sorter's thresholds. Defaults to <see cref="SortSettings.Default"/>.</param>
    public static IReadOnlyList<SharedHashGroup> Groups(PackDraft draft, SortSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return
        [
            .. Classify(draft, settings).Shared
                .OrderByDescending(g => g.Hashes.Count)
                .ThenBy(g => g.Families[0], StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The hashes shared by exactly these families, each with every character carrying it.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="families">The families, by their heads' internal names, in any order and capitals.</param>
    /// <param name="settings">The sorter's thresholds. Defaults to <see cref="SortSettings.Default"/>.</param>
    /// <returns>The hashes, in order; empty when those families share none.</returns>
    public static IReadOnlyList<SharedHash> Between(
        PackDraft draft, IReadOnlyCollection<string> families, SortSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(families);

        var wanted = families.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classification = Classify(draft, settings);

        var group = classification.Shared.FirstOrDefault(g => g.Families.Count == wanted.Count && g.Families.All(wanted.Contains));

        return group is null
            ? []
            : [.. group.Hashes.Select(hash => new SharedHash(hash, classification.Carriers[hash]))];
    }

    /// <summary>Every character that carries a hash.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="hash">The hash, in any capitals.</param>
    /// <returns>The characters by internal name, in order; empty when none does or it is not a hash.</returns>
    public static IReadOnlyList<string> Carriers(PackDraft draft, string hash)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return HashText.Normalize(hash) is { } normal
               && Classify(draft, null).Carriers.TryGetValue(normal, out var carriers)
            ? carriers
            : [];
    }

    /// <summary>Whether the pack already ignores a hash.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="hash">The hash, in any capitals.</param>
    /// <returns>True when it is on the pack's list of hashes to ignore.</returns>
    public static bool IsIgnored(PackDraft draft, string hash)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return HashText.Normalize(hash) is { } normal && Classify(draft, null).Ignored.Contains(normal);
    }

    /// <summary>The family a character belongs to: its base's internal name, or its own.</summary>
    /// <returns>The family's head, or null when there is no such character.</returns>
    public static string? FamilyOf(PackDraft draft, string internalName)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var kept = PackValidator.KeptVariants(draft);

        return kept.TryGetValue(internalName, out var variant) ? PackValidator.FamilyOf(variant, kept) : null;
    }

    private static HashClassification Classify(PackDraft draft, SortSettings? settings) =>
        PackValidator.ClassifyHashes(draft, PackValidator.KeptVariants(draft), settings ?? SortSettings.Default, null);
}

/// <summary>Families that share hashes, and the hashes they share.</summary>
/// <param name="Families">The families, by their heads' internal names, in order.</param>
/// <param name="Hashes">The hashes, in order.</param>
public sealed record SharedHashGroup(IReadOnlyList<string> Families, IReadOnlyList<string> Hashes);

/// <summary>A hash, and every character that carries it.</summary>
/// <param name="Hash">The hash.</param>
/// <param name="Characters">The characters, by internal name, in order.</param>
public sealed record SharedHash(string Hash, IReadOnlyList<string> Characters);
