using System.Globalization;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;
using Xxsm.Packs.Merge;

namespace Xxsm.Packs.Sorting;

/// <summary>The default <see cref="IModSorter"/>: manual filing, hashes, file names, names, else Others.</summary>
/// <remarks>The first tier with a result wins; an ambiguous hash result stops the chain.</remarks>
public sealed class ModSorter(ILogger logger) : IModSorter
{
    private readonly ILogger _logger = logger.ForContext<ModSorter>();

    /// <inheritdoc />
    public SortDecision Sort(SortIndex index, ModSignals signals, SortRequest request = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(signals);

        var extracted = signals.Hashes;
        var prunedMatches = extracted
            .Select(index.PruningOf)
            .OfType<PrunedHash>()
            .OrderBy(p => p.Hash, StringComparer.Ordinal)
            .ToList();

        // Weak evidence is carried through, so a mod in Others still offers "did you mean" candidates.
        var decision =
            TryManual(index, signals, request, extracted, prunedMatches)
            ?? TryHash(index, signals, request, extracted, prunedMatches, out var candidates)
            ?? TryFilename(index, signals, extracted, prunedMatches, candidates)
            ?? TryName(index, signals, request, extracted, prunedMatches, candidates)
            ?? Unsorted(
                signals,
                extracted,
                prunedMatches,
                candidates,
                candidates.Count > 0
                    ? "Some hashes matched, but not strongly enough to name a character."
                    : "Nothing in the mod identified a variant.");

        _logger.Debug(
            "Sorted {Root} to {Variant} by {DecidedBy} at {Confidence:F2}",
            signals.Root,
            decision.VariantId ?? "Others",
            decision.DecidedBy,
            decision.Confidence);

        return decision;
    }

    // Tier 0: a person already decided.

    private static SortDecision? TryManual(
        SortIndex index,
        ModSignals signals,
        SortRequest request,
        IReadOnlyList<string> extracted,
        List<PrunedHash> prunedMatches)
    {
        if (string.IsNullOrWhiteSpace(request.VariantOverride))
        {
            return null;
        }

        var variant = index.Game.Find(request.VariantOverride);

        // Others is a place a person can file a mod in too.
        if (variant is null &&
            PathComparer.AreNamesEqual(request.VariantOverride, ModsFolderLayout.UnsortedFolderName))
        {
            var others = Unsorted(
                signals,
                extracted,
                prunedMatches,
                [],
                $"Filed by hand in {ModsFolderLayout.UnsortedFolderName}; auto-sort does not override a person.");

            return others with { DecidedBy = SortDecidedBy.Manual, Confidence = 1.0 };
        }

        if (variant is null)
        {
            // The override names a variant this pack no longer has: fall through, and say so.
            return null;
        }

        return Resolved(
            signals,
            variant,
            SortDecidedBy.Manual,
            confidence: 1.0,
            isDefaultVariantFallback: false,
            memberDecidedByName: false,
            runnerUp: null,
            candidates: [],
            extracted,
            prunedMatches,
            $"Filed by hand to {variant.InternalName}; auto-sort does not override a person.");
    }

    // Tier 1: hash scoring.

    private SortDecision? TryHash(
        SortIndex index,
        ModSignals signals,
        SortRequest request,
        IReadOnlyList<string> extracted,
        List<PrunedHash> prunedMatches,
        out List<SortCandidate> candidates)
    {
        candidates = [];
        var settings = index.Settings;
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matched = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var hash in extracted)
        {
            var refs = index.Lookup(hash);
            if (refs.Count == 0)
            {
                continue;
            }

            // Each distinct hash counts once per variant, at its best weight there.
            var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in refs)
            {
                var weight = settings.Weight(reference.Kind);
                best[reference.VariantId] = Math.Max(best.GetValueOrDefault(reference.VariantId), weight);
            }

