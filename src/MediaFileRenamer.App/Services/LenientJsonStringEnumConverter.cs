using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaFileRenamer.App.Services;

internal sealed class LenientJsonStringEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsEnum;
    }

    public override JsonConverter CreateConverter(
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return (JsonConverter)Activator.CreateInstance(
            typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var textValue)
                && Enum.IsDefined(textValue))
            {
                return textValue;
            }

            if (reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt64(out var number))
            {
                var numericValue = (TEnum)Enum.ToObject(typeof(TEnum), number);
                if (Enum.IsDefined(numericValue))
                {
                    return numericValue;
                }
            }

            return default;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
