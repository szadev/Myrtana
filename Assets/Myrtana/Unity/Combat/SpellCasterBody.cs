using System;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The body side of casting for one fighter, the hero or an NPC (Docs/combat.md §12): the cast motion on the upper-body
    /// overlay (<see cref="G3CombatAnimator.BeginCast"/>: Raise held while loading, Hit releases, Recover), the mana phases
    /// through <see cref="MagicService"/>, the placeholder glow in the hand, the G3 effect sounds, and the delivery when the
    /// motion releases the spell: a missile (<see cref="CombatPresenter.CastMissile"/>; homing only when power-cast) or an instant effect (heal …).
    /// Who casts what and when is decided by the owner (the player's buttons, the NPC brain).
    /// </summary>
    public sealed class SpellCasterBody
    {
        /// <summary>G3 ShootVelocity 100 of spell missiles → 20 m/s [interp: arrows use the value as m/s; spells fly visibly slower in G3].</summary>
        public const float MissileSpeedScale = 0.2f;

        readonly CombatPresenter _presenter;
        readonly CombatFighter _fighter;
        Transform _hand;
        GameObject _orb;
        int _heldFx, _loadFx, _powerFx;   // G3 effect handles (G3EffectPlayer): spell in the hand, loading, powered
        SpellCast _cast;
        CombatFighter _target;
        Func<Vector3> _aimPoint;

        public SpellCasterBody(CombatPresenter presenter, CombatFighter fighter)
        {
            _presenter = presenter;
            _fighter = fighter;
            fighter.Animator.CastReleased += OnCastReleased;
            fighter.Animator.RangedEnded += OnRangedEnded;
        }

        /// <summary>The cast in progress (loading, powered or released and still in its motion), or null.</summary>
        public SpellCast Cast => _cast;
        public bool Casting => _cast != null;
        /// <summary>A cast ended: its motion is over (or it was aborted). The cast says whether it was released.</summary>
        public event Action<SpellCast> Finished;
        /// <summary>A spell left the hand (delivered).</summary>
        public event Action<SpellCast> Released;

        GameSession Session => _presenter.Session;
        G3CombatAnimator Animator => _fighter.Animator;

        /// <summary>The spell in the hand glows while the spell hand is drawn (G3 EffectItemHold) [interp: placeholder orb].</summary>
        public void ShowHeld(SpellDef spell)
        {
            HideHeld();
            if (spell == null || Hand() == null) return;
            if (G3EffectPlayer.Has(spell.Visuals.ItemHold)) _heldFx = G3EffectPlayer.Start(spell.Visuals.ItemHold, Hand(), withSound: false);
            else _orb = SpellFx.Orb(Hand(), SpellFx.ColorOf(spell), 0.06f, 1.2f);
        }

        public void HideHeld()
        {
            if (_orb != null) UnityEngine.Object.Destroy(_orb);
            _orb = null;
            G3EffectPlayer.Stop(_heldFx);
            _heldFx = 0;
        }

        /// <summary>
        /// Starts a cast from <paramref name="item"/>: the mana phase Load (the minimum is paid) and the Raise motion of the
        /// cast action (the spell's own animation, the action, or plain Cast). False when the item casts no spell or the
        /// body cannot cast now.
        /// </summary>
        public bool Begin(string item, string casterSpecies, CastAction? action = null)
        {
            if (Session == null || _cast != null) return false;
            var cast = Session.Magic.Begin(_fighter.Actor, item, casterSpecies, Time.timeAsDouble, action: action);
            if (cast == null) return false;
            Animator.CastSpeed = SpellCasting.MotionSpeed(cast.Action, _fighter.Actor.Name, Session.Magic.Rules, Session.Settings.Rules);
            bool started;
            try
            {
                started = Animator.BeginCast(cast.Spell.Animation, ActionName(cast.Action), "Cast");
            }
            catch
            {
                // The load already paid the mana: a failing motion must not eat it and leave a half-begun cast (fix-110).
                Session.Magic.Abort(cast);
                Animator.AbortCast();
                throw;
            }
            if (!started)
            {
                Session.Magic.Abort(cast);
                return false;
            }
            _cast = cast;
            ReleaseQueued = false;
            if (_orb != null) _orb.transform.localScale = Vector3.one * 1.6f;
            var v = cast.Spell.Visuals;
            _loadFx = Effect(v.ItemLoad, v.CasterLoad);
            return true;
        }

        /// <summary>Per frame: a held power cast powers up after 0.7 s; an interrupted motion (a hit, a fall) aborts the cast and refunds.</summary>
        public void Update()
        {
            if (_cast == null) return;
            if (!Animator.InCast)
            {
                if (!_cast.Released)
                {
                    if (_fighter.Actor.IsPlayer) Debug.Log($"[Myrtana] cast {_cast.Spell.Id} aborted: the cast motion was cut ({Animator.StateText})");
                    Abort();
                }
                else OnRangedEnded("Cast"); // the recover was cut short (a hit): the cast is over all the same
                return;
            }
            if (Session.Magic.Update(_cast, Time.timeAsDouble)) OnPowered();
        }

        /// <summary>An NPC's chosen power cast: powered at once.</summary>
        public void PowerUp()
        {
            if (_cast == null || _cast.Phase != CastPhase.Load) return;
            Session.Magic.PowerUp(_cast);
            if (_cast.Powered) OnPowered();
        }

        /// <summary>Lets the spell go: the Hit motion releases it at <paramref name="target"/> (aimed at release; homing if power-cast), else towards <paramref name="aimPoint"/>.</summary>
        public bool Release(CombatFighter target, Func<Vector3> aimPoint)
        {
            if (_cast == null || _cast.Released) return false;
            _target = target;
            _aimPoint = aimPoint;
            return Animator.ReleaseCast();
        }

        /// <summary>
        /// G3 _AI_Cast (fix-114, <see cref="CastTrigger"/>): the spell goes off by itself during the cast motion — the Hit
        /// follows as soon as the Raise has played through, whatever the button does. <paramref name="target"/> and
        /// <paramref name="aimPoint"/> are read when it leaves the hand.
        /// </summary>
        public bool ReleaseAfterRaise(CombatFighter target, Func<Vector3> aimPoint)
        {
            if (_cast == null || _cast.Released) return false;
            _target = target;
            _aimPoint = aimPoint;
            ReleaseQueued = true;
            return Animator.ReleaseCastAfterRaise();
        }

        /// <summary>The cast goes off by itself at the end of its raise (<see cref="ReleaseAfterRaise"/>).</summary>
        public bool ReleaseQueued { get; private set; }

        /// <summary>Drops the cast; the mana comes back.</summary>
        public void Abort()
        {
            if (_cast == null) return;
            var cast = _cast;
            _cast = null;
            Session?.Magic.Abort(cast);
            StopCharge();
            Animator.AbortCast();
            if (_orb != null) _orb.transform.localScale = Vector3.one;
            Finished?.Invoke(cast);
        }

        void OnPowered()
        {
            if (_orb != null) _orb.transform.localScale = Vector3.one * 2.5f;
            _powerFx = Effect(_cast.Spell.Visuals.ItemPower, null);
        }

        void OnCastReleased()
        {
            var cast = _cast;
            if (cast == null || Session == null) return;
            if (!Session.Magic.Release(cast))
            {
                Abort();
                return;
            }
            if (_orb != null) _orb.transform.localScale = Vector3.one;
            var v = cast.Spell.Visuals;
            StopCharge();
            // G3 EffectCasterCast on the caster (fear, charm, blood cloud, the cures, summon circles …); before fix-110 only
            // its sound was played. The item cast effect is often the missile's own effect, so it stays a sound.
            var route = SpellRoutes.Of(cast.Spell, Session.Magic.Rules);
            if (SpellRoutes.ShowsCasterCast(cast.Spell, route) && G3EffectPlayer.Has(v.CasterCast))
            {
                G3EffectPlayer.Play(v.CasterCast, _fighter.transform.position);
                if (v.ItemCast != null) Sound(v.ItemCast);
            }
            else Sound(v.ItemCast ?? v.CasterCast);
            // A listener of the release may already have put the spell away (a teleport stone: PlayerCombat.PutAwayAtOnce
            // on TeleportRequested); the teleport itself is the spell's effect then.
            if (_cast == cast) Deliver(cast);
            // The soul travel hold effect is a large white mist; with the ghost the spell stays drawn, so it would sit
            // around the camera for the whole travel (editor-runner-13). It ends with the cast.
            if (cast.Spell.Effect == "MagicSoulTravel" && _heldFx != 0)
            {
                G3EffectPlayer.End(_heldFx);
                _heldFx = 0;
            }
            Released?.Invoke(cast);
        }

        void OnRangedEnded(string what)
        {
            if (what != "Cast" || _cast == null) return;
            var cast = _cast;
            _cast = null;
            StopCharge();
            Finished?.Invoke(cast);
        }

        void Deliver(SpellCast cast)
        {
            var spell = cast.Spell;
            var target = _target != null && _target.isActiveAndEnabled ? _target : null;
            var route = SpellRoutes.Of(spell, Session.Magic.Rules);
            bool applied = Apply(cast, route, target);
            SpellDeliveryLog.Record(_fighter.Actor, spell, route, applied);
        }

        /// <summary>Carries out a released spell by its route (<see cref="SpellRoutes.Of"/>); true when it took effect.</summary>
        bool Apply(SpellCast cast, SpellRoute route, CombatFighter target)
        {
            var spell = cast.Spell;
            switch (route)
            {
                case SpellRoute.Missile:
                    var from = Origin();
                    var aim = target != null ? target.Body.Center : _aimPoint != null ? _aimPoint() : from + _fighter.transform.forward * 30f;
                    float speed = (spell.Missile?.Speed ?? 100f) * MissileSpeedScale;
                    _presenter.CastMissile(new Projectile.Shot { Shooter = _fighter, Spell = cast, Homing = target, HitMultiplier = 1f },
                                           from, (aim - from).normalized * speed);
                    return true;
                case SpellRoute.Area:
                {
                    var area = Session.Magic.Rules.AreaSpellFor(spell);
                    var centre = spell.NeedsFocus && target != null ? target.transform.position : _fighter.transform.position;
                    AreaSpellObject.Spawn(_presenter, _fighter, spell, area, centre);
                    return true;
                }
                case SpellRoute.Summon:
                {
                    if (Session.Summoning == null) return false;
                    var t = _fighter.transform;
                    var at = new Myrtana.Simulation.WorldPose(t.position.x, t.position.y, t.position.z, t.eulerAngles.y);
                    int intelligence = _presenter.Snapshot(_fighter).Intelligence;
                    var created = Session.Summoning.Summon(_fighter.Actor, spell, intelligence, at);
                    if (created.Count == 0) Debug.Log($"[Myrtana] {_fighter.Actor} cast {spell.Id}: nothing could be summoned");
                    return created.Count > 0;
                }
                case SpellRoute.World:
                    return WorldSpellView.Deliver(_presenter, _fighter, cast, target); // magic-3: sleep, control, mist, forms … (§12.5)
                case SpellRoute.Fear:
                    return Fear(spell, target);
                case SpellRoute.Light:
                    return true; // EffectsView: the light ball follows the caster (SpellCasted)
                case SpellRoute.Instant:
                case SpellRoute.Teleport:
                {
                    var on = spell.NeedsFocus && target != null ? target : _fighter;
                    if (!Session.Magic.ApplyInstant(cast, on.Actor)) return false;
                    if (route == SpellRoute.Teleport) return true; // EffectsView shows the teleport
                    if (G3EffectPlayer.Has(spell.Visuals.TargetCast)) G3EffectPlayer.Play(spell.Visuals.TargetCast, on.transform.position, on.transform);
                    else if (!G3EffectPlayer.Has(spell.Visuals.CasterCast)) SpellFx.Burst(on.Body.Center, SpellFx.ColorOf(spell), 1f);
                    return true;
                }
                default:
                    Debug.Log($"[Myrtana] {_fighter.Actor} cast {spell.Id}: {spell.Effect ?? spell.Delivery.ToString()} has no effect ({route}, Docs/combat.md §12)");
                    return false;
            }
        }

        /// <summary>MagicFear (Docs/combat.md §12.3): the target flees, or attacks the caster when it resists.</summary>
        bool Fear(SpellDef spell, CombatFighter target)
        {
            if (target == null || target.Actor.IsPlayer)
            {
                // A focus spell released with no focus (G3: the cast goes off at nothing).
                Debug.Log($"[Myrtana] {_fighter.Actor} cast {spell.Id}: no target ({(target == null ? "no focus" : "the player")})");
                return false;
            }
            var def = _presenter.CombatDefOf(target.Actor);
            var facts = new ControlTarget
            {
                Species = def?.Species, Evil = def != null && def.Evil, Boss = def != null && def.Boss,
                Mage = def != null && (def.Mage || def.Class == NpcClass.Mage),
            };
            var outcome = ControlSpells.Fear(facts);
            bool applied = outcome == ControlOutcome.Works
                ? Session.NpcCombat.Flee(target.Actor, _fighter.Actor)
                : Session.NpcCombat.Engage(target.Actor, _fighter.Actor, AttackReason.ReactToDamage);
            Debug.Log($"[Myrtana] {_fighter.Actor} cast {spell.Id} at {target.Actor}: {outcome} → {(outcome == ControlOutcome.Works ? "flee" : "attack")} (brain {(applied ? "set" : "none")})");
            Sound(spell.Visuals.TargetCast, target.transform);
            return outcome == ControlOutcome.Works && applied;
        }

        Vector3 Origin()
        {
            var hand = Hand();
            return (hand != null ? hand.position : _fighter.transform.position + Vector3.up * 1.5f) + _fighter.transform.forward * 0.4f;
        }

        Transform Hand() => _hand != null ? _hand : _hand = FindDeep(_fighter.Visual, "Slot_RightHand_Weapon");

        /// <summary>A loading/powering effect with its sound, on the spell in the hand (item effects) or the caster; a handle.</summary>
        int Effect(string itemEffect, string casterEffect)
        {
            var effect = itemEffect ?? casterEffect;
            if (string.IsNullOrEmpty(effect)) return 0;
            var at = itemEffect != null && Hand() != null ? Hand() : _fighter.transform;
            return G3EffectPlayer.Start(effect, at, withSound: true);
        }

        void StopCharge()
        {
            G3EffectPlayer.End(_loadFx);   // a load with screen parts (soul travel: screen sheet, white mist) ends at once
            G3EffectPlayer.End(_powerFx);
            _loadFx = _powerFx = 0;
        }

        void Sound(string effect, Transform at = null)
        {
            if (string.IsNullOrEmpty(effect)) return;
            var where = at != null ? at : _fighter.transform;
            G3EffectSounds.Play(effect, where.position + Vector3.up, where);
        }

        /// <summary>The G3 action name of a cast (the motion's action field).</summary>
        static string ActionName(CastAction action) => action.ToString();

        static Transform FindDeep(Transform t, string name)
        {
            if (t == null) return null;
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
