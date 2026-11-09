#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using UnityEngine;
using UnityEngine.AI;

namespace Myrtana.Unity.Dev
{
    /// <summary>One NPC of the threat scan with where it is and how its place was judged.</summary>
    internal sealed class ThreatEntry
    {
        public Threat Threat;
        public string Name;
        public Vector3 Position;
        public Transform View;
        public CombatThreats.Place Place;
        public bool Dead, Simulated;
        public string Line;
        public bool Stuck => View != null && CombatThreats.Stuck(Place);
    }

    /// <summary>
    /// fix-151: the Unity half of <see cref="CombatThreats"/>: feeds it the combat brains (distance to the hero, token,
    /// coordinator) and judges each threat's place with the game's own checks: the capsule test
    /// (<see cref="NpcPlacement.BlockerAt"/>), the cave judge (<see cref="CaveGround.Judge"/>) and a NavMesh path to the hero.
    /// </summary>
    internal static class ThreatScan
    {
        static readonly NavMeshPath _path = new NavMeshPath();
        /// <summary>Paths are only computed this close to the hero, m (farther: "far").</summary>
        const float PathReach = 150f;

        public static List<ThreatEntry> Scan(GameSession s, NpcViewManager views, Transform hero)
        {
            var result = new List<ThreatEntry>();
            if (s == null || hero == null) return result;
            var heroPos = hero.position;
            var pos = new Dictionary<ActorId, (Vector3 p, Transform v)>();
            (Vector3 p, Transform v) Where(ActorId id)
            {
                if (pos.TryGetValue(id, out var w)) return w;
                Transform v = null;
                Vector3 p;
                if (views != null && views.TryGetView(id, out v) && v != null) p = v.position;
                else if (s.Npcs.TryGet(id, out var agent))
                {
                    var pose = s.Npcs.PoseOf(agent);
                    p = new Vector3(pose.X, pose.Y, pose.Z);
                }
                else p = new Vector3(float.NaN, float.NaN, float.NaN);
                return pos[id] = (p, v);
            }
            float Dist(ActorId id)
            {
                var p = Where(id).p;
                return float.IsNaN(p.x) ? float.NaN : Vector3.Distance(p, heroPos);
            }
            var party = new HashSet<ActorId>(s.Party.Members.Members.Select(m => m.Id));
            var rows = new List<ThreatFighter>();
            foreach (var b in s.NpcCombat.Brains)
                rows.Add(new ThreatFighter(b.Self, b.State.Phase, b.State.Target, b.HoldsToken, s.NpcCombat.Coordinator.IsFighting(b.Self), Dist(b.Self)));
            var threats = CombatThreats.Collect(rows, party.Contains, Dist);

            bool heroOnMesh = NavMesh.SamplePosition(heroPos, out var heroHit, 2f, NavMesh.AllAreas);
            foreach (var t in threats)
            {
                var (p, v) = Where(t.Id);
                bool simulated = s.Npcs.TryGet(t.Id, out var agent);
                var e = new ThreatEntry { Threat = t, Position = p, View = v, Dead = s.Combat.Vitals.IsDead(t.Id), Simulated = simulated };
                e.Name = agent != null && !agent.Definition.DisplayName.IsEmpty ? s.Localizer.Text(agent.Definition.DisplayName) : t.Id.Name;
                e.Place = Judge(e, heroOnMesh, heroHit.position);
                var d = float.IsNaN(p.x) ? Vector3.zero : p - heroPos;
                e.Line = CombatThreats.Line(t, e.Name, d.x, d.z, p.x, p.y, p.z, e.Place) +
                         (e.Dead ? " DEAD" : "") + (!e.Simulated ? " NOSIM" : "") + (v == null ? " (no body shown)" : "");
                result.Add(e);
            }
            return result;
        }

