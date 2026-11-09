using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Myrtana.Core
{
    /// <summary>
    /// JSON settings shared by every reader and writer of content packs. System.Text.Json is used because it ships with
    /// both runtimes: .NET 10 (G3Cli, tests) and Unity 6.6 (BCL extension, System.Text.Json 8), so no package is needed.
    /// Conventions: camelCase properties, enums as strings, ids as plain strings, readable (unescaped) Cyrillic.
    /// </summary>
    public static class ContentJson
    {
        public static JsonSerializerOptions CreateOptions(ContentSchema schema, bool indented = false)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info => ApplyFamily(schema, info));
            var options = new JsonSerializerOptions
            {
                TypeInfoResolver = resolver,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = indented,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            options.Converters.Add(new IdJsonConverterFactory());
            options.Converters.Add(new ActorIdJsonConverter());
            options.Converters.Add(new ActorRefJsonConverter());
            options.Converters.Add(new TextIdJsonConverter());
            return options;
        }

        static void ApplyFamily(ContentSchema schema, JsonTypeInfo info)
        {
            if (!schema.TryGetFamily(info.Type, out var family)) return;
            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = family.DiscriminatorProperty,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };
            foreach (var member in family.Members) polymorphism.DerivedTypes.Add(new JsonDerivedType(member.Value, member.Key));
            info.PolymorphismOptions = polymorphism;
        }
    }
}
