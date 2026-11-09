#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using Myrtana.Core;
using Myrtana.Rules;
using System.Linq;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-93 (Docs/dev-scenarios.md): NPC walks go round the standing hero (G3 NPCEvadeSwitch, <see cref="G3.Player.NpcEvadeRules"/>),
    /// NPC arrows are aimed once at release and can be dodged (<see cref="ProjectileFlight"/>), item-granted skills and
    /// the [owner] kill XP for a victim the hero hurt.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        static readonly System.Globalization.CultureInfo Inv93 = System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>
        /// 'npcpass npc [dist=5] [secs=15] [expect=round|wait]': the NPC is put dist m in front of the standing hero, facing
        /// him, and walks (its walker, routine paused) to the point dist m behind him (round) or to the hero's own spot
        /// (wait: the goal under him). round passes when it arrives, went round (a detour) and never came closer than
        /// contact (its footprint + the hero's radius) − 0.05 m; wait passes when it did not arrive, stopped within 0.6 m of
        /// contact and never came closer. Logs closest distance, detours, holds and the evade side.
        /// </summary>
        System.Collections.IEnumerator NpcPassStep(ScenarioStep st)
        {
            var who = Actor(st.Get("npc"));
            if (who.IsPlayer || !TryActorPosition(who, out _, out var viewT) || viewT == null || !viewT.TryGetComponent<NpcView>(out var view))
            {
                Fail($"npcpass: no NPC view '{st.Get("npc")}'");
                yield break;
            }
            float dist = st.Float("dist", 5f), secs = st.Float("secs", 15f);
            bool wait = string.Equals(st.Get("expect"), "wait", System.StringComparison.OrdinalIgnoreCase);
            var hero = Hero.transform;
            var fwd = Vector3.ProjectOnPlane(hero.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.5f) fwd = Vector3.forward;
            // fix-97 (r39 Ardea: 'arrived False, closest 4.67 m, end 11.70 m, detours 0'): the line through the hero must be
            // open walkable ground, else the NavMesh path goes round a hut and never meets him. The hero's facing first, then
            // the other axes in 45 deg steps; the first whose start-goal line has no NavMesh edge.
            float axisYaw = 0f;
            bool clear = false;
            for (int k = 0; k < 8 && !clear; k++)
            {
                float yaw = (k % 2 == 1 ? 1 : -1) * ((k + 1) / 2) * 45f;
                var a = Quaternion.Euler(0, yaw, 0) * fwd;
                var s0 = hero.position + a * dist;
                var g0 = wait ? hero.position : hero.position - a * dist;
                if (!UnityEngine.AI.NavMesh.SamplePosition(s0, out var sh, 1.5f, UnityEngine.AI.NavMesh.AllAreas) ||
                    !UnityEngine.AI.NavMesh.SamplePosition(g0, out var gh, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                if (UnityEngine.AI.NavMesh.Raycast(sh.position, gh.position, out _, UnityEngine.AI.NavMesh.AllAreas)) continue;
                axisYaw = yaw;
                clear = true;
            }
            fwd = Quaternion.Euler(0, axisYaw, 0) * fwd;
            var start = hero.position + fwd * dist;
            start.y = NpcWalker.FloorHeight(start, hero.position.y);
            var goal = wait ? hero.position : hero.position - fwd * dist;
            var fighter = FighterOf(who);
            float own = Hero.TryGetComponent<CharacterController>(out var cc) ? cc.radius : 0.35f;
            float contact = own + (fighter != null && fighter.Body != null ? fighter.Body.Footprint : 0.35f);
            int detours0 = view.Walker.Detours, holds0 = view.Walker.Holds;
            int side = 0;
            float speed = st.Float("speed", 1.6f), min = float.MaxValue, t0 = Time.time;
            // fix-97: placed and walking in the same frame (r39: the transform was set, then a frame without the walk let
            // the routine update put the NPC back on its simulation pose); the pose is reported to the simulation
            view.DevWalk(goal, speed, start, Quaternion.LookRotation(-fwd));
            string plan = view.Walker.Plan;
            // fix-105 (r41 fix93: a wolf spawned and calmed just before stood 5.00 m off the whole walk, 'arrived' / 'stuck' at
            // once): its first frames put the view back on its old simulation pose; a player never sees that, so it is placed
            // again until it stays at the start
            float placed = Time.time;
            for (int frame = 0, again = 0; frame < 30 && again < 2; frame++)
            {
                yield return null;
                var off = view.transform.position - start;
                off.y = 0f;
                if (view.DevWalking && off.magnitude <= speed * (Time.time - placed) + 0.5f) continue;
                Log($"npcpass {who}: put back {off.magnitude.ToString("0.00", Inv93)} m from the start ({(view.DevWalking ? "walking" : view.DevWalkResult.ToString())}): placed again");
                view.DevWalk(goal, speed, start, Quaternion.LookRotation(-fwd));
                plan = view.Walker.Plan;
                placed = Time.time;
                again++;
            }
            while (Time.time - t0 < secs && view.DevWalking)
            {
                // fix-97: an NPC with an Important info talks to the hero within 5 m (G3 AssessTalk): answered as a player
                // would; the walk goes on, the talk is not walking time
                if (_session.Dialog.IsActive)
                {
                    float talk = 0f;
                    yield return AnswerUnaskedTalk("npcpass " + who, x => talk = x);
                    t0 += talk;
                    continue;
                }
                var d = view.transform.position - hero.position;
                d.y = 0f;
                min = Mathf.Min(min, d.magnitude);
                if (view.Walker.Evading) side = view.Walker.EvadeSide;
                yield return null;
            }
            string walkEnd = view.DevWalking ? "timeout" : view.DevWalkResult.ToString();
            bool arrived = !view.DevWalking && view.DevWalkResult == NpcWalker.Result.Arrived;
            view.DevWalkStop();
            var end = view.transform.position - hero.position;
            end.y = 0f;
            int detours = view.Walker.Detours - detours0, holds = view.Walker.Holds - holds0;
            string summary = $"npcpass {who}: arrived {arrived}, closest {min.ToString("0.00", Inv93)} m, end {end.magnitude.ToString("0.00", Inv93)} m, " +
                             $"contact {contact.ToString("0.00", Inv93)} m, detours {detours}, holds {holds}, side {(side > 0 ? "left" : side < 0 ? "right" : "-")}, " +
                             $"walk {walkEnd}, axis {axisYaw.ToString("0", Inv93)} deg{(clear ? "" : " (no open line on the NavMesh)")}, plan {plan}";
            bool ok = wait ? !arrived && min >= contact - 0.05f && end.magnitude <= contact + 0.6f
                           : arrived && detours > 0 && min >= contact - 0.05f;
            if (ok) Log(summary + (wait ? " (waited at the hero as expected)" : " (went round as expected)"));
            else Fail(summary + (wait ? " (expected: stops at the hero, never inside)" : " (expected: goes round him, never inside)"));
        }

        /// <summary>
        /// 'arrowdodge npc [shots=3] [secs=60] [dodge=1] [step=0.5]': the NPC (an archer engaged on the hero) shoots; at each
        /// release the hero runs sideways for step s (dodge=1, alternating sides) or stands (dodge=0). dodge=1 passes when
        /// no more than one of the shots stuck in him; dodge=0 passes when at least shots−1 did. Logs per shot.
        /// </summary>
        System.Collections.IEnumerator ArrowDodgeStep(ScenarioStep st)
        {
            var who = Actor(st.Get("npc"));
            var shooter = FighterOf(who);
            if (shooter == null) { Fail($"arrowdodge: no fighter '{st.Get("npc")}'"); yield break; }
            int shots = st.Int("shots", 3);
            float secs = st.Float("secs", 60f), dodgeFor = st.Float("step", 0.5f);
            bool dodge = st.Bool("dodge", true);
            int released = 0, hits0 = Projectile.BodyHits.TryGetValue(ActorId.Player, out int h) ? h : 0;
            bool fresh = false;
            void OnLaunch(Projectile p)
            {
                if (p.Info != null && p.Info.Shooter == shooter && !p.Info.IsSpell) fresh = true;
            }
            Projectile.Launched += OnLaunch;
            float t0 = Time.time;
            try
            {
                while (released < shots && Time.time - t0 < secs)
                {
                    if (!fresh) { yield return null; continue; }
                    fresh = false;
                    released++;
                    int before = Projectile.BodyHits.TryGetValue(ActorId.Player, out int b0) ? b0 : 0;
                    if (dodge)
                    {
                        Hero.scriptedMove = new Vector2(released % 2 == 0 ? -1 : 1, 0);
                        float t1 = Time.time;
                        while (Time.time - t1 < dodgeFor) yield return null;
                        Hero.scriptedMove = Vector2.zero;
                    }
                    float t2 = Time.time;
                    while (Time.time - t2 < 1.5f) yield return null; // the flight (≤ 30 m at 27.8 m/s)
                    int after = Projectile.BodyHits.TryGetValue(ActorId.Player, out int b1) ? b1 : 0;
                    Log($"arrowdodge shot {released}: {(after > before ? "hit the hero" : "missed")}{(dodge ? " (stepped aside)" : " (stood)")}");
                }
            }
            finally
            {
                Projectile.Launched -= OnLaunch;
                Hero.scriptedMove = Vector2.zero;
            }
            int hits = (Projectile.BodyHits.TryGetValue(ActorId.Player, out int h1) ? h1 : 0) - hits0;
            string summary = $"arrowdodge {who}: {released} shots, {hits} hit the hero";
            if (released < shots) { Fail(summary + $" (expected {shots} shots in {secs:0} s)"); yield break; }
            bool ok = dodge ? hits <= 1 : hits >= shots - 1;
            if (ok) Log(summary + (dodge ? " (dodged as expected)" : " (a standing target is hit)"));
            else Fail(summary + (dodge ? " (expected: a step aside after release dodges)" : " (expected: hits on a standing target)"));
        }

        /// <summary>'strike attacker victim [dmg=]': one melee hit of the attacker's weapon resolved now (BuildHit → Strike).</summary>
        void StrikeStep(ScenarioStep st)
        {
            var a = Actor(st.Get("attacker"));
            var v = Actor(st.Get("victim"));
            var fa = a.IsPlayer ? Combat.Fighter : FighterOf(a);
            var fv = v.IsPlayer ? Combat.Fighter : FighterOf(v);
            var presenter = FindAnyObjectByType<CombatPresenter>();
            if (fa == null || fv == null || presenter == null) { Fail($"strike: no fighters {a} → {v}"); return; }
            var hit = presenter.BuildHit(fa, fv);
            if (st.Has("dmg")) hit.DamageAmount = st.Int("dmg", hit.DamageAmount);
            bool party = a.IsPlayer || (_session.Party?.Members?.DefeatsCountForPlayer(a) ?? false);
            var outcome = _session.Combat.Strike(hit, byPlayerParty: party);
            Log($"strike {a} → {v}: {outcome}, {v} hurt by the hero {_session.Combat.Vitals.WasHurtByPlayer(v)}");
        }

        /// <summary>
        /// fix-97 'unequip item': takes the worn item off every slot it is worn in (HeroEquipment.UnequipItem; it stays
        /// carried). r39 fix93_item_skill: the CP start kit already wears the Orc Slayer, so the scenario takes it off first.
        /// </summary>
        void UnequipStep(ScenarioStep st)
        {
            string item = ResolveItem(st.Get("item"));
            if (item == null) return;
            var eq = _session.Items.Equipment;
            var slots = ((WornSlot[])System.Enum.GetValues(typeof(WornSlot))).Where(sl => eq.In(sl) == item).ToList();
            if (slots.Count == 0) { Log($"unequip {item}: not worn"); return; }
            eq.UnequipItem(item);
            Log($"unequip {item} ({string.Join(",", slots)})");
        }

        /// <summary>fix-97: the hero's XP at the last xpmark (else at the run start), for the condition xpgain.</summary>
        long _xpMark;

        static bool IsFix93Probe(string kind) => kind == "npcdetours" || kind == "npcholds" || kind == "skillactive" || kind == "hurtbyhero" || kind == "xpgain";

        string Fix93Probe(string kind, ActorId who, string arg)
        {
            switch (kind)
            {
                case "npcdetours": return NpcWalker.AllDetours.ToString(Inv93);
                case "npcholds": return NpcWalker.AllHolds.ToString(Inv93);
                // skillactive:Perk_X → learned | item | no (G3 IsSkillActive / HasLearnedSkill)
                case "skillactive":
                    return _session.Hero.HasLearned(arg) ? "learned" : _session.Hero.Knows(arg) ? "item" : "no";
                case "hurtbyhero": return _session.Combat.Vitals.WasHurtByPlayer(who) ? "1" : "0";
                // xpgain → the hero's XP since the last xpmark / the run start (fix-97: a fresh hero has the CP start XP)
                case "xpgain": return (_session.Hero.Xp - _xpMark).ToString(Inv93);
                default: return "-";
            }
        }
    }
}
#endif
