using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myrtana.Rules
{
    /// <summary>
    /// A rule value with a variant for the Alternative Balancing option. In JSON either a plain value (same in both
    /// modes) or <c>{"default": 100, "alternativeBalancing": 150}</c>.
    /// </summary>
    [JsonConverter(typeof(BalancedJsonConverterFactory))]
    public readonly struct Balanced<T>
    {
        public Balanced(T @default, T alternativeBalancing)
        {
            Default = @default;
            AlternativeBalancing = alternativeBalancing;
        }

        public Balanced(T both) : this(both, both) { }

        public T Default { get; }
        public T AlternativeBalancing { get; }

        public T For(RuleOptions options) => options.AlternativeBalancing ? AlternativeBalancing : Default;

        public static implicit operator Balanced<T>(T both) => new Balanced<T>(both);
    }

    public sealed class BalancedJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Balanced<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]));

        sealed class Converter<T> : JsonConverter<Balanced<T>>
        {
            const string DefaultName = "default", AlternativeName = "alternativeBalancing";

            public override Balanced<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartObject) return new Balanced<T>(JsonSerializer.Deserialize<T>(ref reader, options));
                T def = default, alt = default;
                bool hasAlt = false;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string name = reader.GetString();
                    reader.Read();
                    var value = JsonSerializer.Deserialize<T>(ref reader, options);
                    if (name == DefaultName) def = value;
                    else if (name == AlternativeName)
                    {
                        alt = value;
                        hasAlt = true;
                    }
                }
                return new Balanced<T>(def, hasAlt ? alt : def);
            }

            public override void Write(Utf8JsonWriter writer, Balanced<T> value, JsonSerializerOptions options)
            {
                if (Equals(value.Default, value.AlternativeBalancing))
                {
                    JsonSerializer.Serialize(writer, value.Default, options);
                    return;
                }
                writer.WriteStartObject();
                writer.WritePropertyName(DefaultName);
                JsonSerializer.Serialize(writer, value.Default, options);
                writer.WritePropertyName(AlternativeName);
                JsonSerializer.Serialize(writer, value.AlternativeBalancing, options);
                writer.WriteEndObject();
            }
        }
    }
}
