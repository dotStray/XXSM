namespace Xxsm.Packs.Studio;

/// <summary>How much a proposed skin link can be trusted.</summary>
public enum LinkConfidence
{
    /// <summary>More often wrong than right. Shown first, for a person to decide.</summary>
    Low = 0,

    /// <summary>Plausible either way.</summary>
    Medium = 1,

    /// <summary>Very likely right, still shown and still editable.</summary>
    High = 2,
}

/// <summary>A guess at whether a character is an outfit of another, and why.</summary>
/// <param name="InternalName">The character the guess is about.</param>
/// <param name="BaseCharacterId">The character it is guessed to be an outfit of, or null for a base.</param>
/// <param name="Confidence">How far to trust it.</param>
/// <param name="Reason">One plain sentence saying what the guess rests on.</param>
public sealed record SkinLinkProposal(
    string InternalName,
    string? BaseCharacterId,
    LinkConfidence Confidence,
    string Reason)
{
    /// <summary>Whether the guess is that this is an outfit.</summary>
    public bool IsSkin => BaseCharacterId is not null;
}

/// <summary>Proposes base/skin links from names alone, as an editable list, graded by confidence.</summary>
public static class SkinLinks
{
    /// <summary>Proposes a link for every name.</summary>
    /// <param name="names">Every character name in play, imported and already in the draft.</param>
    /// <param name="containers">For a folder inside another character's folder, that folder's name; proposed as
    /// that character's with medium confidence.</param>
    public static IReadOnlyList<SkinLinkProposal> Propose(
        IEnumerable<string> names,
        IReadOnlyDictionary<string, string>? containers = null)
    {
        ArgumentNullException.ThrowIfNull(names);

        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                distinct.Add(name);
            }
        }

        var direct = new Dictionary<string, SkinLinkProposal>(StringComparer.Ordinal);

        foreach (var name in distinct)
        {
            direct[name] = ProposeOne(name, distinct, containers);
        }

        return [.. distinct.Select(name => Collapse(direct[name], direct))];
    }

    private static SkinLinkProposal ProposeOne(
        string name,
        List<string> names,
        IReadOnlyDictionary<string, string>? containers)
    {
        if (containers is not null
            && containers.TryGetValue(name, out var container)
            && names.Contains(container, StringComparer.Ordinal)
            && !string.Equals(container, name, StringComparison.Ordinal))
        {
            return new SkinLinkProposal(
                name,
                container,
                LinkConfidence.Medium,
                $"It was found inside {container}'s folder, which usually means an outfit or an alternate form.");
        }

        string? best = null;

        foreach (var candidate in names)
        {
            if (candidate.Length < name.Length
                && name.StartsWith(candidate, StringComparison.Ordinal)
                && (best is null || candidate.Length > best.Length))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return new SkinLinkProposal(
                name, null, LinkConfidence.High, "No other character's name begins its name.");
        }

        var suffix = name[best.Length..];
        var (confidence, reason) = Classify(best, suffix);

        return new SkinLinkProposal(name, best, confidence, reason);
    }

    private static (LinkConfidence Confidence, string Reason) Classify(string baseName, string suffix)
    {
        var first = suffix[0];

        if (char.IsAsciiDigit(first))
        {
            return (LinkConfidence.Low,
                $"Its name is {baseName} followed by the number '{suffix}', which is more often a different " +
                "character than an outfit.");
        }

        if (char.IsLower(first))
        {
            return (LinkConfidence.Low,
                $"'{baseName}' is only the start of a longer word in its name, so they may not be related at all.");
        }

        if (char.IsUpper(first) && suffix.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return (LinkConfidence.Medium,
                $"Its name is {baseName} followed by '{suffix}', which could be an outfit or a different character.");
        }

        return (LinkConfidence.High, $"Its name is {baseName} followed by '{suffix}'.");
    }

    /// <summary>Points a skin of a skin at the root of its chain, keeping the weakest confidence along it.</summary>
    private static SkinLinkProposal Collapse(SkinLinkProposal proposal, Dictionary<string, SkinLinkProposal> direct)
    {
        if (proposal.BaseCharacterId is null)
        {
            return proposal;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { proposal.InternalName };
        var root = proposal.BaseCharacterId;
        var confidence = proposal.Confidence;

        while (direct.TryGetValue(root, out var step) && step.BaseCharacterId is { } next && visited.Add(root))
        {
            confidence = (LinkConfidence)Math.Min((int)confidence, (int)step.Confidence);
            root = next;
        }

        return root == proposal.BaseCharacterId
            ? proposal
            : proposal with
            {
                BaseCharacterId = root,
                Confidence = confidence,
                Reason = proposal.Reason + $" That one is itself an outfit of {root}, so this is linked to {root}.",
            };
    }
}
