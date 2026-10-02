using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xxsm.Packs.Serialization;

/// <summary>Writes an enum as its name in camel case: <c>hash</c>, not <c>Hash</c> and not <c>1</c>.</summary>
public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>Creates the converter.</summary>
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}
