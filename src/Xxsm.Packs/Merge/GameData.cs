using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Merge;

/// <summary>Everything known about one game: the pack, the user's overlay, and the merged view.</summary>
public sealed class GameData
{
    private readonly Dictionary<string, MergedVariant> _byInternalName;
    private readonly Dictionary<string, IReadOnlyList<MergedVariant>> _families;

    /// <summary>Creates the merged read model.</summary>
    /// <param name="gameId">The game.</param>
    /// <param name="game">The game definition, including the attribute schema.</param>
    /// <param name="pack">The pack this was merged from, or null when there is no pack installed.</param>
    /// <param name="overlay">The overlay that was applied.</param>
    /// <param name="variants">Every variant, merged and derived.</param>
    /// <param name="ignoredHashes">Hashes the pack or overlay denies, lowercase.</param>
    /// <param name="diagnostics">Everything noticed while loading and merging.</param>
    public GameData(
        string gameId,
        GameDefinition game,
        GamePack? pack,
        PackOverlay overlay,
        IReadOnlyList<MergedVariant> variants,
        IReadOnlySet<string> ignoredHashes,
        IReadOnlyList<PackDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(ignoredHashes);
        ArgumentNullException.ThrowIfNull(diagnostics);

        GameId = gameId;
        Game = game;
        Pack = pack;
        Overlay = overlay;
        Variants = variants;
        IgnoredHashes = ignoredHashes;
        Diagnostics = diagnostics;

        _byInternalName = variants.ToDictionary(v => v.InternalName, StringComparer.OrdinalIgnoreCase);
        _families = variants
            .GroupBy(v => v.FamilyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, IReadOnlyList<MergedVariant> (g) => [.. g], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The game.</summary>
    public string GameId { get; }

    /// <summary>The game definition. The UI builds its filter chips from its attributes.</summary>
    public GameDefinition Game { get; }

    /// <summary>The installed pack, or null when the user has only their own overlay.</summary>
    public GamePack? Pack { get; }

    /// <summary>The user's overlay as it was read.</summary>
    public PackOverlay Overlay { get; }

    /// <summary>Every variant, merged. Includes hidden ones — filtering is the caller's job.</summary>
    public IReadOnlyList<MergedVariant> Variants { get; }

    /// <summary>Hashes the pack or overlay denies, lowercase; never indexed, on top of fan-out pruning.</summary>
    public IReadOnlySet<string> IgnoredHashes { get; }

    /// <summary>Everything the loader and the merge noticed, worst first.</summary>
    public IReadOnlyList<PackDiagnostic> Diagnostics { get; }

    /// <summary>The variants a user would see in the grid, with hidden ones removed.</summary>
    public IEnumerable<MergedVariant> VisibleVariants => Variants.Where(v => !v.Hidden);

    /// <summary>Every distinct family id.</summary>
    public IEnumerable<string> FamilyIds => _families.Keys;

    /// <summary>Finds a variant by internal name, case-insensitively.</summary>
    /// <param name="internalName">The id to look up.</param>
    /// <returns>The variant, or null.</returns>
    public MergedVariant? Find(string internalName) =>
        string.IsNullOrWhiteSpace(internalName) ? null : _byInternalName.GetValueOrDefault(internalName);

    /// <summary>Returns every member of a family, including its base.</summary>
    /// <param name="familyId">The family id, which is the base character's internal name.</param>
    /// <returns>The family's members, or an empty list.</returns>
    public IReadOnlyList<MergedVariant> GetFamily(string familyId) =>
        string.IsNullOrWhiteSpace(familyId) ? [] : _families.GetValueOrDefault(familyId) ?? [];
}
