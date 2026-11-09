using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// An arrow or bolt in flight (Docs/combat.md §2, §9): a kinematic body whose path is swept every frame against the
    /// level (solid colliders) and the standing fighters' body capsules (<see cref="Sweep"/>, fix-68). The player's shots fly under
    /// gravity (G3 path style 2, Physics); NPC arrows and bolts are aimed once at release and fly straight on without drop
    /// (path style 0, Line: no re-aim, so a step aside dodges them; <see cref="ProjectileFlight"/>, fix-93). The first fighter touched gets the hit (<see cref="CombatPresenter"/> builds the
    /// missile <see cref="HitContext"/>); the arrow then ends as its template's TouchBehavior says (<see cref="ProjectileTouchRules"/>,
    /// fix-176): the explosive arrow is gone in its blast (KillSelf), bolts bounce off the level and fall (Reflect), arrows
    /// stick in wood, earth and flesh and bounce off stone (MaterialDependant), on a body they link to the nearest bone
    /// (LinkToBones; a blunt arrow falls off); one in a body fades after the template's DecayDuration (10 s). [owner,
    /// fix-195; G3 never gives them back] The arrow or bolt is had again (<see cref="SpentAmmo"/>): one that touched a
    /// character goes into its inventory (looted from the body), one that stuck in or came to rest on the level becomes
    /// a pickable dropped world item in that pose (this object goes, the item's view takes its place). The template's impact effect (the explosive arrow's blast) plays where it hits. Spell missiles (fireball,
    /// ice lance …) fly straight without gravity, aimed once at release at the focus target; only a power cast with a
    /// focus homes on it (G3 MagicProjectile PathStyle 3, fix-159); they burst where they hit (<see cref="CombatPresenter.OnSpellHit"/>).
    /// </summary>
    public sealed class Projectile : MonoBehaviour
    {
        /// <summary>What was shot, by whom.</summary>
        public sealed class Shot
        {
            public CombatFighter Shooter;
            public ItemDef Ammo;
            public ItemDef Weapon;
            public WeaponClass WeaponClass;
            public float HitMultiplier;
            public int Quality;
            /// <summary>
            /// The shooter's target (null for the player's aimed arrows): shots were aimed at it at release; only a power-cast
            /// spell missile homes on it (Missile, fix-159); a hit on it counts as on the shooter's target.
            /// </summary>
            public CombatFighter Homing;
            /// <summary>The released cast of a spell missile (null for arrows and bolts).</summary>
            public SpellCast Spell;

            public bool IsSpell => Spell != null;
        }

        const float MaxFlightSeconds = 10f;
        const float StickDepth = 0.06f;     // the tip goes this far into what it hits, m [interp]
        const float FadeSeconds = 0.5f;
        /// <summary>A projectile bouncing off keeps this much of its speed (G3 hands it to the physics) [interp].</summary>
        const float BounceKeep = 0.25f;
        /// <summary>A falling projectile that has not come to rest after this long simply stops [interp].</summary>
        const float MaxDropSeconds = 4f;

        CombatPresenter _presenter;
        Shot _shot;
        Vector3 _velocity;
        ProjectilePathStyle _style;
        Quaternion _meshToForward = Quaternion.identity;
        float _age, _stuckAt = -1, _droppedAt = -1;
        /// <summary>fix-195: the projectile was already given back (into a victim's inventory or the world).</summary>
        bool _recovered;
        Vector3 _scale;

        public Shot Info => _shot;
        /// <summary>An arrow or bolt hit the level (not a fighter): the impact sound (G3 OnItemTouchesLevel).</summary>
        public static event System.Action<Projectile, RaycastHit> HitLevel;
        /// <summary>The flight model of this shot (dev probes, fix-159).</summary>
        public ProjectilePathStyle Style => _style;
        /// <summary>Dev probes (fix-159, <c>missileflight</c>, <c>missileturn</c>): the flight model of the last spell missile
        /// launched, and how far (degrees) its velocity has turned from the release direction so far.</summary>
        public static string LastSpellFlight = "-";
        public static float LastSpellTurnDegrees;
        static Projectile _lastSpell;
        Vector3 _releaseDir;
        /// <summary>A shot left the bow, crossbow or hand (dev runner arrowdodge, fix-93).</summary>
        public static event System.Action<Projectile> Launched;
        public bool Stuck => _stuckAt >= 0;
        /// <summary>How the shot ended, with the impact effect played and its visual parts (fix-176: dev op <c>loose</c>, probes).</summary>
        public static event System.Action<Projectile, ProjectileRest, string, int> Rested;
        /// <summary>Dev counters per rest (probe <c>arrowrest:Vanish</c> …).</summary>
        public static readonly System.Collections.Generic.Dictionary<ProjectileRest, int> Rests = new System.Collections.Generic.Dictionary<ProjectileRest, int>();
        bool Dropping => _droppedAt >= 0 && !Stuck;
        /// <summary>
        /// fix-186 (dev op <c>loose</c>): what the shot touched first: "actor &lt;id&gt;" (a fighter's body capsule) or
        /// "level &lt;collider&gt; (&lt;layer&gt;)", with the point; null while in flight.
        /// </summary>
        public string Touched { get; private set; }

        /// <summary>Starts the flight at <paramref name="position"/>; <paramref name="tipAxis"/> is the mesh's local axis towards its tip.</summary>
        public void Launch(CombatPresenter presenter, Shot shot, Vector3 position, Vector3 velocity, Vector3 tipAxis)
        {
            _presenter = presenter;
            _shot = shot;
            _velocity = velocity;
            // The player's arrows and bolts carry no target (aimed by the crosshair); NPC shots carry theirs.
            _style = ProjectileFlight.StyleOf(player: shot.Homing == null, shot.IsSpell, hasTarget: shot.Homing != null,
                                             powerCast: shot.Spell != null && shot.Spell.Powered);
            _meshToForward = Quaternion.Inverse(Quaternion.LookRotation(tipAxis.sqrMagnitude > 1e-6f ? tipAxis.normalized : Vector3.forward));
            _scale = transform.localScale;
            transform.position = position;
            Orient();
            _releaseDir = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : Vector3.forward;
            if (shot.IsSpell)
            {
                _lastSpell = this;
                LastSpellFlight = _style.ToString();
                LastSpellTurnDegrees = 0;
            }
            Launched?.Invoke(this);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            _age += dt;
            if (Stuck)
            {
                Decay();
                return;
            }
            if (Dropping)
            {
                Fall(dt);
                return;
            }
            if (_age > MaxFlightSeconds)
            {
                Destroy(gameObject);
                return;
            }
            // G3 ShootIteration: only a Missile (power-cast spell at a target) is re-aimed; the player's Physics shot falls; an NPC's Line flies straight.
            if (ProjectileFlight.ReAims(_style)) Home();
            else if (ProjectileFlight.Falls(_style)) _velocity += Vector3.down * (RangedRules.Gravity * dt);
            var from = transform.position;
            var step = _velocity * dt;
            if (Sweep(from, step)) return;
            transform.position = from + step;
            Orient();
            if (_lastSpell == this) LastSpellTurnDegrees = Mathf.Max(LastSpellTurnDegrees, Vector3.Angle(_releaseDir, _velocity));
        }

        void Home()
        {
            var target = _shot.Homing;
            if (target == null || target.Body == null || !target.isActiveAndEnabled) return;
            var to = target.Body.Center - transform.position;
            // G3 ShootIteration (Missile): every TargetUpdateMSec the velocity is set straight to GetDirection (the target's
            // spine, at ShootVelocity); all Mis_ templates have TargetUpdateMSec 0, so a powered missile re-aims every frame.
            if (to.sqrMagnitude > 1e-6f) _velocity = to.normalized * _velocity.magnitude;
        }

        /// <summary>
        /// The first thing on the segment (fix-68): the level (non-trigger colliders that are not a fighter's), or the body
        /// of any standing fighter but the shooter, whichever the path meets first. Fighters are tested as their body
        /// capsules (<see cref="ProjectilePath"/>, G3: projectiles collide with every character's movement capsule), not
        /// through physics triggers, so a body in the way always takes the arrow: an orc between an orc archer and the
        /// hero gets it (damage then per G3's friendly-fire gates, <see cref="CombatPresenter.OnProjectileHit"/>), and the
        /// arrow sticks in the first wall, rock or body it meets. Lying bodies (downed, dead) are flown over [interp: their
        /// upright capsule does not fit them]; fix-241: with the port option "Arrows finish downed foes" a downed NPC is
        /// tested as its lying capsule (<see cref="LyingBody"/>) and the touch finishes it (<see cref="CombatPresenter.OnProjectileHit"/>).
        /// </summary>
        bool Sweep(Vector3 from, Vector3 step)
        {
            float length = step.magnitude;
            if (length < 1e-5f) return false;
            var dir = step / length;
            // The level: the nearest solid collider that does not belong to a fighter.
            RaycastHit level = default;
            float levelAt = float.MaxValue;
            int n = Physics.RaycastNonAlloc(from, dir, Hits, length, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = Hits[i];
                if (h.distance >= levelAt || h.collider.GetComponentInParent<CombatFighter>() != null) continue;
                level = h;
                levelAt = h.distance;
            }
            if (WatchTarget != null && _shot.Shooter != null && _shot.Shooter.Actor.IsPlayer) Watch(from, step);
            // The bodies: the first capsule the path enters.
            CombatFighter body = null;
            float bodyAt = float.MaxValue;
            if (_presenter != null)
            {
                var p0 = P(from);
                var p1 = P(from + step);
                var vitals = _presenter.Session?.Combat.Vitals;
                // fix-241: with "Arrows finish downed foes" a downed body is hit as its lying pose (head to legs)
                bool downedTargets = _presenter.Session?.Combat.Port.RangedFinishesDowned == true;
                foreach (var f in _presenter.Fighters)
                {
                    if (f == null || f == _shot.Shooter || !f.isActiveAndEnabled || f.Body == null) continue;
                    var state = vitals != null ? vitals.StateOf(f.Actor) : VitalState.Alive;
                    Vector3 a, b;
                    float r;
                    if (state == VitalState.Down)
                    {
                        if (!downedTargets || f.Actor.IsPlayer || !LyingBody.Capsule(f, out a, out b, out r)) continue;
                    }
                    else if (state != VitalState.Alive || !f.Body.WorldCapsule(out a, out b, out r)) continue;
                    var enter = ProjectilePath.Enter(p0, p1, P(a), P(b), r);
                    if (enter == null) continue;
                    float at = enter.Value * length;
                    if (at < bodyAt) { body = f; bodyAt = at; }
                }
            }
            if (body != null && bodyAt <= levelAt)
            {
                var point = from + dir * bodyAt;
                Touched = $"actor {body.Actor} at {point}";
                if (_shot.IsSpell)
                {
                    _presenter?.OnSpellHit(this, body, point);
                    Burst(point);
                    return true;
                }
                G3.Unity.G3ItemEffect.PlayImpact(_shot.Ammo?.Id, point, hitTarget: true, out var fx, out int parts); // explosive arrow blast (EffectTargetHit)
                BodyHits[body.Actor] = BodyHits.TryGetValue(body.Actor, out int had) ? had + 1 : 1;
                var bone = _presenter != null ? _presenter.OnProjectileHit(this, body, point) : null;
                var data = ProjectileTouchRules.Of(_shot.Ammo);
                var onBody = ProjectileTouchRules.OnCharacter(ProjectileTouchRules.TouchOf(data), data.LinkToBones);
                Rest(onBody, point, dir, -dir, bone != null ? bone : body.transform, body.transform, fx, parts);
                Recover(onBody, body.Actor, false); // fix-195: into the victim's inventory
                return true;
            }
            if (levelAt == float.MaxValue) return false;
            Touched = $"level {level.collider.name} ({LayerMask.LayerToName(level.collider.gameObject.layer)}) at {level.point}";
            if (_shot.IsSpell)
            {
                Burst(level.point);
                return true;
            }
            G3.Unity.G3ItemEffect.PlayImpact(_shot.Ammo?.Id, level.point, hitTarget: false, out var impact, out int impactParts); // EffectPointImpact
            LevelHits++;
            HitLevel?.Invoke(this, level); // impact sound by surface (CombatSounds)
            var touch = ProjectileTouchRules.TouchOf(ProjectileTouchRules.Of(_shot.Ammo));
            var onLevel = ProjectileTouchRules.OnLevel(touch, ShapeMaterial(level.collider));
            Rest(onLevel, level.point, dir, level.normal, null, null, impact, impactParts);
            if (onLevel != ProjectileRest.Drop) Recover(onLevel, default, true); // fix-195: a pickable item where it stuck (a dropping one: where it lies)
            return true;
        }

        /// <summary>fix-188 dev (loose op): the target whose capsule the player's shot is measured against; <see cref="WatchNote"/> = the closest pass.</summary>
        public static CombatFighter WatchTarget;
        public static string WatchNote;
        static float s_watchBest = float.MaxValue;

        /// <summary>Resets the dev watch for a new shot.</summary>
        public static void BeginWatch(CombatFighter target) { WatchTarget = target; WatchNote = null; s_watchBest = float.MaxValue; }

        void Watch(Vector3 from, Vector3 step)
        {
            var f = WatchTarget;
            var vitals = _presenter != null ? _presenter.Session?.Combat.Vitals : null;
            string state = $"enabled {(f != null && f.isActiveAndEnabled)} body {(f != null && f.Body != null)} vital {(vitals != null && f != null ? vitals.StateOf(f.Actor).ToString() : "-")} " +
                           $"listed {(_presenter != null && f != null && System.Linq.Enumerable.Contains(_presenter.Fighters, f))}";
            if (f == null || f.Body == null || !f.Body.WorldCapsule(out var a, out var b, out float r)) { if (WatchNote == null) WatchNote = "no capsule: " + state; return; }
            float best = float.MaxValue;
            for (int i = 0; i <= 16; i++)
            {
                var q = from + step * (i / 16f);
                var ab = b - a;
                float t = ab.sqrMagnitude > 1e-9f ? Mathf.Clamp01(Vector3.Dot(q - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector3.Distance(q, a + ab * t));
            }
            if (best >= s_watchBest) return;
            s_watchBest = best;
            WatchNote = $"closest {best:F2} m (r {r:F2}) seg {from}->{from + step} capsule {a}..{b} age {_age:F2} {state}";
        }

        static readonly RaycastHit[] Hits = new RaycastHit[16];

        /// <summary>The eEShapeMaterial of a level collider (the collision builder's "G3Surface_&lt;n&gt;_…" material), or null.</summary>
        static int? ShapeMaterial(Collider c)
        {
            var m = c != null ? c.sharedMaterial : null;
            return m != null && G3.Unity.SurfaceProbe.TryShapeMaterial(m.name, out int shape) ? shape : (int?)null;
        }

        /// <summary>
        /// Ends the flight as <paramref name="rest"/> says (gCProjectile_PS::OnTouch, fix-176). On every touch the
        /// projectile's own effect stops emitting and its light goes out (StopAllRuntimeEffects: the fire arrow's flame).
        /// </summary>
        void Rest(ProjectileRest rest, Vector3 point, Vector3 dir, Vector3 normal, Transform bone, Transform body, string impact, int parts)
        {
            Rests[rest] = Rests.TryGetValue(rest, out int n) ? n + 1 : 1;
            G3.Unity.G3ItemEffect.Extinguish(gameObject);
            // fix-184: told after the stick (the dev op reads what it stuck in from its parent)
            if (rest != ProjectileRest.Stick && rest != ProjectileRest.StickToBone) Rested?.Invoke(this, rest, impact, parts);
            switch (rest)
            {
                case ProjectileRest.Vanish:
                    Destroy(gameObject);
                    return;
                case ProjectileRest.Drop:
                {
                    // physics on: it bounces back off the surface and falls [interp: G3 hands it to its rigid body];
                    // off a body (blunt arrow) it drops at the victim's feet
                    var n0 = normal.sqrMagnitude > 1e-6f ? normal.normalized : -dir;
                    _velocity = body != null ? -dir : Vector3.Reflect(_velocity, n0) * BounceKeep;
                    transform.position = point + n0 * 0.03f;
                    _droppedAt = _age;
                    return;
                }
                default:
                    StickTo(point, dir, rest == ProjectileRest.StickToBone ? bone : body);
                    Rested?.Invoke(this, rest, impact, parts);
                    return;
            }
        }

        /// <summary>A bounced or dropped projectile falls until it lies on the level (it hits no one any more).</summary>
        void Fall(float dt)
        {
            if (_age - _droppedAt > MaxDropSeconds)
            {
                _stuckAt = _age;
                Recover(ProjectileRest.Drop, default, false); // did not come to rest: the item settles on the ground below
                return;
            }
            _velocity += Vector3.down * (RangedRules.Gravity * dt);
            var from = transform.position;
            var step = _velocity * dt;
            float length = step.magnitude;
            if (length > 1e-5f && Physics.Raycast(from, step / length, out var hit, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                hit.collider.GetComponentInParent<CombatFighter>() == null)
            {
                // lies down along the surface
                var along = Vector3.ProjectOnPlane(_velocity, hit.normal);
                _velocity = along.sqrMagnitude > 1e-6f ? along.normalized : Vector3.Cross(hit.normal, Vector3.right).normalized;
                Orient();
                transform.position = hit.point + hit.normal * 0.02f;
                _stuckAt = _age;
                Recover(ProjectileRest.Drop, default, true); // fix-195: a pickable item where it lies
                return;
            }
            transform.position = from + step;
            transform.rotation = Quaternion.AngleAxis(540f * dt, transform.right) * transform.rotation; // tumbles
        }

        /// <summary>Dev counters (runner probes <c>arrowhits:actor</c>, <c>arrowlevel</c>; fix-68): arrows and bolts that
        /// stuck in a body, per victim, and in the level.</summary>
        public static readonly System.Collections.Generic.Dictionary<Myrtana.Core.ActorId, int> BodyHits = new System.Collections.Generic.Dictionary<Myrtana.Core.ActorId, int>();
        public static int LevelHits;

        static P3 P(Vector3 v) => new P3(v.x, v.y, v.z);

        /// <summary>A spell missile ends where it hits: the impact effect, then it is gone.</summary>
        void Burst(Vector3 point)
        {
            var missile = _shot.Spell.Spell.Missile;
            if (!G3.Unity.G3EffectPlayer.Has(missile?.ImpactEffect)) SpellFx.Burst(point, SpellFx.ColorOf(_shot.Spell.Spell));
            G3.Unity.G3EffectSounds.Play(missile?.ImpactEffect, point); // sound + G3 impact visuals
            Destroy(gameObject);
        }

        /// <summary>
        /// fix-195 [owner]: gives the spent arrow or bolt back (<see cref="SpentAmmo.Recover"/>) once: into
        /// <paramref name="victim"/>'s inventory, or (no victim) as a dropped world item where this object lies, in its
        /// pose when <paramref name="keepPose"/> (else resting on the ground below); then this object goes, the item's view
        /// takes its place. Spell missiles and shots without a session are left alone.
        /// </summary>
        void Recover(ProjectileRest rest, Myrtana.Core.ActorId victim, bool keepPose)
        {
            if (_recovered || _shot == null || _shot.IsSpell || _shot.Ammo == null) return;
            var session = _presenter != null ? _presenter.Session : null;
            if (session == null) return;
            _recovered = true;
            transform.GetPositionAndRotation(out var p, out var q);
            var shooter = _shot.Shooter != null ? _shot.Shooter.Actor : default;
            var fate = SpentAmmo.Recover(session, shooter, _shot.Ammo.Id, _shot.Quality, rest, victim, new[] { p.x, p.y, p.z },
                                         keepPose ? new[] { q.x, q.y, q.z, q.w } : null);
            Recovered[fate] = Recovered.TryGetValue(fate, out int n) ? n + 1 : 1;
            if (fate == SpentAmmoFate.Lies) Destroy(gameObject);
        }

        /// <summary>Dev counters per fate of the spent projectiles (fix-195).</summary>
        public static readonly System.Collections.Generic.Dictionary<SpentAmmoFate, int> Recovered = new System.Collections.Generic.Dictionary<SpentAmmoFate, int>();

        void StickTo(Vector3 point, Vector3 dir, Transform parent)
        {
            _velocity = dir;
            Orient();
            transform.position = point + dir * StickDepth;
            if (parent != null) transform.SetParent(parent, true);
            _stuckAt = _age;
        }

        void Decay()
        {
            float t = _age - _stuckAt - RangedRules.DecaySeconds;
            if (t < 0) return;
            if (t >= FadeSeconds)
            {
                Destroy(gameObject);
                return;
            }
            transform.localScale = _scale * (1 - t / FadeSeconds); // FadeOnDecay [interp: shrinks instead of fading]
        }

        void Orient()
        {
            if (_velocity.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(_velocity) * _meshToForward;
        }
    }
}