        static CombatThreats.Place Judge(ThreatEntry e, bool heroOnMesh, Vector3 heroNav)
        {
            var place = new CombatThreats.Place { Cave = "Open", CaveOk = true, Path = "?", OffMesh = float.NaN };
            if (e.View == null) { place.Cave = "noview"; place.Path = "noview"; return place; }
            var feet = e.View.position;
            place.Inside = NpcPlacement.BlockerAt(feet, e.View);
            var st = CaveGround.Judge(feet, out _);
            place.Cave = st.ToString();
            place.CaveOk = G3.Player.CaveFloorRules.MayStand(st);
            if (!NavMesh.SamplePosition(feet, out var hit, 1f, NavMesh.AllAreas))
            {
                place.Path = "offmesh";
                if (NavMesh.SamplePosition(feet, out var far, 16f, NavMesh.AllAreas)) place.OffMesh = Vector3.Distance(feet, far.position);
                return place;
            }
            place.OffMesh = Vector3.Distance(feet, hit.position);
            if (!heroOnMesh) { place.Path = "hero-offmesh"; return place; }
            if (Vector3.Distance(feet, heroNav) > PathReach) { place.Path = "far"; return place; }
            if (!NavMesh.CalculatePath(hit.position, heroNav, NavMesh.AllAreas, _path)) { place.Path = "none"; return place; }
            place.Path = _path.status == NavMeshPathStatus.PathComplete ? "complete" : _path.status == NavMeshPathStatus.PathPartial ? "partial" : "none";
            return place;
        }
    }

    /// <summary>
    /// fix-151: on-screen markers of the threats (console <c>enemies</c> / <c>enemies off</c>): a box and a label with the
    /// distance at each one's chest, drawn by IMGUI over everything (so through walls and rock), clamped to the screen edge
    /// with an arrow when it is off screen or behind the camera. The list is refreshed every second; changes go to the log
    /// ("[threats]"). Dev builds only, nothing when off.
    /// </summary>
    internal sealed class ThreatMarkers : MonoBehaviour
    {
        static ThreatMarkers _instance;
        const float RefreshSeconds = 1f;
        float _next;
        List<ThreatEntry> _entries = new List<ThreatEntry>();
        HashSet<ActorId> _shown = new HashSet<ActorId>();
        GameBootstrap _boot;
        NpcViewManager _views;
        G3Player _hero;
        GUIStyle _label;

        public static bool On => _instance != null;

