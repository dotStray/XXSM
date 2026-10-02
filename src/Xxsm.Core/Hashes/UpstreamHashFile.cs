using System.Text.Json.Serialization;

namespace Xxsm.Core.Hashes;

/// <summary>One component object from an upstream <c>hash.json</c>. Read hashes through <see cref="Hashes"/>.</summary>
/// <remarks>Every string field may be <c>""</c>, meaning the component has no such hash.</remarks>
public sealed record UpstreamHashComponent
{
    /// <summary>Often <c>""</c>, or <c>Face</c> / <c>Hair</c> / <c>Body</c> / <c>Head</c>.</summary>
    [JsonPropertyName("component_name")]
    public string? ComponentName { get; init; }

    /// <summary>The vertex shader hash: shared by many characters, so worth nothing. Absent from Star Rail.</summary>
    [JsonPropertyName("root_vs")]
    public string? RootVs { get; init; }

    /// <summary>A hash present upstream only on alternate outfits. Read, not used.</summary>
    [JsonPropertyName("first_vs")]
    public string? FirstVs { get; init; }

    /// <summary>The draw vertex buffer hash.</summary>
    [JsonPropertyName("draw_vb")]
    public string? DrawVb { get; init; }

    /// <summary>The position vertex buffer hash.</summary>
    [JsonPropertyName("position_vb")]
    public string? PositionVb { get; init; }

    /// <summary>The blend vertex buffer hash.</summary>
    [JsonPropertyName("blend_vb")]
    public string? BlendVb { get; init; }

    /// <summary>The texture coordinate vertex buffer hash.</summary>
    [JsonPropertyName("texcoord_vb")]
    public string? TexcoordVb { get; init; }

    /// <summary>The index buffer hash — the strongest single identifier a mod carries.</summary>
    [JsonPropertyName("ib")]
    public string? Ib { get; init; }

    /// <summary>Per object index, <c>[kind, extension, hash]</c> triples; the counts may not match.</summary>
    [JsonPropertyName("texture_hashes")]
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>>? TextureHashes { get; init; }

    /// <summary>Every well-formed hash on this component, lowercase and deduplicated, in a stable order.</summary>
    public IReadOnlyList<string> Hashes()
    {
        var found = new List<string>();

        foreach (var candidate in new[] { Ib, PositionVb, BlendVb, TexcoordVb, DrawVb, RootVs, FirstVs })
        {
            var hash = HashText.Normalize(candidate);
            if (hash is not null && !found.Contains(hash, StringComparer.Ordinal))
            {
                found.Add(hash);
            }
        }

        foreach (var texture in Textures())
        {
            if (!found.Contains(texture.Hash, StringComparer.Ordinal))
            {
                found.Add(texture.Hash);
            }
        }

        return found;
    }

    /// <summary>The texture hashes, flattened, in file order, skipping malformed triples.</summary>
    public IReadOnlyList<UpstreamTextureHash> Textures()
    {
        var textures = new List<UpstreamTextureHash>();
        if (TextureHashes is null)
        {
            return textures;
        }

        for (var slot = 0; slot < TextureHashes.Count; slot++)
        {
            var group = TextureHashes[slot];
            if (group is null)
            {
                continue;
            }

            foreach (var entry in group)
            {
                if (entry is not { Count: 3 })
                {
                    continue;
                }

                var hash = HashText.Normalize(entry[2]);
                if (hash is not null)
                {
                    textures.Add(new UpstreamTextureHash(entry[0], entry[1], hash, slot));
                }
            }
        }

        return textures;
    }
}

/// <summary>One texture hash from an upstream <c>hash.json</c>.</summary>
/// <param name="Kind">The upstream texture kind, <c>Diffuse</c>, <c>LightMap</c> and others; an open set.</param>
/// <param name="Extension">The file extension the texture is stored with, normally <c>.dds</c>.</param>
/// <param name="Hash">The hash, lowercase.</param>
/// <param name="Slot">Which object-index group it came from.</param>
public readonly record struct UpstreamTextureHash(string Kind, string Extension, string Hash, int Slot);
