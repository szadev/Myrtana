#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-104 ops and probes (Docs/dev-scenarios.md): the focus needs a free line of sight (<see cref="FocusLineOfSight"/>).
    /// Builds solid test geometry the ray must not pass: an upper floor over an NPC, a wall between the hero and it; an item
    /// dropped in front of the hero. The geometry goes at the scenario's end.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        readonly List<GameObject> _fix104Geometry = new List<GameObject>();
        string _fix104Item; // the item 'lositem' dropped

        /// <summary>
        /// 'losfloor npc=$a [height=3] [dist=2]': a solid 8 × 0.3 × 8 m floor whose top is <c>height</c> m over the NPC's feet,
        /// centred over it; the hero stands on it <c>dist</c> m (horizontally) from the NPC, facing it — upstairs in a house.
        /// </summary>
        IEnumerator LosFloorStep(ScenarioStep st)
        {
            var id = Actor(st.Get("npc"));
            if (!TryActorPosition(id, out var np, out _)) { Fail($"losfloor: no actor '{st.Get("npc")}'"); yield break; }
            float height = st.Float("height", 3f), dist = st.Float("dist", 2f);
            var away = Vector3.ProjectOnPlane(Hero.transform.position - np, Vector3.up);
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
            float top = np.y + height;
            Solid("[fix104 floor]", new Vector3(np.x, top - 0.15f, np.z), new Vector3(8f, 0.3f, 8f), Quaternion.identity);
            Physics.SyncTransforms();
            Hero.Teleport(new Vector3(np.x, top, np.z) + away * dist + Vector3.up * 0.15f);
            yield return new WaitForSeconds(0.3f);
            Face(np + Vector3.up * 1.35f, -30f);
            yield return new WaitForSeconds(0.4f);
            Log($"losfloor over {id}: floor top {F(top)}, hero {V(Hero.transform.position)}");
        }

        /// <summary>
        /// 'loswall npc=$a | item=1': a solid 4 × 4 × 0.3 m wall halfway between the hero and the NPC (item: the one 'lositem'
        /// dropped), across the line between them.
        /// </summary>
        void LosWall(ScenarioStep st)
        {
            string who = st.Get("npc");
            Vector3 np;
            object id = who;
            if (st.Bool("item", false))
            {
                if (_fix104Item == null || !_session.Items.World.TryGetDropped(_fix104Item, out var d)) { Fail("loswall: no item from lositem"); return; }
                np = new Vector3(d.Position[0], d.Position[1], d.Position[2]);
            }
            else
            {
                var actor = Actor(who);
                id = actor;
                if (!TryActorPosition(actor, out np, out _)) { Fail($"loswall: no actor '{who}'"); return; }
            }
            var hp = Hero.transform.position;
            var flat = Vector3.ProjectOnPlane(np - hp, Vector3.up);
            if (flat.sqrMagnitude < 0.04f) { Fail("loswall: the hero stands on the NPC"); return; }
            var mid = (hp + np) * 0.5f + Vector3.up * 2f;
            Solid("[fix104 wall]", mid, new Vector3(4f, 4f, 0.3f), Quaternion.LookRotation(flat.normalized, Vector3.up));
            Physics.SyncTransforms();
            Log($"loswall between hero and {id} at {V(mid)}");
        }

        /// <summary>'lositem [item=It_1H_Sword_01] [dist=1.2]': drops an item (no owner) in front of the hero; sets $item.</summary>
        void LosItem(ScenarioStep st)
        {
            var hero = Hero.transform;
            var flat = Vector3.ProjectOnPlane(hero.forward, Vector3.up).normalized;
            var at = hero.position + flat * st.Float("dist", 1.2f);
            if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)) at = hit.point;
            var dropped = _session.Items.World.Drop(default, st.Get("item", "It_1H_Sword_01"), 1, 0, new[] { at.x, at.y, at.z }, 0f);
            if (dropped == null) { Fail("lositem: drop refused"); return; }
            _fix104Item = dropped.Id;
            _vars["item"] = dropped.Id;
            Log($"lositem {dropped.Item} ({dropped.Id}) at {V(at)}");
        }

        /// <summary>
        /// 'losshelf [size=1.2,1.4,0.5]': a solid box around the item 'lositem' dropped (0.4 m over the box's bottom, facing the
        /// hero), as G3's single-box shelves hold the items on them (fix-114: the Kap Dun shed's teleport stone).
        /// </summary>
        void LosShelf(ScenarioStep st)
        {
            if (_fix104Item == null || !_session.Items.World.TryGetDropped(_fix104Item, out var d)) { Fail("losshelf: no item from lositem"); return; }
            var size = ScenarioStep.TryVector(st.Get("size", "1.2,1.4,0.5"), out var v) && v.Length == 3 ? new Vector3(v[0], v[1], v[2]) : new Vector3(1.2f, 1.4f, 0.5f);
            var at = new Vector3(d.Position[0], d.Position[1], d.Position[2]);
            var flat = Vector3.ProjectOnPlane(at - Hero.transform.position, Vector3.up);
            var rot = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : Quaternion.identity;
            var centre = at + Vector3.up * (size.y * 0.5f - 0.4f);
            Solid("[fix114 shelf]", centre, size, rot);
            Physics.SyncTransforms();
            Log($"losshelf around {d.Item} ({d.Id}) at {V(centre)} size {V(size)}");
        }

        void Solid(string name, Vector3 at, Vector3 size, Quaternion rotation)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetPositionAndRotation(at, rotation);
            go.transform.localScale = size;
            _fix104Geometry.Add(go);
        }

        /// <summary>'losclear': removes the test geometry (also done at the scenario's end).</summary>
        void ClearLosGeometry(bool log)
        {
            foreach (var go in _fix104Geometry) if (go != null) Destroy(go);
            if (log) Log($"losclear {_fix104Geometry.Count}");
            _fix104Geometry.Clear();
            Physics.SyncTransforms();
        }

        static bool IsFix104Probe(string kind) => kind == "ifocusitem" || kind == "ifocusany" || kind == "casttiming" || kind == "heroverlay" ||
                                                     kind == "missileflight" || kind == "missileturn";

        string Fix104Probe(string kind)
        {
            switch (kind)
            {
                // fix-114: when the last 'cast' let its spell go (held: during the motion with the button down; afterup; none)
                case "casttiming": return _lastCastTiming;
                // fix-159: the flight model of the last spell missile (Line: aimed once at release; Missile: homes, G3
                // only for a power cast at a focus), and the most its flight turned from the release direction, degrees
                case "missileflight": return Projectile.LastSpellFlight;
                case "missileturn": return Projectile.LastSpellTurnDegrees.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                // fix-114: an upper-body motion the hero's combat and torch views do not run (a cast pose left over after a
                // teleport); "-" when there is none
                case "heroverlay":
                {
                    var motions = Hero != null ? Hero.GetComponentInChildren<G3.Unity.Characters.G3MotionPlayer>() : null;
                    var key = motions != null ? motions.OverlayKey : null;
                    if (key == null) return "-";
                    if (Combat != null && Combat.Fighter != null && Combat.Fighter.Animator.InRangedOverlay) return "-";
                    var torch = HeroTorchView.Instance;
                    return torch != null && torch.OverlayRunning ? "-" : key;
                }
            }
            var pi = FindAnyObjectByType<PlayerInteraction>();
            if (pi == null) return "-";
            switch (kind)
            {
                // the world item in the normal focus ('item' when it is the one 'lositem' dropped)
                case "ifocusitem": return pi.FocusItem == null ? "-" : pi.FocusItem == _fix104Item ? "item" : pi.FocusItem;
                // anything in the normal focus: an NPC, an item or an object
                case "ifocusany": return (!pi.Focus.IsEmpty || pi.FocusItem != null || pi.FocusObject != null).ToString();
                default: return "-";
            }
        }
    }
}
#endif
