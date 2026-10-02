using Serilog;
using Xxsm.Core.Hashes;
using Xxsm.Core.Mods;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Studio;

/// <summary>The characters whose own hashes would stop identifying them if these hashes were ignored.</summary>
/// <remarks>Measured with the real sorter, with and without the hashes ignored.</remarks>
public interface IHashIgnoreCost
{
    /// <summary>Works out what ignoring some hashes would cost. Changes nothing.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="hashes">The hashes to consider ignoring, in any capitals.</param>
    /// <param name="settings">The sorter's thresholds. Defaults to <see cref="SortSettings.Default"/>.</param>
    HashIgnoreCostReport Measure(PackDraft draft, IReadOnlyCollection<string> hashes, SortSettings? settings = null);
}

/// <summary>The default <see cref="IHashIgnoreCost"/>, through the pack trial and the real sorter.</summary>
public sealed class HashIgnoreCost(IPackTrial trial, ILogger logger) : IHashIgnoreCost
{
    /// <summary>The notional mod folder each character's hashes are sorted in, named so no name match helps.</summary>
    private const string ProbeRoot = "xxsm-ignore-check";

    private readonly IPackTrial _trial = trial;
    private readonly ILogger _logger = logger.ForContext<HashIgnoreCost>();

    /// <inheritdoc />
    public HashIgnoreCostReport Measure(PackDraft draft, IReadOnlyCollection<string> hashes, SortSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(hashes);

        var rules = settings ?? SortSettings.Default;
        var already = (draft.Hashes.IgnoredHashes ?? [])
            .Select(HashText.Normalize)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var adding = hashes
            .Select(HashText.Normalize)
            .OfType<string>()
            .Where(h => !already.Contains(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (adding.Count == 0)
        {
            return new HashIgnoreCostReport([], 0, 0);
        }

        var ignored = draft with
        {
            Hashes = draft.Hashes with { IgnoredHashes = [.. already, .. adding] },
        };

        var before = SortIndex.Build(_trial.ToGameData(draft), rules);
        var after = SortIndex.Build(_trial.ToGameData(ignored), rules);
        var sorter = new ModSorter(_logger);

        var owned = (draft.Hashes.Entries ?? [])
            .Where(entry => HashText.Normalize(entry.Hash) is not null)
            .GroupBy(entry => entry.Variant, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)[.. group.Select(e => e.Hash).Distinct(StringComparer.OrdinalIgnoreCase)],
                StringComparer.OrdinalIgnoreCase);

        var losses = new List<HashIgnoreLoss>();
        var checkedCount = 0;

        foreach (var variant in PackValidator.KeptVariants(draft).Values.OrderBy(v => v.InternalName, StringComparer.OrdinalIgnoreCase))
        {
            if (!owned.TryGetValue(variant.InternalName, out var own) || own.Count == 0)
            {
                continue;
            }

            checkedCount++;

            var was = sorter.Sort(before, Probe(own));

            if (!Identifies(was, variant.InternalName))
            {
                continue;
            }

            var now = sorter.Sort(after, Probe(own));

            if (!Identifies(now, variant.InternalName))
            {
                losses.Add(new HashIgnoreLoss(variant.InternalName, now.VariantId, now.IsAmbiguous));
            }
        }

        _logger.Information(
            "Ignoring {Adding} hashes in {GameId} would cost {Losses} of {Checked} characters their identification",
            adding.Count,
            draft.GameId,
            losses.Count,
            checkedCount);

        return new HashIgnoreCostReport(losses, checkedCount, adding.Count);
    }

    /// <summary>Whether a decision put a character's own hashes back on that variant, not just its family.</summary>
    private static bool Identifies(SortDecision decision, string internalName) =>
        decision.IsResolved && string.Equals(decision.VariantId, internalName, StringComparison.OrdinalIgnoreCase);

    private static ModSignals Probe(IReadOnlyList<string> hashes) => new()
    {
        Root = ProbeRoot,
        Hashes = hashes,
        OverrideSections = [],
        AssetNames = [],
        IniFiles = ["mod.ini"],
        HashJsonFiles = [],
        FilesSeen = 1,
        IniBytesRead = 0,
        LimitsReached = ModScanLimit.None,
        Diagnostics = [],
    };
}

/// <summary>What ignoring some hashes would cost.</summary>
/// <param name="Losses">The characters whose own hashes would stop finding them. Empty when it costs nothing.</param>
/// <param name="Checked">How many characters were put through the sorter — those with any hashes.</param>
/// <param name="Adding">How many of the hashes are not on the list already.</param>
public sealed record HashIgnoreCostReport(IReadOnlyList<HashIgnoreLoss> Losses, int Checked, int Adding)
{
    /// <summary>Whether ignoring them costs nothing the sorter can see.</summary>
    public bool IsFree => Losses.Count == 0;
}

/// <summary>One character that would stop being found by its own hashes.</summary>
/// <param name="InternalName">The character.</param>
/// <param name="GoesToInstead">Where its mods would go instead, or null for <c>Others/</c>.</param>
/// <param name="IsAmbiguous">Whether it would tie with another family rather than simply score nothing.</param>
public sealed record HashIgnoreLoss(string InternalName, string? GoesToInstead, bool IsAmbiguous);
