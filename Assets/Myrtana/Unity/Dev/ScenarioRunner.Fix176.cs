#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-176 (Docs/dev-scenarios.md): one aimed shot of the equipped ammunition and how it ended (<see cref="ProjectileTouchRules"/>:
    /// the explosive arrow's blast and KillSelf, arrows sticking or bouncing off by material, bolts bouncing, blunt arrows
    /// falling off a body).
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        string _looseRest, _looseImpact;
        int _looseParts;

        /// <summary>
        /// 'loose target [ammo=Id] [at=ground] [draw=2.05] [wait=4]': the bow or crossbow drawn (drawn when not), the crosshair held over
        /// the target (an actor's body centre; at=ground: the ground 2 m short of it; or a point x,y,z / x,z on the ground), a full draw, released; waits up to wait
        /// s for the shot's touch. ammo= equips that stack first (one is given when none is carried). Logs the ammunition,
        /// what it touched, its end (Stick/StickToBone/Vanish/Drop), the impact effect and its visual parts (probes
        /// looserest, looseimpact, looseparts); fails when the shot touched nothing.
        /// </summary>
        /// <summary>fix-186: the bow's state for the loose op's logs.</summary>
        string BowState(G3.Unity.Characters.G3CombatAnimator a) =>
            $"nocked {a.Nocked} ({Combat.NockedAmmo ?? "-"}) busy {a.Busy} aim {a.InAim} held {a.AimHeld} shot {a.InShot} reload {a.InReload} " +
            $"drawn {Combat.Fighter.RangedDrawn} button {Combat.PrimaryHeld}";

        IEnumerator LooseStep(ScenarioStep st)
        {
            var s = _session;
            int failuresBefore = _failures.Count;
            string ammo = st.Get("ammo");
            if (!string.IsNullOrEmpty(ammo))
            {
                if (s.Items.Inventories.Of(ActorId.Player).Count(ammo) < 1) s.Items.Inventories.Give(ActorId.Player, ammo, 1);
                if (s.Items.Equipment.In(WornSlot.Ammo) != ammo && s.Items.Equipment.Equip(ammo) is var res && res != EquipResult.Done)
                {
                    Fail($"loose: cannot equip {ammo} ({res})");
                    yield break;
                }
            }
            if (!Combat.Fighter.RangedDrawn)
            {
                yield return DrawStep("ranged");
                if (!Combat.Fighter.RangedDrawn) { if (_failures.Count == failuresBefore) Fail("loose: no bow or crossbow drawn"); yield break; }
            }
            if (!TryTarget(st.Get("target"), out var p, out bool hasY, out var actor, out _, out var err)) { Fail("loose: " + err); yield break; }
            if (!hasY && FindGround(p, true, out var ground, out _, null)) p = ground.point;
            var f = actor.HasValue ? FighterOf(actor.Value) : null;
            var aim = actor.HasValue ? BodyCentre(p, f) : p;
            if (actor.HasValue && st.Get("at") == "ground")
            {
                // at=ground: the ground 2 m short of the actor, on the hero's side
                var back = Hero.transform.position - p;
                back.y = 0f;
                var near = p + (back.sqrMagnitude > 1e-4f ? back.normalized : Vector3.zero) * 2f;
                aim = FindGround(near, true, out var g2, out _, f != null ? f.transform : null) ? g2.point : near;
            }
            var a = Combat.Fighter.Animator;
            float t0 = Time.time;
            // fix-186 (regress-1021b/editor: 'no shot left the bow' on a loose right after the previous shot): the bow ready
            // first: the last shot's recover and reload over, an arrow (the chosen one, after the renock) on the string
            string wanted = s.Items.Equipment.In(WornSlot.Ammo);
            while ((a.Busy || a.InShot || a.InReload || !a.Nocked || (wanted != null && Combat.NockedAmmo != wanted)) && Time.time - t0 < 4f)
            {
                AimAt(aim);
                yield return null;
            }
            float speed = RangedRules.ShotVelocity(true, 1f);
            yield return AimFor(aim, 0.2f, speed, near: true);
            _looseRest = null; _looseImpact = null; _looseParts = 0;
            // fix-188 ('no shot left the bow' with nocked False / shot True right after an explosive loose): the explosive arrow
            // Vanishes = Destroy(gameObject), and Unity's '== null' on the destroyed Projectile then read true, so a shot that
            // flew and burst counted as none (intermittent: whether the destroy ran before this coroutine resumed). Plain
            // references only: ReferenceEquals for the shot, its Shot info kept for the log.
            Projectile shot = null;
            Projectile.Shot shotInfo = null;
            string touched = null;
            System.Action<Projectile> onLaunch = pr =>
            {
                if (ReferenceEquals(shot, null) && pr.Info?.Shooter != null && pr.Info.Shooter.Actor.IsPlayer) { shot = pr; shotInfo = pr.Info; }
            };
            System.Action<Projectile, ProjectileRest, string, int> onRest = (pr, rest, fx, parts) =>
            {
                if (ReferenceEquals(shot, null) || !ReferenceEquals(pr, shot)) return;
                _looseRest = rest.ToString();
                _looseImpact = fx ?? "-";
                _looseParts = parts;
                // fix-186: what the sweep touched (a body capsule or a level collider), not the parent it stuck to
                touched = (pr.Touched ?? "-") + (pr.transform.parent != null ? ", in " + pr.transform.parent.name : "");
            };
            Projectile.BeginWatch(f); // fix-188: how close the shot passed the target (logged on a miss)
            Projectile.Launched += onLaunch;
            Projectile.Rested += onRest;
            string used = s.Items.Equipment.In(WornSlot.Ammo) ?? "-";
            try
            {
                Combat.PrimaryDown();
                float aimStart = Time.time;
                while (!a.AimHeld && Time.time - aimStart < 3f) { AimAt(HoldOver(aim, speed, near: true)); yield return null; }
                yield return AimFor(aim, st.Float("draw", RangedRules.BowFullDrawSeconds + 0.1f), speed, near: true);
                Combat.PrimaryUp();
                float until = Time.time + st.Float("wait", 4f);
                float retryAt = Time.time + 1f;
                bool retried = false;
                while (_looseRest == null && Time.time < until)
                {
                    if (ReferenceEquals(shot, null) && !retried && Time.time >= retryAt)
                    {
                        // fix-186: the press was lost (no shot 1 s after the release): a player clicks again
                        retried = true;
                        Log($"loose: no shot 1 s after the release ({BowState(a)}), pressing again");
                        Combat.PrimaryDown();
                        float again = Time.time;
                        while (!a.AimHeld && Time.time - again < 3f) { AimAt(HoldOver(aim, speed, near: true)); yield return null; }
                        yield return AimFor(aim, st.Float("draw", RangedRules.BowFullDrawSeconds + 0.1f), speed, near: true);
                        Combat.PrimaryUp();
                        until = Time.time + st.Float("wait", 4f);
                    }
                    yield return null;
                }
            }
            finally
            {
                Projectile.Launched -= onLaunch;
                Projectile.Rested -= onRest;
                if (Combat.PrimaryHeld) Combat.PrimaryUp();
                Projectile.WatchTarget = null;
            }
            if (ReferenceEquals(shot, null)) { Fail($"loose {st.Get("target")}: no shot left the bow ({used}; {BowState(a)})"); yield break; }
            if (_looseRest == null) { Fail($"loose {st.Get("target")}: the {used} shot touched nothing within {st.Float("wait", 4f)} s"); yield break; }
            var info = shotInfo;
            string shotAmmo = info?.Ammo?.Id ?? "-";
            Log($"loose {st.Get("target")}: {used} (shot {shotAmmo} x{(info != null ? info.HitMultiplier : 0f):0.00} q{(info != null ? info.Quality : 0)}) -> {_looseRest} " +
                $"({touched}), impact {_looseImpact} parts {_looseParts}" +
                (f != null && touched != null && !touched.StartsWith("actor " + f.Actor) ? $"; MISSED the target: {Projectile.WatchNote ?? "-"}" : ""));
        }
    }
}
#endif
