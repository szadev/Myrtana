using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace G3.Unity.Editor.Remaster
{
    /// <summary>
    /// fix-187: keeps runtime render-pipeline tweaks out of the project's assets. The game changes the shared URP assets at
    /// runtime (G3WorldV2 shadows/STP, lighting quality, the options' shadow distance / render scale / upscaler, G3RenderTuning,
    /// G3GrdFallback, the SSAO boost on the renderer feature); each restores its own values, but in an order that is not
    /// guaranteed (one snapshot taken while another's value is applied writes that value back), so in the editor the values
    /// of a Play session stayed in memory and the next save wrote them to disk (Demo_2026-10-22a shipped
    /// PC_RPAsset m_ShadowDistance 90, committed 50). In a player nothing persists, so the guard is editor-only:
    /// <list type="bullet">
    /// <item>entering Play: every pipeline asset of the quality levels / graphics settings, their renderer data and renderer
    /// features are snapshot (JSON in SessionState, survives a domain reload); back in Edit mode they are restored and their
    /// dirty flag reset to what it was;</item>
    /// <item><see cref="CheckAgainstDisk"/> (DemoBuild preflight): the in-memory values against the files on disk; with fix the
    /// disk values are copied back.</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    public static class RenderPipelineAssetGuard
    {
        const string SessionKey = "G3.RenderPipelineAssetGuard.Snapshot";
        const string Tag = "[RenderPipelineAssetGuard] ";

        [Serializable] sealed class Entry { public string id; public string json; public bool dirty; }
        [Serializable] sealed class Snapshot { public List<Entry> entries = new List<Entry>(); }

        static RenderPipelineAssetGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) Take();
            else if (change == PlayModeStateChange.EnteredEditMode) Restore();
        }

        /// <summary>The pipeline assets of the graphics settings and every quality level, their renderer data and features.</summary>
        public static List<Object> Tracked()
        {
            var list = new List<Object>();
            void Add(Object o)
            {
                if (o != null && !list.Contains(o) && AssetDatabase.Contains(o)) list.Add(o);
            }
            void AddPipeline(RenderPipelineAsset rp)
            {
                if (rp == null) return;
                Add(rp);
                if (!(rp is UniversalRenderPipelineAsset urp)) return;
                var data = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(urp) as ScriptableRendererData[];
                if (data == null) return;
                foreach (var d in data)
                {
                    if (d == null) continue;
                    Add(d);
                    foreach (var f in d.rendererFeatures) Add(f);
                }
            }
            AddPipeline(GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < QualitySettings.names.Length; i++) AddPipeline(QualitySettings.GetRenderPipelineAssetAt(i));
            return list;
        }

        static void Take()
        {
            var snap = new Snapshot();
            foreach (var o in Tracked())
                snap.entries.Add(new Entry
                {
                    id = GlobalObjectId.GetGlobalObjectIdSlow(o).ToString(),
                    json = EditorJsonUtility.ToJson(o),
                    dirty = EditorUtility.IsDirty(o),
                });
            SessionState.SetString(SessionKey, JsonUtility.ToJson(snap));
        }

        /// <summary>Puts the snapshot of the last Play entry back (no-op without one). Returns the restored objects' names.</summary>
        public static string Restore()
        {
            string raw = SessionState.GetString(SessionKey, "");
            SessionState.EraseString(SessionKey);
            if (string.IsNullOrEmpty(raw)) return "";
            var snap = JsonUtility.FromJson<Snapshot>(raw);
            var restored = new List<string>();
            foreach (var e in snap.entries)
            {
                if (!GlobalObjectId.TryParse(e.id, out var gid)) continue;
                var o = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (o == null) continue;
                if (EditorJsonUtility.ToJson(o) != e.json)
                {
                    EditorJsonUtility.FromJsonOverwrite(e.json, o);
                    restored.Add(o.name);
                }
                if (!e.dirty && EditorUtility.IsDirty(o)) EditorUtility.ClearDirty(o);
            }
            if (restored.Count == 0) return "";
            string names = string.Join(", ", restored);
            Debug.Log(Tag + "render-pipeline values changed in Play restored: " + names);
            return names;
        }

        /// <summary>
        /// Compares the tracked objects' values (object references aside) with their files on disk. <paramref name="fix"/>
        /// copies the disk values back into memory. Returns one line per difference (empty = all equal); <paramref name="ok"/>
        /// false when a difference is left.
        /// </summary>
        public static string CheckAgainstDisk(bool fix, out bool ok)
        {
            ok = true;
            var sb = new StringBuilder();
            foreach (var path in Tracked().Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).Distinct())
            {
                Object[] disk;
                try { disk = InternalEditorUtility.LoadSerializedFileAndForget(path); }
                catch (Exception e) { sb.AppendLine($"{path}: not read ({e.Message})"); ok = false; continue; }
                try
                {
                    var mem = AssetDatabase.LoadAllAssetsAtPath(path);
                    foreach (var d in disk)
                    {
                        if (d == null || d is GameObject || d is Component) continue;
                        var m = Pair(d, disk, mem);
                        if (m == null) { sb.AppendLine($"{path}: {d.GetType().Name} '{d.name}' not loaded in memory"); continue; }
                        if (!Compare(d, m, fix, sb, path)) ok = false;
                    }
                }
                finally
                {
                    foreach (var d in disk) if (d != null) Object.DestroyImmediate(d);
                }
            }
            return sb.ToString();
        }

        // the n-th object of that type and name in the file
        static Object Pair(Object d, Object[] disk, Object[] mem)
        {
            int n = 0;
            foreach (var o in disk)
            {
                if (o == d) break;
                if (o != null && o.GetType() == d.GetType() && o.name == d.name) n++;
            }
            foreach (var o in mem)
                if (o != null && o.GetType() == d.GetType() && o.name == d.name && n-- == 0) return o;
            return null;
        }

        static readonly HashSet<string> Skip = new HashSet<string> { "m_Script", "m_ObjectHideFlags", "m_EditorHideFlags", "m_Name", "m_EditorClassIdentifier" };

        static bool Compare(Object disk, Object mem, bool fix, StringBuilder sb, string path)
        {
            var dso = new SerializedObject(disk);
            var mso = new SerializedObject(mem);
            bool wasDirty = EditorUtility.IsDirty(mem), changed = false, left = false;
            var it = dso.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = false;
                if (it.depth == 0 && Skip.Contains(it.name)) continue;
                var t = it.propertyType;
                if (t == SerializedPropertyType.ObjectReference || t == SerializedPropertyType.ExposedReference || t == SerializedPropertyType.ManagedReference) continue;
                if (t == SerializedPropertyType.Generic) { enter = true; continue; }   // structs / arrays: their fields
                var mp = mso.FindProperty(it.propertyPath);
                if (mp == null || mp.propertyType != t) continue;
                if (SerializedProperty.DataEquals(it, mp)) continue;
                string where = $"{path}: {mem.name}.{it.propertyPath} {Value(mp)} (disk {Value(it)})";
                if (fix && t != SerializedPropertyType.ArraySize)
                {
                    mso.CopyFromSerializedProperty(it);
                    changed = true;
                    sb.AppendLine(where + " -> restored");
                }
                else
                {
                    left = true;
                    sb.AppendLine(where);
                }
            }
            if (changed)
            {
                mso.ApplyModifiedPropertiesWithoutUndo();
                if (!wasDirty) EditorUtility.ClearDirty(mem);
            }
            return !left;
        }

        static string Value(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.LayerMask: case SerializedPropertyType.ArraySize: case SerializedPropertyType.Character:
                    return p.longValue.ToString();
                case SerializedPropertyType.Boolean: return p.boolValue.ToString();
                case SerializedPropertyType.Float: return p.doubleValue.ToString("0.####");
                case SerializedPropertyType.String: return "\"" + p.stringValue + "\"";
                case SerializedPropertyType.Enum: return p.intValue.ToString();
                case SerializedPropertyType.Vector2: return p.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return p.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return p.vector4Value.ToString();
                case SerializedPropertyType.Color: return p.colorValue.ToString();
                default: return p.propertyType.ToString();
            }
        }

        [MenuItem("G3/Remaster/World/Check Render Pipeline Assets Against Disk", priority = 400)]
        static void MenuCheck()
        {
            string r = CheckAgainstDisk(false, out bool ok);
            Debug.Log(Tag + (ok && r.Length == 0 ? "in-memory pipeline assets equal the files on disk" : "differences:\n" + r));
        }
    }
}
