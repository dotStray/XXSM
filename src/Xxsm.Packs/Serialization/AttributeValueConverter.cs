using System.Text.Json;
using System.Text.Json.Serialization;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Serialization;

/// <summary>Reads an attribute value written as a string, a number or a string array; writes that shape back.</summary>
/// <remarks>Any other token, <c>null</c> included, throws.</remarks>
public sealed class AttributeValueConverter : JsonConverter<AttributeValue>
{
    /// <summary>True, so that a <c>null</c> reaches <see cref="Read"/> and is refused there.</summary>
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override AttributeValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return AttributeValue.FromString(reader.GetString() ?? string.Empty);

            case JsonTokenType.Number:
                return AttributeValue.FromNumber(reader.GetDouble());

            case JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString() ?? string.Empty,
                        JsonTokenType.Number => reader.GetDouble()
                            .ToString("0.################", System.Globalization.CultureInfo.InvariantCulture),
                        _ => throw new JsonException(
                            $"An attribute array may only contain strings or numbers, not {reader.TokenType}."),
                    });
                }

                return AttributeValue.FromArray(items);

            default:
                throw new JsonException(
                    $"An attribute value must be a string, a number, or an array of them, not {reader.TokenType}.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AttributeValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.IsArray)
        {
            writer.WriteStartArray();
            foreach (var id in value.Ids)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            return;
        }

        if (value.Number is { } number)
        {
            writer.WriteNumberValue(number);
            return;
        }

        writer.WriteStringValue(value.SingleId ?? string.Empty);
    }
}
