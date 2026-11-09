using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>Primitive writer a save section is written with. The format behind it can change without touching modules.</summary>
    public interface ISaveWriter
    {
        void WriteBool(bool value);
        void WriteInt32(int value);
        void WriteUInt64(ulong value);
        void WriteDouble(double value);
        /// <summary>fix-157: 4 bytes, for values that are floats anyway (poses).</summary>
        void WriteSingle(float value);
        /// <summary>Null is allowed and read back as null.</summary>
        void WriteString(string value);
    }

    public interface ISaveReader
    {
        bool ReadBool();
        int ReadInt32();
        ulong ReadUInt64();
        double ReadDouble();
        float ReadSingle();
        string ReadString();
    }

    /// <summary>
    /// A module that owns saveable state. Each participant writes one named, versioned section; <see cref="Read"/>
    /// receives the version the section was written with, so old saves can be migrated. Sections are read into a
    /// freshly created new-game state, so <see cref="Read"/> only has to add what the save holds.
    /// </summary>
    public interface ISaveParticipant
    {
        string Section { get; }
        int Version { get; }
        void Write(ISaveWriter writer);
        void Read(ISaveReader reader, int version);
    }

    /// <summary>
    /// fix-171: a section whose state can be copied on the main thread (cheap: values, no encoding) and written from the copy on
    /// another thread (the crash-recovery snapshot, <see cref="SaveFile.Prepare"/>): <see cref="WriteCaptured"/> must write exactly
    /// the bytes <see cref="ISaveParticipant.Write"/> would have written when <see cref="Capture"/> ran. The capture may be reused by
    /// the next <see cref="Capture"/> (one snapshot in flight at a time).
    /// </summary>
    public interface ISaveCapture
    {
        object Capture();
        void WriteCaptured(object captured, ISaveWriter writer);
    }

    /// <summary>
    /// Something that forbids saving right now (a conversation, a fight, falling). Saving is allowed when no blocker
    /// gives a reason.
    /// </summary>
    public interface ISaveBlocker
    {
        /// <summary>Why the game cannot be saved now (a short key such as "dialog"), or null.</summary>
        string SaveBlockReason { get; }
    }

    /// <summary>A blocker from a condition, for modules that do not want to implement the interface themselves.</summary>
    public sealed class SaveBlocker : ISaveBlocker
    {
        readonly Func<bool> _blocks;
        readonly string _reason;

        SaveBlocker(Func<bool> blocks, string reason)
        {
            _blocks = blocks;
            _reason = reason;
        }

        public static ISaveBlocker When(Func<bool> blocks, string reason) => new SaveBlocker(blocks, reason);

        public string SaveBlockReason => _blocks() ? _reason : null;
    }

    /// <summary>Helpers for the collections most sections are made of.</summary>
    public static class SaveIo
    {
        public static void WriteStrings(this ISaveWriter w, ICollection<string> values)
        {
            w.WriteInt32(values.Count);
            foreach (var v in values) w.WriteString(v);
        }

        public static List<string> ReadStrings(this ISaveReader r)
        {
            int n = r.ReadInt32();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString());
            return list;
        }

        /// <summary>Writes the strings sorted ordinally, so equal sets always give identical bytes.</summary>
        public static void WriteSortedStrings(this ISaveWriter w, IEnumerable<string> values)
        {
            var sorted = new List<string>(values);
            sorted.Sort(StringComparer.Ordinal);
            w.WriteStrings(sorted);
        }
    }
}
