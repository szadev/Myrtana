using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Myrtana.Core
{
    /// <summary>Base for value types that are stored as one JSON string.</summary>
    public abstract class StringValueJsonConverter<T> : JsonConverter<T>
    {
        protected abstract T FromString(string value);
        protected abstract string ToString(T value);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? default : FromString(reader.GetString());

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            string s = ToString(value);
            if (s == null) writer.WriteNullValue();
            else writer.WriteStringValue(s);
        }
    }

    /// <summary>Creates the string converter for every <see cref="Id{T}"/>.</summary>
    public sealed class IdJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Id<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(IdConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]));

        sealed class IdConverter<T> : StringValueJsonConverter<Id<T>>
        {
            protected override Id<T> FromString(string value) => new Id<T>(value);
            protected override string ToString(Id<T> value) => value.Name;
        }
    }

    public sealed class ActorIdJsonConverter : StringValueJsonConverter<ActorId>
    {
        protected override ActorId FromString(string value) => new ActorId(value);
        protected override string ToString(ActorId value) => value.Name;
    }

    public sealed class ActorRefJsonConverter : StringValueJsonConverter<ActorRef>
    {
        protected override ActorRef FromString(string value) => new ActorRef(value);
        protected override string ToString(ActorRef value) => value.Value;
    }

    public sealed class TextIdJsonConverter : StringValueJsonConverter<TextId>
    {
        protected override TextId FromString(string value) => new TextId(value);
        protected override string ToString(TextId value) => value.Value;
    }
}
