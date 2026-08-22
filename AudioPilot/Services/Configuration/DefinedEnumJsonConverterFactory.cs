using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioPilot.Services.Configuration
{
    /// <summary>Validates enum values before model setters can normalize them, preserving each enum's existing JSON representation.</summary>
    internal sealed class DefinedEnumJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(DefinedEnumJsonConverter<>).MakeGenericType(typeToConvert), nonPublic: true)!;

        private sealed class DefinedEnumJsonConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
        {
            private static readonly JsonConverter<TEnum> Converter = (JsonConverter<TEnum>)JsonSerializerOptions.Default.GetConverter(typeof(TEnum));

            public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                TEnum value = Converter.Read(ref reader, typeToConvert, options);
                if (!Enum.IsDefined(value))
                {
                    throw new JsonException($"Undefined {typeof(TEnum).Name} value: {value}.");
                }
                return value;
            }

            public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            {
                if (!Enum.IsDefined(value))
                {
                    throw new JsonException($"Undefined {typeof(TEnum).Name} value: {value}.");
                }
                Converter.Write(writer, value, options);
            }
        }
    }
}
