namespace Xxsm.Packs.Model;

/// <summary>The overlay's per-variant field names, as spelled in the JSON and in <c>lockedFields</c>.</summary>
public static class OverlayFields
{
    /// <summary>Every field, in the order a user interface should show them.</summary>
    public static readonly string[] All =
    [
        DisplayName,
        BaseCharacterId,
        IsDefaultVariant,
        Aliases,
        ModFilesName,
        Image,
        ReleaseDate,
        Attributes,
        Hidden,
        Notes,
        Hashes,
    ];

    /// <summary>The name shown in the grid.</summary>
    public const string DisplayName = "displayName";

    /// <summary>The base character this is an outfit of.</summary>
    public const string BaseCharacterId = "baseCharacterId";

    /// <summary>Whether this is the family's fallback.</summary>
    public const string IsDefaultVariant = "isDefaultVariant";

    /// <summary>Extra strings the name-fallback matcher accepts.</summary>
    public const string Aliases = "aliases";

    /// <summary>The filename-fallback prefix and Mods sub-folder name.</summary>
    public const string ModFilesName = "modFilesName";

    /// <summary>The portrait.</summary>
    public const string Image = "image";

    /// <summary>The in-game release date.</summary>
    public const string ReleaseDate = "releaseDate";

    /// <summary>Attribute values keyed by attribute id.</summary>
    public const string Attributes = "attributes";

    /// <summary>Whether the variant is hidden from the grid.</summary>
    public const string Hidden = "hidden";

    /// <summary>Free-text notes.</summary>
    public const string Notes = "notes";

    /// <summary>The pseudo-field for a variant's whole hash set, valid in <c>lockedFields</c>.</summary>
    public const string Hashes = "hashes";

    /// <summary>Whether a name is one of the overlay's known per-variant fields.</summary>
    /// <param name="field">The name to test, in any capitalisation.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool IsKnown(string? field) =>
        field is not null && All.Contains(field, StringComparer.OrdinalIgnoreCase);

    /// <summary>A field's value in an overlay entry, by its exact name; null for the hashes or an unknown name.</summary>
    public static object? ValueOf(OverlayVariant entry, string field)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return field switch
        {
            DisplayName => entry.DisplayName,
            BaseCharacterId => entry.BaseCharacterId,
            IsDefaultVariant => entry.IsDefaultVariant,
            Aliases => entry.Aliases,
            ModFilesName => entry.ModFilesName,
            Image => entry.Image,
            ReleaseDate => entry.ReleaseDate,
            Attributes => entry.Attributes,
            Hidden => entry.Hidden,
            Notes => entry.Notes,
            _ => null,
        };
    }

    /// <summary>Every field's value in an overlay entry, in the order of <see cref="All"/>, without the hashes.</summary>
    public static IEnumerable<(string Field, object? Value)> ValuesOf(OverlayVariant entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return All.Where(field => field != Hashes).Select(field => (field, ValueOf(entry, field)));
    }
}