        public static void Show(bool on)
        {
            if (on && _instance == null)
            {
                var go = new GameObject("[ThreatMarkers]") { hideFlags = HideFlags.DontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<ThreatMarkers>();
            }
            else if (!on && _instance != null)
            {
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + RefreshSeconds;
            if (_boot == null) _boot = FindAnyObjectByType<GameBootstrap>();
            if (_views == null) _views = FindAnyObjectByType<NpcViewManager>();
            if (_hero == null) _hero = FindAnyObjectByType<G3Player>();
            var s = _boot != null ? _boot.Session : null;
            if (s == null || _hero == null) { _entries.Clear(); return; }
            _entries = ThreatScan.Scan(s, _views, _hero.transform);
            var now = new HashSet<ActorId>(_entries.Select(e => e.Threat.Id));
            if (!now.SetEquals(_shown))
            {
                var added = _entries.Where(e => !_shown.Contains(e.Threat.Id)).ToList();
                var gone = _shown.Where(id => !now.Contains(id)).ToList();
                Debug.Log($"[threats] {now.Count} now (inCombat {s.NpcCombat.IsPlayerInCombat})" +
                          (gone.Count > 0 ? "; gone " + string.Join(", ", gone) : "") +
                          string.Concat(added.Select(e => "\n[threats] + " + e.Line)));
                _shown = now;
            }
        }

        void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null || _hero == null) return;
            if (_label == null)
                _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };
            var old = GUI.color;
            float w = Screen.width, h = Screen.height;
            int battle = 0;
            foreach (var e in _entries)
            {
                if ((e.Threat.Reasons & ThreatReason.Battle) != 0) battle++;
                Vector3 p = e.View != null ? e.View.position + Vector3.up * 1.1f : e.Position + Vector3.up * 1.1f;
                if (float.IsNaN(p.x)) continue;
                var sp = cam.WorldToScreenPoint(p);
                bool behind = sp.z < 0f;
                float x = behind ? w - sp.x : sp.x, y = behind ? sp.y : sp.y;
                y = h - y;
                bool off = behind || x < 0 || x > w || y < 0 || y > h;
                if (behind) y = h - 40f; // behind the camera: along the bottom edge
                x = Mathf.Clamp(x, 30f, w - 30f);
                y = Mathf.Clamp(y, 30f, h - 50f);
                var color = e.Stuck ? new Color(1f, 0.2f, 1f) : (e.Threat.Reasons & ThreatReason.Battle) != 0 ? new Color(1f, 0.15f, 0.1f)
                    : (e.Threat.Reasons & ThreatReason.Music) != 0 ? new Color(1f, 0.6f, 0.1f) : new Color(1f, 1f, 0.2f);
                float dist = Vector3.Distance(_hero.transform.position, e.View != null ? e.View.position : e.Position);
                float size = Mathf.Clamp(600f / Mathf.Max(dist, 1f), 10f, 40f);
                GUI.color = Color.black;
                Frame(new Rect(x - size / 2 - 1, y - size / 2 - 1, size + 2, size + 2), 4f);
                GUI.color = color;
                Frame(new Rect(x - size / 2, y - size / 2, size, size), 2f);
                string flags = CombatThreats.Flags(e.Place);
                string text = $"{(off ? (behind ? "v " : "> ") : "")}{e.Name} {dist:0}m{(flags.Length > 0 ? " [" + flags + "]" : "")}{(e.Dead ? " DEAD" : "")}";
                var r = new Rect(x - 200f, y + size / 2 + 2f, 400f, 20f);
                GUI.color = Color.black;
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, _label);
                GUI.color = color;
                GUI.Label(r, text, _label);
            }
            GUI.color = Color.black;
            string head = $"threats: {_entries.Count} (save block {battle}) — 'enemies off' hides, 'enemies fix' / 'enemies kill'";
            GUI.Label(new Rect(9, h - 23, w, 20), head);
            GUI.color = Color.white;
            GUI.Label(new Rect(8, h - 24, w, 20), head);
            GUI.color = old;
        }

        static void Frame(Rect r, float t)
        {
            var tex = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), tex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), tex);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), tex);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), tex);
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }

    public sealed partial class ScenarioRunner
    {
        /// <summary>fix-151: <c>enemies [on|off|list|fix|kill]</c> (alias threats; Docs/dev-scenarios.md).</summary>
        IEnumerator EnemiesStep(ScenarioStep st)
        {
            string arg = st.Get("mode");
            if (!CombatThreats.TryParseMode(arg, out var mode)) { Fail($"enemies: '{arg}' is not on|off|list|fix|kill"); yield break; }
            if (mode == CombatThreats.Mode.Off)
            {
                ThreatMarkers.Show(false);
                Log("[threats] markers off");
                yield break;
            }
            if (Hero == null) { Fail("enemies: no hero"); yield break; }
            var list = ThreatScan.Scan(_session, Views, Hero.transform);
            Log($"[threats] {list.Count} keeping the hero in combat (inCombat {_session.NpcCombat.IsPlayerInCombat}; " +
                $"battle = save block, music = fight music, hero/party = its target, companion = a companion fights it)");
            foreach (var e in list) Log("[threats] " + e.Line);
            if (mode == CombatThreats.Mode.On)
            {
                ThreatMarkers.Show(true);
                Log("[threats] markers on ('enemies off' hides them)");
            }
            else if (mode == CombatThreats.Mode.Fix)
            {
                int moved = 0;
                foreach (var e in list)
                {
                    if (e.Dead || !e.Stuck) continue;
                    if (Views == null || !Views.TryGetViewComponent(e.Threat.Id, out var nv)) { Log($"[threats] fix {e.Threat.Id}: no body shown"); continue; }
                    string did = nv.DevUnstick();
                    if (did != null && did.StartsWith("moved")) moved++;
                    Log($"[threats] fix {e.Threat.Id}: {did ?? "nothing to do"}");
                }
                foreach (var e in list.Where(e => !e.Dead && e.View == null)) Log($"[threats] fix {e.Threat.Id}: no body shown (far, simulated only)");
                Log($"[threats] fix: {moved} moved of {list.Count(e => e.Stuck && !e.Dead)} stuck");
                yield return null;
            }
            else if (mode == CombatThreats.Mode.Kill)
            {
                int n = 0;
                foreach (var e in list)
                {
                    if (e.Dead) continue;
                    _session.Combat.Vitals.Kill(e.Threat.Id, false);
                    n++;
                }
                yield return null;
                Log($"[threats] kill: {n} killed; inCombat now {_session.NpcCombat.IsPlayerInCombat}");
            }
        }
    }
}
#endif
