using Xxsm.Packs.Model;

namespace Xxsm.Packs.Sorting;

/// <summary>The measured constants the auto-sort algorithm is tuned by, each overridable.</summary>
public sealed record SortSettings
{
    // Declared before Default on purpose: static initialisers run in textual order.
    private static readonly Dictionary<HashKind, int> DefaultWeights = new()
    {
        [HashKind.Ib] = 10,
        [HashKind.PositionVb] = 8,
        [HashKind.BlendVb] = 8,
        [HashKind.TexcoordVb] = 6,
        [HashKind.DrawVb] = 6,

        [HashKind.Unknown] = 6,

        [HashKind.Texture] = 1,

        [HashKind.RootVs] = 0,
    };

    /// <summary>The measured defaults.</summary>
    public static SortSettings Default { get; } = new();

    /// <summary>How many distinct variants a hash may span before it stops identifying anything. Default 8.</summary>
    public int AmbiguityThreshold { get; init; } = 8;

    /// <summary>The score a family must reach to be accepted at all. Default 10.</summary>
    public int MinScore { get; init; } = 10;

    /// <summary>How far the winning family must beat the runner-up from a different family. Default 1.25.</summary>
    public double MarginRatio { get; init; } = 1.25;

    /// <summary>The confidence cap when the family is certain but the member is the default. Default 0.7.</summary>
    public double DefaultVariantConfidenceCeiling { get; init; } = 0.7;

    /// <summary>What one matched hash of each kind adds to a variant's score. Overridable.</summary>
    public IReadOnlyDictionary<HashKind, int> Weights { get; init; } = DefaultWeights;

    /// <summary>The score one matched hash of the given kind contributes.</summary>
    /// <param name="kind">The field the hash came from.</param>
    public int Weight(HashKind kind) =>
        Weights.TryGetValue(kind, out var weight) ? weight : DefaultWeights[HashKind.Unknown];
}
