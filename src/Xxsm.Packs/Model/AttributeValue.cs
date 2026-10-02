using System.Globalization;
using System.Text.Json.Serialization;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Model;

/// <summary>The value of one declared attribute on a variant: an element, a rarity, a list of regions.</summary>
/// <remarks>A string, a number or an array in the pack; all three read into <see cref="Ids"/>.</remarks>
[JsonConverter(typeof(AttributeValueConverter))]
public sealed record AttributeValue
{
    private AttributeValue(IReadOnlyList<string> ids, double? number, bool isArray)
    {
        Ids = ids;
        Number = number;
        IsArray = isArray;
    }

    /// <summary>The value or values as comparable ids, a number in invariant form. Never null; may be empty.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>The numeric value when the pack wrote a number, otherwise null.</summary>
    public double? Number { get; }

    /// <summary>Whether the pack wrote an array, which is preserved for round-tripping.</summary>
    public bool IsArray { get; }

    /// <summary>The single value, or null when there is not exactly one.</summary>
    public string? SingleId => Ids.Count == 1 ? Ids[0] : null;

    /// <summary>Creates a value from a single string.</summary>
    public static AttributeValue FromString(string id) => new([id], null, isArray: false);

    /// <summary>Creates a value from a number.</summary>
    public static AttributeValue FromNumber(double value) =>
        new([value.ToString("0.################", CultureInfo.InvariantCulture)], value, isArray: false);

    /// <summary>Creates a value from a list.</summary>
    public static AttributeValue FromArray(IEnumerable<string> ids) =>
        new([.. ids], null, isArray: true);

    /// <summary>Whether this value includes the id, compared ignoring case.</summary>
    public bool Contains(string id) =>
        Ids.Any(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public override string ToString() => string.Join(", ", Ids);
}
