using System.Text.Json.Serialization;
using Xxsm.Core.Hashes;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Sorting;

/// <summary>One reference from a hash to the variant that claims it.</summary>
/// <param name="VariantId">The variant's internal name.</param>
/// <param name="Kind">Which 3DMigoto field the hash came from. This is what it scores by.</param>
/// <param name="Component">The upstream component name, for the audit trail. Often empty.</param>
public readonly record struct SortHashRef(string VariantId, HashKind Kind, string Component);

/// <summary>Why a hash was moved out of the identifying index.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<HashPruneReason>))]
public enum HashPruneReason
{
    /// <summary>The pack or the user's overlay named it in <c>ignoredHashes</c>.</summary>
    Ignored,

    /// <summary>It spans more variants than the ambiguity threshold, so it identifies none.</summary>
    FanOut,

    /// <summary>Every reference to it is a <see cref="HashKind.RootVs"/> shader hash, shared by all.</summary>
    SharedShader,
}

/// <summary>A hash that was pruned, and why. Diagnostics only; it never scores.</summary>
/// <param name="Hash">The hash, lowercase.</param>
/// <param name="Reason">Why it was pruned.</param>
/// <param name="VariantCount">How many distinct variants referenced it.</param>
public sealed record PrunedHash(string Hash, HashPruneReason Reason, int VariantCount);

/// <summary>The immutable hash-to-variant index the sorter scores against, built once per pack load.</summary>
public sealed class SortIndex
{
    private readonly Dictionary<string, IReadOnlyList<SortHashRef>> _identifying;
    private readonly Dictionary<string, PrunedHash> _pruned;

    private SortIndex(
        GameData game,
        SortSettings settings,
        Dictionary<string, IReadOnlyList<SortHashRef>> identifying,
        Dictionary<string, PrunedHash> pruned)
    {
        Game = game;
        Settings = settings;
        _identifying = identifying;
        _pruned = pruned;
    }

    /// <summary>The merged pack and overlay this was built from.</summary>
    public GameData Game { get; }

    /// <summary>The thresholds this index was pruned with.</summary>
    public SortSettings Settings { get; }

    /// <summary>How many hashes can still identify a variant.</summary>
    public int IdentifyingCount => _identifying.Count;

    /// <summary>Every hash that was pruned, with the reason. Sorted by hash.</summary>
    public IReadOnlyList<PrunedHash> Pruned => [.. _pruned.Values.OrderBy(p => p.Hash, StringComparer.Ordinal)];

    /// <summary>Builds the index from merged game data. Variants with no hashes contribute nothing.</summary>
    public static SortIndex Build(GameData game, SortSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        var tuning = settings ?? SortSettings.Default;

        var collected = new Dictionary<string, List<SortHashRef>>(StringComparer.OrdinalIgnoreCase);

        foreach (var variant in game.Variants)
        {
            foreach (var entry in variant.Hashes)
            {
                // An empty or malformed value is absent: never trust the loader to have filtered it.
                var hash = HashText.Normalize(entry.Hash);
                if (hash is null)
                {
                    continue;
                }

                if (!collected.TryGetValue(hash, out var refs))
                {
                    refs = [];
                    collected[hash] = refs;
                }

                refs.Add(new SortHashRef(variant.InternalName, entry.Kind, entry.Component ?? string.Empty));
            }
        }

        var identifying = new Dictionary<string, IReadOnlyList<SortHashRef>>(StringComparer.OrdinalIgnoreCase);
        var pruned = new Dictionary<string, PrunedHash>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hash, refs) in collected)
        {
            var variantCount = refs
                .Select(r => r.VariantId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            // An ignored hash that also fans out is reported as ignored: a person decided that.
            if (game.IgnoredHashes.Contains(hash))
            {
                pruned[hash] = new PrunedHash(hash, HashPruneReason.Ignored, variantCount);
            }
            else if (variantCount > tuning.AmbiguityThreshold)
            {
                pruned[hash] = new PrunedHash(hash, HashPruneReason.FanOut, variantCount);
            }
            else if (refs.TrueForAll(r => r.Kind == HashKind.RootVs))
            {
                pruned[hash] = new PrunedHash(hash, HashPruneReason.SharedShader, variantCount);
            }
            else
            {
                identifying[hash] = refs;
            }
        }

        return new SortIndex(game, tuning, identifying, pruned);
    }

    /// <summary>Looks up the variants that claim a hash.</summary>
    /// <param name="hash">The hash to look up. Normalised before lookup.</param>
    /// <returns>The references, or an empty list when the hash is unknown or was pruned.</returns>
    public IReadOnlyList<SortHashRef> Lookup(string hash)
    {
        var normalized = HashText.Normalize(hash);
        return normalized is null ? [] : _identifying.GetValueOrDefault(normalized) ?? [];
    }

    /// <summary>Whether a hash was pruned, and why.</summary>
    /// <returns>The pruning record, or null when the hash was not pruned.</returns>
    public PrunedHash? PruningOf(string hash)
    {
        var normalized = HashText.Normalize(hash);
        return normalized is null ? null : _pruned.GetValueOrDefault(normalized);
    }
}
