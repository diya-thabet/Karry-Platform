using System.Text.Json;
using System.Text.Json.Serialization;

namespace Karry.SharedKernel.Units;

/// <summary>
/// A quantity on the wire (US-P1-UNI-01 AC4): <c>{"value": 18.562, "unit": "m3", "state": "loose"}</c>; <c>state</c> is
/// null for every unit but m³. The value is a JSON number read and written as decimal, never through double.
/// </summary>
public sealed class QuantityJsonConverter : JsonConverter<Quantity>
{
    public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A quantity is an object {value, unit, state}.");
        }

        decimal? value = null;
        string? unit = null;
        string? state = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "value" when reader.TokenType == JsonTokenType.Number:
                    value = reader.GetDecimal();
                    break;
                case "unit" when reader.TokenType == JsonTokenType.String:
                    unit = reader.GetString();
                    break;
                case "state" when reader.TokenType is JsonTokenType.String or JsonTokenType.Null:
                    state = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                default:
                    throw new JsonException($"Unexpected '{name}' in a quantity.");
            }
        }

        var parsedUnit = Unit.Find(unit) ?? throw new JsonException($"Unknown unit '{unit}'.");
        var parsedState = state is null ? (MaterialState?)null : MaterialStates.Find(state) ?? throw new JsonException($"Unknown material state '{state}'.");
        if (value is null || parsedUnit.HasState != parsedState.HasValue)
        {
            throw new JsonException("A quantity needs a value, and a state exactly when the unit is m3.");
        }

        return new Quantity(value.Value, parsedUnit, parsedState);
    }

    public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteString("unit", value.Unit.Code);
        if (value.State is { } state)
        {
            writer.WriteString("state", MaterialStates.Code(state));
        }
        else
        {
            writer.WriteNull("state");
        }

        writer.WriteEndObject();
    }
}