            foreach (var (variantId, weight) in best)
            {
                scores[variantId] = scores.GetValueOrDefault(variantId) + weight;
                if (!matched.TryGetValue(variantId, out var hashes))
                {
                    hashes = new SortedSet<string>(StringComparer.Ordinal);
                    matched[variantId] = hashes;
                }

                hashes.Add(hash);
            }
        }

        if (scores.Count == 0)
        {
            return null;
        }

        var familyOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var familyScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (variantId, score) in scores)
        {
            var family = index.Game.Find(variantId)?.FamilyId ?? variantId;
            familyOf[variantId] = family;

            // Stage one compares families as wholes, so a base and its outfit are not split.
            familyScores[family] = familyScores.GetValueOrDefault(family) + score;
        }

        candidates = BuildCandidates(index, scores, familyOf, familyScores, matched);

        var rankedFamilies = familyScores
            .OrderByDescending(f => f.Value)
            .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (bestFamily, bestScore) = rankedFamilies[0];
        var runnerUpScore = rankedFamilies.Count > 1 ? rankedFamilies[1].Value : 0;

        if (bestScore < settings.MinScore)
        {
            _logger.Debug(
                "Hash evidence for {Root} peaked at {Score}, below the minimum of {MinScore}",
                signals.Root,
                bestScore,
                settings.MinScore);

            return null;
        }

        if (runnerUpScore > 0 && bestScore < runnerUpScore * settings.MarginRatio)
        {
            var tied = rankedFamilies
                .Where(f => f.Value * settings.MarginRatio > bestScore)
                .Select(f => f.Key)
                .ToList();

            return Ambiguous(
                signals,
                extracted,
                prunedMatches,
                candidates,
                $"{string.Join(", ", tied)} scored too closely to tell apart " +
                $"({string.Join(", ", rankedFamilies.Take(tied.Count).Select(f => $"{f.Key} {f.Value}"))}). " +
                "Two unrelated families matching means the mod, the pack or the archive is wrong, " +
                "so nothing is guessed.");
        }

        var confidence = Confidence(bestScore, runnerUpScore);
        var member = ResolveWithinFamily(
            index, bestFamily, scores, matched, NameSources(signals, request));

        var winner = member.Winner;

        // Neither a default fallback nor a name-broken tie identifies the outfit: both are capped.
        if (member.IsFallback || member.ByName)
        {
            confidence = Math.Min(confidence, settings.DefaultVariantConfidenceCeiling);
        }

        var runnerUp = candidates.FirstOrDefault(c => !IdEquals(c.VariantId, winner.InternalName));

        var explanation =
            member.IsFallback
                ? $"{bestFamily} is certain (score {bestScore}), but no outfit had a hash of its own, " +
                  "so this is the family's default variant and a guess."
            : member.ByName
                ? $"{bestFamily} is certain (score {bestScore}), but its members share the hashes that " +
                  $"matched, so the mod's own name picked {winner.InternalName} from within the family."
                : $"{winner.InternalName} matched {EnglishCount.Plural(member.UniqueMatches, "hash", "hashes")} that no " +
                  $"other member of {bestFamily} claims, out of " +
                  $"{(matched.GetValueOrDefault(winner.InternalName)?.Count ?? 0)
                      .ToString(CultureInfo.InvariantCulture)} matched.";

        return Resolved(
            signals,
            winner,
            SortDecidedBy.Hash,
            confidence,
            member.IsFallback,
            member.ByName,
            runnerUp?.VariantId,
            candidates,
            extracted,
            prunedMatches,
            explanation);
    }

    /// <summary>Formats a count with a singular or plural noun, for the explanation.</summary>

    private static List<SortCandidate> BuildCandidates(
        SortIndex index,
        Dictionary<string, int> scores,
        Dictionary<string, string> familyOf,
        Dictionary<string, int> familyScores,
        Dictionary<string, SortedSet<string>> matched)
    {
        var candidates = new List<SortCandidate>(scores.Count);
        foreach (var (variantId, score) in scores)
        {
            var family = familyOf[variantId];
            var hashes = matched.GetValueOrDefault(variantId);
            candidates.Add(new SortCandidate(
                index.Game.Find(variantId)?.InternalName ?? variantId,
                family,
                score,
                familyScores[family],
                hashes is null ? [] : [.. hashes],
                CountFamilyUnique(index, family, variantId, hashes)));
        }

        return
        [
            .. candidates
                .OrderByDescending(c => c.FamilyScore)
                .ThenByDescending(c => c.Score)
                .ThenByDescending(c => c.FamilyUniqueMatches)
                .ThenBy(c => c.VariantId, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Stage two: the family member with the most hashes no sibling claims; on a tie, the default.</summary>
    private static MemberResolution ResolveWithinFamily(
        SortIndex index,
        string familyId,
        Dictionary<string, int> scores,
        Dictionary<string, SortedSet<string>> matched,
        List<string> nameSources)
    {
        var family = index.Game.GetFamily(familyId);
        var scored = family.Where(m => scores.GetValueOrDefault(m.InternalName) > 0).ToList();

        var unique = scored.ToDictionary(
            m => m.InternalName,
            m => CountFamilyUnique(index, familyId, m.InternalName, matched.GetValueOrDefault(m.InternalName)),
            StringComparer.OrdinalIgnoreCase);

        var top = unique.Count == 0 ? 0 : unique.Values.Max();
        var leaders = scored.Where(m => unique[m.InternalName] == top).ToList();

        if (top > 0 && leaders.Count == 1)
        {
            return new MemberResolution(leaders[0], top, false, false);
        }

        // The hashes settled the family but not the member: ask the name, within this family only.
        var byName = MatchNameWithinFamily(leaders.Count > 1 ? leaders : scored, nameSources);
        if (byName is not null)
        {
            return new MemberResolution(byName, unique.GetValueOrDefault(byName.InternalName), false, true);
        }

        var fallback =
            family.FirstOrDefault(m => m.IsDefaultVariant)
            ?? family.FirstOrDefault(m => IdEquals(m.InternalName, familyId))
            ?? scored.OrderByDescending(m => scores[m.InternalName])
                .ThenBy(m => m.InternalName, StringComparer.OrdinalIgnoreCase)
                .First();

        return new MemberResolution(fallback, unique.GetValueOrDefault(fallback.InternalName), true, false);
    }

    /// <summary>The one family member the mod's names point at, or null for none or several.</summary>
    private static MergedVariant? MatchNameWithinFamily(
        List<MergedVariant> members, List<string> nameSources)
    {
        if (members.Count < 2 || nameSources.Count == 0)
        {
            return null;
        }

        var keys = BuildNameKeys(members);

        foreach (var source in nameSources)
        {
            var normalized = SortName.Normalize(source);
            if (normalized.Length > 0 && keys.TryGetValue(normalized, out var exact) && exact.Count == 1)
            {
                return exact[0];
            }
        }

        foreach (var level in new[] { SortName.ChunkLevel, SortName.PartLevel })
        {
            var hits = new List<MergedVariant>();
            foreach (var source in nameSources)
            {
                foreach (var token in SortName.Tokenize(source))
                {
                    if (token.Level != level
                        || token.Text.Length < SortName.MinimumTokenLength
                        || !keys.TryGetValue(token.Key, out var found)
                        || found.Count != 1)
                    {
                        continue;
                    }

                    if (!hits.Any(h => IdEquals(h.InternalName, found[0].InternalName)))
                    {
                        hits.Add(found[0]);
                    }
                }
            }

            if (hits.Count == 1)
            {
                return hits[0];
            }

            if (hits.Count > 1)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Which member of a family won stage two, and on what evidence.</summary>
    private readonly record struct MemberResolution(
        MergedVariant Winner, int UniqueMatches, bool IsFallback, bool ByName);

    /// <summary>How many of a variant's matched hashes exactly one member of its family claims.</summary>
    private static int CountFamilyUnique(
        SortIndex index, string familyId, string variantId, SortedSet<string>? matched)
    {
        if (matched is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var hash in matched)
        {
            var claimants = index.Lookup(hash)
                .Where(r => IdEquals(index.Game.Find(r.VariantId)?.FamilyId ?? r.VariantId, familyId))
                .Select(r => r.VariantId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (claimants.Count == 1 && IdEquals(claimants[0], variantId))
            {
                count++;
            }
        }

        return count;
    }

    // Tier 2: file-name prefix.

    private static SortDecision? TryFilename(
        SortIndex index,
        ModSignals signals,
        IReadOnlyList<string> extracted,
        List<PrunedHash> prunedMatches,
        List<SortCandidate> candidates)
    {
        if (signals.AssetNames.Count == 0)
        {
            return null;
        }

        var best = new List<MergedVariant>();
        var bestLength = 0;

        foreach (var variant in index.Game.Variants)
        {
            var prefix = variant.ModFilesName;
            if (string.IsNullOrEmpty(prefix) || prefix.Length < bestLength)
            {
                continue;
            }

            var hit = signals.AssetNames.Any(
                asset => asset.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!hit)
            {
                continue;
            }

            if (prefix.Length > bestLength)
            {
                bestLength = prefix.Length;
                best.Clear();
            }

            best.Add(variant);
        }

        if (best.Count == 0)
        {
            return null;
        }

        if (best.Count > 1)
        {
            return Ambiguous(
                signals,
                extracted,
                prunedMatches,
                candidates,
                $"Asset filenames matched {string.Join(", ", best.Select(v => v.InternalName))} " +
                "equally well, so nothing is guessed.");
        }

        var winner = best[0];
        return Resolved(
            signals,
            winner,
            SortDecidedBy.Filename,
            confidence: 0.6,
            isDefaultVariantFallback: false,
            memberDecidedByName: false,
            runnerUp: null,
            candidates,
            extracted,
            prunedMatches,
            $"No usable hashes, but an asset file starts with '{winner.ModFilesName}'.");
    }

    // Tier 3: name matching.

    /// <summary>Everything about a mod that carries a name: the folder, the archive, then INI sections.</summary>
    private static List<string> NameSources(ModSignals signals, SortRequest request)
    {
        var sources = new List<string>();

        var folder = request.FolderName ?? Path.GetFileName(signals.Root.TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            sources.Add(folder);
        }

        if (!string.IsNullOrWhiteSpace(request.ArchiveName))
        {
            sources.Add(request.ArchiveName);
        }

        sources.AddRange(signals.OverrideSections.Select(s => s.Name));
        return sources;
    }

    private static SortDecision? TryName(
        SortIndex index,
        ModSignals signals,
        SortRequest request,
        IReadOnlyList<string> extracted,
        List<PrunedHash> prunedMatches,
        List<SortCandidate> candidates)
    {
        var sources = NameSources(signals, request);
        if (sources.Count == 0)
        {
            return null;
        }

        var keys = BuildNameKeys(index.Game.Variants);

        // An exact whole-name match, across every source, before any containment match.
        foreach (var source in sources)
        {
            var normalized = SortName.Normalize(source);
            if (normalized.Length > 0 && keys.TryGetValue(normalized, out var exact) && exact.Count == 1)
            {
                var winner = exact.Single();
                return Resolved(
                    signals,
                    winner,
                    SortDecidedBy.Name,
                    confidence: 0.75,
                    isDefaultVariantFallback: false,
                    memberDecidedByName: false,
                    runnerUp: null,
                    candidates,
                    extracted,
                    prunedMatches,
                    $"'{source}' is exactly {winner.InternalName}'s name.");
            }
        }

        // Whole chunks first, then their camel-case pieces: "JeanCN_v3" is JeanCN, not Jean.
        var hits = new Dictionary<string, (MergedVariant Variant, string Token, string Source)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var level in new[] { SortName.ChunkLevel, SortName.PartLevel })
        {
            foreach (var source in sources)
            {
                foreach (var token in SortName.Tokenize(source))
                {
                    if (token.Level != level
                        || token.Text.Length < SortName.MinimumTokenLength
                        || !keys.TryGetValue(token.Key, out var found)
                        || found.Count != 1)
                    {
                        continue;
                    }

                    var variant = found.Single();
                    hits[variant.InternalName] = (variant, token.Text, source);
                }
            }

            if (hits.Count > 0)
            {
                break;
            }
        }

        if (hits.Count == 0)
        {
            return null;
        }

        if (hits.Count > 1)
        {
            // A wrong confident answer is worse than Others.
            return Ambiguous(
                signals,
                extracted,
                prunedMatches,
                candidates,
                $"The name matched {string.Join(", ", hits.Keys.Order(StringComparer.OrdinalIgnoreCase))} " +
                "and there is no way to choose between them.");
        }

        var (matchedVariant, matchedToken, matchedSource) = hits.Values.Single();
        return Resolved(
            signals,
            matchedVariant,
            SortDecidedBy.Name,
            confidence: 0.65,
            isDefaultVariantFallback: false,
            memberDecidedByName: false,
            runnerUp: null,
            candidates,
            extracted,
            prunedMatches,
            $"'{matchedSource}' contains the whole word '{matchedToken}', which only " +
            $"{matchedVariant.InternalName} answers to.");
    }

    /// <summary>Every normalised name a variant answers to; a key more than one claims is never matched.</summary>
    private static Dictionary<string, List<MergedVariant>> BuildNameKeys(
        IEnumerable<MergedVariant> variants)
    {
        var keys = new Dictionary<string, List<MergedVariant>>(StringComparer.Ordinal);

        void Add(string? name, MergedVariant variant)
        {
            var key = SortName.Normalize(name);
            if (key.Length == 0)
            {
                return;
            }

            if (!keys.TryGetValue(key, out var owners))
            {
                owners = [];
                keys[key] = owners;
            }

            if (!owners.Any(o => IdEquals(o.InternalName, variant.InternalName)))
            {
                owners.Add(variant);
            }
        }

        foreach (var variant in variants)
        {
            Add(variant.InternalName, variant);
            Add(variant.DisplayName, variant);
            foreach (var alias in variant.Aliases)
            {
                Add(alias, variant);
            }
        }

        return keys;
    }

    // Building the record.

    private static SortDecision Resolved(
        ModSignals signals,
        MergedVariant variant,
        SortDecidedBy decidedBy,
        double confidence,
        bool isDefaultVariantFallback,
        bool memberDecidedByName,
        string? runnerUp,
        IReadOnlyList<SortCandidate> candidates,
        IReadOnlyList<string> extracted,
        List<PrunedHash> prunedMatches,
        string explanation) => new()
        {
            Root = signals.Root,
            VariantId = variant.InternalName,
            FamilyId = variant.FamilyId,
            DecidedBy = decidedBy,
            Confidence = Math.Round(confidence, 4, MidpointRounding.AwayFromZero),
            IsAmbiguous = false,
            IsDefaultVariantFallback = isDefaultVariantFallback,
            MemberDecidedByName = memberDecidedByName,
            MatchedVariantHasNoHashes = !variant.HasHashes,
            RunnerUpVariantId = runnerUp,
            Candidates = candidates,
            ExtractedHashes = extracted,
            PrunedMatches = prunedMatches,
            Explanation = variant.HasHashes
                ? explanation
                : explanation + $" {variant.InternalName} has no hashes yet, so only its name could find it.",
        };

    private static SortDecision Ambiguous(
        ModSignals signals,
        IReadOnlyList<string> extracted,
        List<PrunedHash> prunedMatches,
        IReadOnlyList<SortCandidate> candidates,
        string explanation) => new()
        {
            Root = signals.Root,
            VariantId = null,
            FamilyId = null,
            DecidedBy = SortDecidedBy.Unsorted,
            Confidence = 0,
            IsAmbiguous = true,
            IsDefaultVariantFallback = false,
            MemberDecidedByName = false,
            MatchedVariantHasNoHashes = false,
            RunnerUpVariantId = null,
            Candidates = candidates,
            ExtractedHashes = extracted,
            PrunedMatches = prunedMatches,
            Explanation = explanation,
        };

    private static SortDecision Unsorted(
        ModSignals signals,
        IReadOnlyList<string> extracted,
        List<PrunedHash> prunedMatches,
        IReadOnlyList<SortCandidate> candidates,
        string explanation) => new()
        {
            Root = signals.Root,
            VariantId = null,
            FamilyId = null,
            DecidedBy = SortDecidedBy.Unsorted,
            Confidence = 0,
            IsAmbiguous = false,
            IsDefaultVariantFallback = false,
            MemberDecidedByName = false,
            MatchedVariantHasNoHashes = false,
            RunnerUpVariantId = null,
            Candidates = candidates,
            ExtractedHashes = extracted,
            PrunedMatches = prunedMatches,
            Explanation = prunedMatches.Count > 0
                ? explanation + (prunedMatches.Count == 1
                    ? " Its one hash is shared too widely to identify anything."
                    : $" {prunedMatches.Count} of its hashes are shared too widely to identify anything.")
                : explanation,
        };

    private static double Confidence(int score, int runnerUp)
    {
        var total = score + runnerUp;
        var raw = total == 0 ? 1.0 : (double)score / total;
        return Math.Clamp(raw, 0.5, 1.0);
    }

    /// <summary>Compares two variant ids ignoring case; ids, not paths, so not <c>PathComparer</c>.</summary>
    private static bool IdEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
