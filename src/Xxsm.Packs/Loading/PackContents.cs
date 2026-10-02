namespace Xxsm.Packs.Loading;

/// <summary>What a pack holds, as its card says: characters, skins, and how many still wait for hashes.</summary>
/// <param name="Characters">Variants that are not a skin of another.</param>
/// <param name="Skins">Variants with a base character.</param>
/// <param name="WaitingForHashes">Variants the pack marks <c>hashesPending</c>.</param>
public sealed record PackContents(int Characters, int Skins, int WaitingForHashes)
{
    /// <summary>Counts a loaded pack.</summary>
    public static PackContents Of(GamePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var skins = pack.Variants.Count(v => v.BaseCharacterId is { Length: > 0 });
        var waiting = pack.Variants.Count(v => v.HashesPending == true);

        return new PackContents(pack.Variants.Count - skins, skins, waiting);
    }
}
