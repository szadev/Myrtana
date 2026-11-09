using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>
    /// What a content pack may contain, registered by the modules at start-up:
    /// <list type="bullet">
    /// <item><b>kinds</b>: a folder of JSON files holding arrays of one definition type (<c>dialogs/*.json</c> → DialogEntryDef);</item>
    /// <item><b>families</b>: an abstract base class whose JSON objects name their concrete class in a discriminator
    /// property (<c>{"op": "Say", …}</c> → SayCommand). The discriminator must be the first property of the object.</item>
    /// </list>
    /// A new module or pack adds its own kinds and family members without editing existing code.
    /// </summary>
    public sealed class ContentSchema
    {
        readonly List<ContentKind> _kinds = new List<ContentKind>();
        readonly Dictionary<Type, TypeFamily> _families = new Dictionary<Type, TypeFamily>();

        public IReadOnlyList<ContentKind> Kinds => _kinds;

        public void AddKind<T>(string folder) where T : class, IDefinition
        {
            foreach (var k in _kinds)
                if (k.Folder == folder || k.Type == typeof(T)) throw new InvalidOperationException($"Content kind {folder}/{typeof(T).Name} is already registered.");
            _kinds.Add(new ContentKind(folder, typeof(T)));
        }

        public void AddFamily<TBase>(string discriminatorProperty) where TBase : class =>
            _families.Add(typeof(TBase), new TypeFamily(discriminatorProperty));

        public void AddMember<TBase, TDerived>(string name) where TBase : class where TDerived : TBase
        {
            if (!_families.TryGetValue(typeof(TBase), out var family))
                throw new InvalidOperationException($"{typeof(TBase).Name} is not a registered family.");
            family.Add(name, typeof(TDerived));
        }

        public bool TryGetFamily(Type baseType, out TypeFamily family) => _families.TryGetValue(baseType, out family);
    }

    public sealed class ContentKind
    {
        public ContentKind(string folder, Type type)
        {
            Folder = folder;
            Type = type;
        }

        public string Folder { get; }
        public Type Type { get; }
    }

    public sealed class TypeFamily
    {
        readonly List<KeyValuePair<string, Type>> _members = new List<KeyValuePair<string, Type>>();

        public TypeFamily(string discriminatorProperty) => DiscriminatorProperty = discriminatorProperty;

        public string DiscriminatorProperty { get; }
        public IReadOnlyList<KeyValuePair<string, Type>> Members => _members;

        public void Add(string name, Type type)
        {
            foreach (var m in _members)
                if (m.Key == name || m.Value == type) throw new InvalidOperationException($"Family member {name}/{type.Name} is already registered.");
            _members.Add(new KeyValuePair<string, Type>(name, type));
        }
    }
}
