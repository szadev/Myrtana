#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System;
using System.Collections;
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
    /// <summary>
    /// fix-54: the fight op with a bow, a crossbow or a spell (loadouts bow/crossbow/spells, <c>fight npc style=</c>), and
    /// the ground snap of spawned NPCs.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        const float ShootMax = 20f, ShootMin = 6f;

        /// <summary>
        /// <c>fight npc style=bow|crossbow|spell</c>: the hero shoots (bow drawn fully, 2.05 s, then released; fix-56) or casts the loadout's spell
        /// (default It_Spell_Fireball) at the NPC from 6-20 m until it is Down/Dead or secs run out; farther than 20 m he walks
        /// closer. Five shots in a row that take no hit point move him 8 m from it on his side (line of sight; a dev
        /// shortcut, at most 3 times). Dev refills, logged once: ammunition below 5, mana for the next cast. The aim follows
        /// the camera: every frame the look that puts the screen centre on the target's body centre (<see cref="AimAt"/>; PlayerCombat
        /// shoots at the point under the screen centre). fix-57: the aim tracks the moving target through the whole draw
        /// (it stayed on where the target stood when the draw began) and leads it by its speed x the flight time; hits
        /// count the kill shot (HitResolved; r31: "hits=0 lost=5" in every bow fight); a foe within
        /// <see cref="ScenarioPacing.MeleeSwitchDistance"/> m is fought with the melee weapon, drawn with its quick-slot key
        /// (a bow has no melee in G3); a spell is readied with its quick-slot key (r31: "spell It_Spell_Fireball not readied").
        /// </summary>
        IEnumerator DistanceFightStep(ScenarioStep st, AttackStyle style, bool mustKill)
        {
            var s = _session;
            var id = Actor(st.Get("npc"));
            var V0 = s.Combat.Vitals;
            bool spell = style == AttackStyle.Spell;
            string spellItem = _styleSpell ?? "It_Spell_Fireball";
            int failuresBefore = _failures.Count;
            _fightDamage = 0;
            _fightUnreachable = false;
            string melee = s.Items.Equipment.In(WornSlot.MeleeWeapon);
            if (!spell && melee != null && TryActorPosition(id, out var p0, out _) &&
                ScenarioPacing.SwitchToMelee(style, Flat(p0 - Hero.transform.position), true))
            {
                yield return MeleeInsteadOfRanged(st, id, style, melee, Flat(p0 - Hero.transform.position), mustKill);
                yield break;
            }
            if (spell)
            {
                yield return ReadySpellByQuickSlot(spellItem, "fight " + id);
                if (_failures.Count > failuresBefore) yield break;
            }
            else if (!Combat.Fighter.RangedDrawn || !Armed(Combat.Fighter.Animator))
            {
                yield return DrawStep("ranged");
                if (!Combat.Fighter.RangedDrawn) { if (_failures.Count == failuresBefore) Fail($"fight {id}: no bow or crossbow drawn"); yield break; }
            }
            if (_failures.Count > failuresBefore) yield break;
            int hp = st.Int("hp", 5);
            if (V0.HitPoints(id) > hp) V0.AddHitPoints(id, hp - V0.HitPoints(id));
            int hpStart = V0.HitPoints(id);
            ProvokeProtectedBystander(id);
            float t0 = Time.time, limit = st.Float("secs", 30f), staggered = 0f, h = 0f;
            int shots = 0, drops = 0, hops = 0, shotsAtHit = 0, refills = 0, lastHp = hpStart, resolved = 0, blocked = 0;
            bool ammoRefilled = false, manaRefilled = false, switchMelee = false;
            string ammo = spell ? null : style == AttackStyle.Crossbow ? "Bolt" : "Arrow";
            // fix-57: every resolved hit of the hero on the target counts, the kill shot too (the loop below ends before it
            // would see that one's hit points)
            var sub = s.Events.Subscribe<HitResolved>(e =>
            {
                if (!e.Attacker.IsPlayer || e.Victim != id) return;
                if (e.Outcome.Result == HitResult.Hit) { resolved++; shotsAtHit = shots; }
                else if (e.Outcome.Result == HitResult.Blocked) blocked++;
            });
            var track = new TargetTrack();
            try
            {
                while (V0.StateOf(id) == VitalState.Alive && Time.time - t0 < ScenarioPacing.FightLimit(limit, staggered))
                {
                    if (!TryActorPosition(id, out var p, out var view)) { Fail("fight: lost " + id); break; }
                    if (ScenarioPacing.Reclamp(V0.HitPoints(id), hpStart)) { refills++; V0.AddHitPoints(id, hpStart - V0.HitPoints(id)); }
                    int now = V0.HitPoints(id);
                    if (now < lastHp) { drops++; shotsAtHit = shots; }
                    lastHp = now;
                    var a = Combat.Fighter.Animator;
                    if (a.InReaction || a.IsDown || Combat.Fighter.SittingKnockedDown || Combat.Fighter.Frozen) { staggered += Time.deltaTime; yield return null; continue; }
                    var f = view != null ? view.GetComponent<CombatFighter>() : null;
                    if (f != null) { Combat.PinnedFocus = f; Combat.SetFocus(f); }
                    var hero = Hero.transform.position;
                    var flat = p - hero;
                    flat.y = 0f;
                    h = flat.magnitude;
                    // fix-57: at the hero's feet a player takes the melee weapon (quick-slot key); a bow has no melee in G3
                    if (!spell && ScenarioPacing.SwitchToMelee(style, h, melee != null)) { switchMelee = true; break; }
                    if (h > ShootMax)
                    {
                        Face(p, 5f);
                        Hero.scriptedMove = new Vector2(0, 1);
                        yield return null;
                        continue;
                    }
                    Hero.scriptedMove = Vector2.zero;
                    if (shots - shotsAtHit >= 5 && hops < 3)
                    {
                        hops++;
                        shotsAtHit = shots;
                        var back = h > 0.01f ? -flat / h : -Hero.transform.forward;
                        if (FindGround(p + back * 8f, true, out var ground, out var why, view))
                        {
                            Hero.Teleport(ground.point + Vector3.up * 0.15f);
                            Log($"fight {id}: 5 {(spell ? "casts" : "shots")} took no hit point (dist {F(h)}), the hero is put 8 m from it ({hops})");
                            yield return new WaitForSeconds(0.3f);
                        }
                        else Log($"fight {id}: 5 {(spell ? "casts" : "shots")} took no hit point, no ground 8 m from it ({why})");
                        continue;
                    }
                    if (spell)
                    {
                        if (s.Hero.Stats.Value("MP") < 50)
                        {
                            s.Hero.Stats.Apply("MP", ModifierOp.SetValToMax, 0);
                            if (!manaRefilled) Log($"fight {id}: mana refilled (dev)");
                            manaRefilled = true;
                        }
                        float castSpeed = SpellSpeed(spellItem);
                        yield return AimTrack(id, track, 0.2f, castSpeed, false);
                        if (!Combat.BeginCast()) { yield return new WaitForSeconds(0.3f); continue; }
                        yield return AimTrack(id, track, 0.5f, castSpeed, false);
                        Combat.ReleaseCast();
                        shots++;
                        yield return AimTrack(id, track, 0.9f, castSpeed, false);
                        continue;
                    }
                    if (s.Items.Inventories.Of(ActorId.Player).Count(ammo) < 5)
                    {
                        s.Items.Inventories.Give(ActorId.Player, ammo, 50);
                        if (!ammoRefilled) Log($"fight {id}: 50 {ammo} given (dev refill)");
                        ammoRefilled = true;
                    }
                    if (a.Busy && !a.Nocked) { yield return AimTrack(id, track, 0f, 0f, false); continue; } // drawing or reloading
                    // fix-56: full draw (55.6 m/s; a crossbow always shoots at that), the crosshair held over the foe by the drop;
                    // fix-57: on the moving target all the time, led by its speed
                    float speed = RangedRules.ShotVelocity(true, 1f);
                    yield return AimTrack(id, track, 0.1f, speed, true);
                    Combat.PrimaryDown();
                    float aimStart = Time.time;
                    while (!a.AimHeld && Time.time - aimStart < 3f) yield return AimTrack(id, track, 0f, speed, true);
                    yield return AimTrack(id, track, style == AttackStyle.Crossbow ? 0.4f : RangedRules.BowFullDrawSeconds + 0.1f, speed, true);
                    Combat.PrimaryUp();
                    shots++;
                    yield return AimTrack(id, track, 0.6f, speed, true);
                }
            }
            finally
            {
                sub.Dispose();
                if (Combat.PrimaryHeld) Combat.PrimaryUp();
                Combat.PinnedFocus = null;
                Hero.scriptedMove = Vector2.zero;
            }
            _fightDamage = Mathf.Max(0, hpStart - V0.HitPoints(id));
            int hits = ScenarioPacing.RangedHits(drops, resolved, _fightDamage);
            string how = (staggered > 1f ? FormattableString.Invariant($", hero staggered/frozen {staggered:0.0} s") : "") + (refills > 0 ? $", hp refilled {refills}x" : "") +
                         (hops > 0 ? $", moved {hops}x" : "") + (blocked > 0 ? $", blocked {blocked}" : "");
            Log($"fight {id} ({style}{(spell ? " " + spellItem : "")}): {V0.StateOf(id)} hp={V0.HitPoints(id)} {(spell ? "casts" : "shots")}={shots} hits={hits} " +
                $"lost={_fightDamage} dist={F(h)} {(Time.time - t0).ToString("0.00", Inv)} s{how}");
            if (switchMelee && V0.StateOf(id) == VitalState.Alive)
            {
                yield return MeleeInsteadOfRanged(st, id, style, melee, h, mustKill);
                yield break;
            }
            if (mustKill) CreditClock(Mathf.Max(0f, Time.time - t0 - limit));
            if (V0.StateOf(id) == VitalState.Alive && mustKill) Fail(FormattableString.Invariant($"fight {id} ({style}): still alive after {Time.time - t0:0} s, {shots} {(spell ? "casts" : "shots")}, {hits} hits{how}"));
            yield return SheatheAfterFight(st, id, mustKill);
        }

        /// <summary>
        /// fix-57: the foe is within <see cref="ScenarioPacing.MeleeSwitchDistance"/> m of a bow or crossbow fighter: the
        /// melee weapon's quick-slot key (G3 OnPlayerUseQuickSlot: the bow is put away, the weapon drawn), then the melee fight.
        /// </summary>
        IEnumerator MeleeInsteadOfRanged(ScenarioStep st, ActorId id, AttackStyle style, string melee, float dist, bool mustKill)
        {
            Log($"fight {id}: {F(dist)} m from the hero with the {style} style, switches to the melee weapon {melee} (quick-slot key)");
            float t0 = Time.time;
            while (Combat.Fighter.Animator.Busy && Time.time - t0 < 3f) yield return null;
            bool meleeInHand = Armed(Combat.Fighter.Animator) && !Combat.Fighter.RangedDrawn && !Combat.Fighter.SpellDrawn;
            if (!meleeInHand && !Combat.TryReady(melee)) Log($"fight {id}: quick slot {melee} refused ({Combat.DrawRefusal() ?? "-"}), the fight draws it");
            t0 = Time.time;
            while (!(Armed(Combat.Fighter.Animator) && !Combat.Fighter.RangedDrawn && !Combat.Fighter.SpellDrawn) && Time.time - t0 < 4f) yield return null;
            yield return FightStep(st, mustKill, AttackStyle.Melee);
        }

        /// <summary>
        /// fix-57: readies <paramref name="item"/> as a player does: its quick-slot key (QuickSlotInput.Use, G3
        /// OnPlayerUseQuickSlot) when it is on the bar, else the same readying the spell page's Enter does; waits until it
        /// is in the spell hand (a drawn weapon is put away first, about 1 s; r31 checked after 1 s and failed). Fails with
        /// the reason (not learned, not carried, the animator state).
        /// </summary>
        IEnumerator ReadySpellByQuickSlot(string item, string tag)
        {
            var s = _session;
            if (Combat.Fighter.SpellDrawn && Combat.ReadiedSpell == item) yield break;
            string spellId = s.HeroSpells.SpellOf(item);
            if (!s.Items.Inventories.Of(ActorId.Player).Has(item) || !s.HeroSpells.CanReady(item))
            {
                Fail($"{tag}: spell {item} not readied: " +
                     (spellId != null && !s.HeroSpells.IsLearned(spellId) ? "not learned (loadout spells / teach)" : "not carried"));
                yield break;
            }
            float t0 = Time.time;
            while (Combat.Fighter.Animator.Busy && Time.time - t0 < 3f) yield return null;
            int slot = s.Items.QuickSlots.SlotOf(item);
            var keys = _boot != null ? _boot.GetComponent<QuickSlotInput>() : null;
            string how;
            if (slot >= 0 && keys != null && !Combat.Fighter.SpellDrawn)
            {
                how = $"quick-slot key {QuickSlots.DigitOfSlot(slot)}";
                keys.Use(slot);
            }
            else
            {
                // another spell in the hand (its key would put it away) or not on the bar: the spell page's Enter
                how = slot < 0 ? "not on the quick bar, readied as the spell page does" : "another spell in the hand, readied as the spell page does";
                Combat.ReadySpell(item);
            }
            t0 = Time.time;
            while (!(Combat.Fighter.SpellDrawn && Combat.ReadiedSpell == item && !Combat.Fighter.Animator.Busy) && Time.time - t0 < 5f) yield return null;
            if (Combat.Fighter.SpellDrawn && Combat.ReadiedSpell == item) Log($"{tag}: {item} in the spell hand ({how}, {F(Time.time - t0)} s)");
            else Fail($"{tag}: spell {item} not readied after {F(Time.time - t0)} s ({how}; in the hand {Combat.ReadiedSpell ?? "-"}, spell drawn {Combat.Fighter.SpellDrawn}, " +
                      $"animator {Combat.Fighter.Animator.StateText}, refusal {Combat.DrawRefusal() ?? "none"})");
        }

        /// <summary>The flight speed of a spell's missile, m/s (0 = not known: no lead).</summary>
        float SpellSpeed(string item) =>
            _session.Magic.TryGetSpell(item, out var def) && def?.Missile != null ? Mathf.Max(0f, def.Missile.Speed * SpellCasterBody.MissileSpeedScale) : 0f;

        /// <summary>fix-57: a moving target's velocity from its last positions (smoothed, flat), for leading a shot.</summary>
        sealed class TargetTrack
        {
            Vector3 _last, _velocity;
            float _lastAt = -1f;

            public Vector3 Velocity => _velocity;

            public void Feed(Vector3 p, float now)
            {
                if (_lastAt >= 0f && now - _lastAt > 1e-3f)
                {
                    var v = (p - _last) / (now - _lastAt);
                    v.y = 0f;
                    if (v.magnitude > 15f) v = Vector3.zero; // a teleport, not a run
                    _velocity = Vector3.Lerp(_velocity, v, 0.25f);
                }
                _last = p;
                _lastAt = now;
            }
        }

        /// <summary>
        /// fix-57: keeps aiming at <paramref name="id"/>'s body centre for <paramref name="seconds"/> (at least one frame),
        /// re-read every frame, led by its velocity x the flight time and, for an arrow or bolt (<paramref name="gravity"/>),
        /// held over it by the drop (<see cref="HoldOver"/>).
        /// </summary>
        IEnumerator AimTrack(ActorId id, TargetTrack track, float seconds, float speed, bool gravity)
        {
            float until = Time.time + seconds;
            do
            {
                if (TryActorPosition(id, out var p, out var view))
                {
                    track.Feed(p, Time.time);
                    var f = view != null ? view.GetComponent<CombatFighter>() : null;
                    var target = BodyCentre(p, f);
                    if (speed > 0f) target += track.Velocity * (Vector3.Distance(Combat.ShotFrom, target) / speed);
                    AimAt(gravity && speed > 0f ? HoldOver(target, speed) : target);
                }
                yield return null;
            } while (Time.time < until);
        }

        /// <summary>Keeps aiming at <paramref name="target"/> for <paramref name="seconds"/> (held over it for a shot of <paramref name="speed"/> m/s).</summary>
        IEnumerator AimFor(Vector3 target, float seconds, float speed = 0f, bool near = false)
        {
            float until = Time.time + seconds;
            do { AimAt(speed > 0f ? HoldOver(target, speed, near) : target); yield return null; } while (Time.time < until);
        }

        /// <summary>
        /// fix-56: where a G3 player puts the crosshair to hit <paramref name="target"/> with a shot of
        /// <paramref name="speed"/> m/s that drops under gravity (PathStyle Curve): a point far along the launch line from the
        /// bow (<see cref="RangedRules.LaunchElevation"/>). The screen-centre ray through it passes just over the foe, so the
        /// pick point — which the arrow flies at — lies on that line. Too close to matter: the target itself.
        /// </summary>
        Vector3 HoldOver(Vector3 target, float speed, bool near = false)
        {
            var from = Combat.ShotFrom;
            var d = target - from;
            var flat = new Vector3(d.x, 0f, d.z);
            float h = flat.magnitude;
            if (h < 1f) return target;
            RangedRules.LaunchElevation(h, d.y, speed, out float elevation);
            if (elevation - Mathf.Atan2(d.y, h) < 0.002f) return target;
            // fix-188 (near, the loose op): the point of the launch line over the target, not 200 m out: a tree 4 m behind a wolf
            // caught the screen-centre ray to the far point, the shot flew at the trunk and passed 0.3 m over the wolf's back
            float reach = near ? d.magnitude / Mathf.Max(0.2f, Mathf.Cos(elevation)) + 0.3f : HoldOverReach;
            return from + (flat / h * Mathf.Cos(elevation) + Vector3.up * Mathf.Sin(elevation)) * reach;
        }

        const float HoldOverReach = 200f; // within PlayerCombat.AimPoint's 300 m pick

        /// <summary>
        /// Turns the hero's look so the screen centre (the crosshair; PlayerCombat shoots at the point under it) sits on
        /// <paramref name="target"/>, as a player moves the mouse: G3PlayerCamera.LookFor solves the boom (shoulder offset,
        /// look-at lift above the hero). fix-56: the look used to point straight from the camera at the target, but the
        /// camera looks at a point TargetOffsetY above the hero, so the crosshair sat ~10° above the target.
        /// </summary>
        void AimAt(Vector3 target)
        {
            var cam = Camera.main;
            var rig = cam != null ? cam.GetComponent<G3PlayerCamera>() : null;
            if (rig != null)
            {
                rig.LookFor(target, out float yaw, out float pitch);
                Hero.SetLook(yaw, pitch);
                return;
            }
            var from = cam != null ? cam.transform.position : Hero.transform.position + Vector3.up * 1.6f;
            var d = target - from;
            if (d.sqrMagnitude < 0.01f) return;
            Hero.SetLook(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg);
        }

        /// <summary>Where a player puts the crosshair on a foe: the middle of its body (CombatBody capsule), else 1 m up.</summary>
        static Vector3 BodyCentre(Vector3 feet, CombatFighter f) =>
            f != null && f.Body != null ? f.Body.Center : feet + Vector3.up * (f != null ? 1.0f : 0.8f);

        // ---------------------------------------------------------------- spawn ground

        /// <summary>
        /// fix-54 (r30 melee_small: a spawned Meatbug ended 54 m below the hero): the ground for a new NPC at (x, z), as G3
        /// places an entity on the floor below it: the hits of a ray from 50 m above (any height when the level is unknown),
        /// leaving out bodies (the hero, NPCs, ragdolls and corpses) and triggers; the floor closest to <paramref name="level"/>
        /// within 6 m, walkable (slope &lt; 50°), with room for a body; else the same in rings up to 6 m. Then the nearest
        /// NavMesh point within 1.5 m of it, where NPCs walk.
        /// </summary>
        bool SpawnGround(Vector3 p, float level, bool knownLevel, out Vector3 ground, out string how)
        {
            ground = p;
            how = "no floor";
            // fix-184 (regress-1021b fix78_party_snoop: Mitch, Diego and Milten were put on the floor 5.4 m above the hero in
            // Mitch's house, Mitch never saw him): a floor within 3 m of the asked level (one storey) anywhere in the rings beats one up to 6 m off
            foreach (float tolerance in knownLevel ? new[] { 3f, 6f } : new[] { 6f })
            foreach (float r in new[] { 0f, 1.5f, 3f, 4.5f, 6f })
            {
                int n = r == 0 ? 1 : 8;
                for (int i = 0; i < n; i++)
                {
                    var c = p + Quaternion.Euler(0, i * 360f / n, 0) * Vector3.forward * r;
                    float top = knownLevel ? level + 50f : 1500f;
                    var hits = Physics.RaycastAll(new Vector3(c.x, top, c.z), Vector3.down, knownLevel ? 100f : 3000f, ~0, QueryTriggerInteraction.Ignore);
                    RaycastHit? best = null;
                    foreach (var x in hits)
                    {
                        var col = x.collider;
                        if (col.attachedRigidbody != null || col is CharacterController) continue;
                        if (col.GetComponentInParent<NpcView>() != null || col.GetComponentInParent<G3Player>() != null) continue;
                        if (x.normal.y < 0.64f) continue;
                        if (knownLevel && Mathf.Abs(x.point.y - level) > tolerance) continue;
                        if (InsideRock(x, hits)) continue;
                        if (!best.HasValue || Mathf.Abs(x.point.y - level) < Mathf.Abs(best.Value.point.y - level)) best = x;
                    }
                    if (!best.HasValue) { how = $"no walkable floor within {F(tolerance)} m of y {F(level)} at {V(c)}"; continue; }
                    var g = best.Value.point;
                    bool blocked = Physics.OverlapCapsule(g + Vector3.up * 0.75f, g + Vector3.up * 1.4f, 0.3f, ~0, QueryTriggerInteraction.Ignore)
                                          .Any(col => col.attachedRigidbody == null && !(col is CharacterController) && !(col is TerrainCollider) &&
                                                      col.GetComponentInParent<NpcView>() == null && col.GetComponentInParent<G3Player>() == null);
                    if (blocked) { how = $"no room at {V(g)}"; continue; }
                    if (NavMesh.SamplePosition(g, out var nav, 1.5f, NavMesh.AllAreas) && Mathf.Abs(nav.position.y - g.y) < 1f) g = nav.position;
                    ground = g;
                    how = $"floor {best.Value.collider.name} y {F(g.y)}" + (r > 0 ? $", moved {F(r)} m" : "");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// fix-188 (fix180_corpse_pose: FreeSlave_01 spawned 3 m off the Hammerclan path on a floor at y 177.7 under the
        /// one-sided landscape 7-10 m above it; it died, slid off that piece into the rock and lay "propped" 1.2 m over a
        /// ground the survey guessed): a floor with ground over it (a hit from above higher than 2 m) and nothing over it
        /// seen from below (a house has its ceiling, a cave its roof; inside a one-sided terrain mesh a ray up hits nothing) is
        /// inside the rock.
        /// </summary>
        static bool InsideRock(RaycastHit floor, RaycastHit[] hits)
        {
            bool covered = false;
            foreach (var h in hits)
                if (h.point.y > floor.point.y + 2f && h.collider.attachedRigidbody == null) { covered = true; break; }
            return covered && !Physics.Raycast(floor.point + Vector3.up * 0.5f, Vector3.up, 200f, ~0, QueryTriggerInteraction.Ignore);
        }

        /// <summary>After a spawn: a body found more than 2 m off its floor (fell through, landed on a roof) is put back on it.</summary>
        void CheckSpawnedOnGround(ActorId id, Vector3 floor)
        {
            if (Views == null || !Views.TryGetView(id, out var view) || view == null) return;
            float dy = view.position.y - floor.y;
            if (Mathf.Abs(dy) <= 2f) return;
            Log($"spawn {id}: body {F(dy)} m off its floor at {V(view.position)} -> put back on {V(floor)}");
            view.position = floor;
            NpcPlacement.Resolve(view, "spawn check");
        }
    }
}
#endif
