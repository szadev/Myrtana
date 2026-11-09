using System;
using System.Collections.Generic;
using G3.Unity;
using G3.Unity.Characters;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The presentation of the NPC and world spells (Docs/combat.md §12.5; the rules are <see cref="SpellEffectServices"/>):
    /// delivers a released spell with its focus (an NPC, or for Telekinesis and Open Locks an object or item in front of the
    /// camera within 10 m), shows sleepers lying (LieKnockDown) and misted NPCs standing, the mist's and time bubble's
    /// effects, slows the fighters inside a time bubble (their motions at half speed [interp]), pulls items by telekinesis
    /// and puts the conjured flame sword into the hero's hand. One per combat presenter (<see cref="Attach"/>).
    /// </summary>
    public sealed class WorldSpellView : MonoBehaviour
    {
        const float Cm = 100f, FocusCosine = 0.97f; // object focus within ~14° of the camera's forward [interp]
        const float MistEffectSeconds = SpellEffectRules.MistEffectEndsEarlySeconds;

        static WorldSpellView s_instance;

        CombatPresenter _presenter;
        NpcViewManager _npcs;
        GameSession _bound;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly Dictionary<SpellObjectKind, int> _objectFx = new Dictionary<SpellObjectKind, int>();
        readonly HashSet<CombatFighter> _slowed = new HashSet<CombatFighter>();
        readonly List<Pull> _pulls = new List<Pull>();
        WorldItemViews _items;
        InteractiveObjectViews _objects;
        PlayerCombat _swordFor;
        float _swordUntil;

        sealed class Pull
        {
            public Transform item;
            public Vector3 to;
        }

        GameSession Session => _presenter != null ? _presenter.Session : null;

        /// <summary>Makes sure the presenter has its world spell view, bound to the presenter's session.</summary>
        public static void Attach(CombatPresenter presenter, NpcViewManager npcs)
        {
            if (presenter == null) return;
            if (!presenter.TryGetComponent<WorldSpellView>(out var view)) view = presenter.gameObject.AddComponent<WorldSpellView>();
            view._presenter = presenter;
            view._npcs = npcs;
            view.Rebind();
            s_instance = view;
        }

        void Rebind()
        {
            if (_bound == Session) return;
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            foreach (var h in _objectFx.Values) G3EffectPlayer.Stop(h);
            _objectFx.Clear();
            _slowed.Clear();
            _pulls.Clear();
            _bound = Session;
            if (_bound == null) return;
            _subscriptions.Add(_bound.Events.Subscribe<ActorSpellbound>(OnSpellbound));
            _subscriptions.Add(_bound.Events.Subscribe<SpellObjectChanged>(OnSpellObject));
        }

        void OnDestroy()
        {
            foreach (var s in _subscriptions) s.Dispose();
            if (s_instance == this) s_instance = null;
        }

        // ------------------------------------------------------------------------------------------- delivery

        /// <summary>
        /// A released spell of <see cref="SpellEffectServices.Handles"/>: its targets (the NPC focus, else for the player
        /// the object or item focus), the rules, and the looks (the spell's target effect where it acts).
        /// </summary>
        public static bool Deliver(CombatPresenter presenter, CombatFighter caster, SpellCast cast, CombatFighter target)
        {
            var session = presenter.Session;
            if (session == null || cast == null) return false;
            var spell = cast.Spell;
            var t = caster.transform;
            var targets = new SpellTargets
            {
                Target = target != null ? target.Actor : default,
                CasterPose = new WorldPose(t.position.x, t.position.y, t.position.z, t.eulerAngles.y),
                Intelligence = presenter.Snapshot(caster).Intelligence,
            };
            Transform objectView = null;
            if (SpellEffectServices.TargetsObjects(spell) && caster.Actor.IsPlayer && s_instance != null)
                objectView = s_instance.ObjectFocus(t, spell.Effect == "MagicTelekinesis", out targets.Object, out targets.WorldItem);
            bool done = session.SpellEffects.Deliver(cast, targets);
            Debug.Log($"[Myrtana] {caster.Actor} cast {spell.Id} ({spell.Effect}) at {(target != null ? target.Actor.ToString() : targets.Object ?? targets.WorldItem ?? "-")}: {(done ? "works" : "no effect")}");
            if (!done) return false;
            var at = target != null ? target.transform : objectView != null ? objectView : caster.transform;
            // Soul travel: the target effect is the ghost form's (HeroFormView keeps it for the whole travel); a second
            // fire-and-forget copy here left its screen parts on for 12 s + particle life (editor-runner-14).
            var effect = spell.Effect == "MagicSoulTravel" ? null : spell.Visuals.TargetCast;
            if (G3EffectPlayer.Has(effect)) G3EffectPlayer.Play(effect, at.position + Vector3.up, at);
            else if (spell.Effect != "MagicSoulTravel" && spell.Effect != "MagicTransform" && spell.Effect != "MagicAnimalForm")
                SpellFx.Burst(at.position + Vector3.up, SpellFx.ColorOf(spell), 0.8f);
            if (spell.Effect == "MagicTelekinesis" && targets.WorldItem != null && objectView != null) s_instance.StartPull(objectView, t);
            if (spell.Effect == "MagicSummonFlameSword" && caster.TryGetComponent<PlayerCombat>(out var hero) && s_instance != null)
            {
                s_instance._swordFor = hero; // drawn once the cast motion is over (PS_Magic_FlameSword: HoldStacks)
                s_instance._swordUntil = Time.time + 4f;
            }
            return true;
        }

        /// <summary>The player has an item (Telekinesis) or object in front of the camera within 10 m (the spell's focus).</summary>
        public static bool HasObjectFocus(Transform caster) =>
            s_instance != null && s_instance.ObjectFocus(caster, true, out _, out _) != null;

        /// <summary>
        /// The object or world item nearest to the camera's line within 10 m of the caster (PS_Magic_Loop prefers items and
        /// interactables for these spells); its view, or null.
        /// </summary>
        Transform ObjectFocus(Transform caster, bool items, out string objectId, out string worldItem)
        {
            objectId = worldItem = null;
            var camera = Camera.main;
            if (camera == null || caster == null) return null;
            if (_items == null) _items = FindAnyObjectByType<WorldItemViews>();
            if (_objects == null) _objects = FindAnyObjectByType<InteractiveObjectViews>();
            var eye = camera.transform.position;
            var forward = camera.transform.forward;
            float best = FocusCosine, range = SpellEffectRules.ObjectFocusRange / Cm;
            Transform found = null;
            if (_objects != null)
                foreach (var o in _objects.Shown)
                {
                    if (o.Value == null || !Better(o.Value.FocusPoint, ref best)) continue;
                    found = o.Value.transform;
                    objectId = o.Key;
                }
            if (items && _items != null)
                foreach (var i in _items.Shown)
                {
                    if (i.Value == null || !Better(i.Value.transform.position, ref best)) continue;
                    found = i.Value.transform;
                    worldItem = i.Key;
                    objectId = null;
                }
            return found;

            bool Better(Vector3 point, ref float bestCos)
            {
                if ((point - caster.position).sqrMagnitude > range * range) return false;
                var dir = point - eye;
                if (dir.sqrMagnitude < 0.01f) return false;
                float cos = Vector3.Dot(forward, dir.normalized);
                if (cos < bestCos) return false;
                bestCos = cos;
                return true;
            }
        }

        // ------------------------------------------------------------------------------------------- holds

        /// <summary>ZS_MagicSleep: the body lies (LieKnockDown) and the routine lets go of it; ZS_Misted: it stands still.</summary>
        void OnSpellbound(ActorSpellbound e)
        {
            if (e.Actor.IsPlayer)
            {
                Debug.Log($"[Myrtana] the hero is {(e.On ? "" : "no longer ")}{e.Hold}");
                return;
            }
            var npcView = _npcs != null && _npcs.TryGetView(e.Actor, out var v) ? v.GetComponent<NpcView>() : null;
            _presenter.TryGet(e.Actor, out var fighter);
            if (e.Hold == SpellHold.Asleep && fighter != null)
            {
                if (e.On) fighter.Animator.FallDown("LieKnockDown", false);
                else if (Session.Combat.Vitals.StateOf(e.Actor) == VitalState.Alive) fighter.GetUp();
            }
            if (npcView != null) npcView.BodyTaken = e.On || Session.SpellEffects.World.Paralysed(e.Actor);
        }

        // ------------------------------------------------------------------------------------------- spell objects

        /// <summary>The mist (eff_magic_mist_cast_01) and the time bubble (eff_magic_timebubble_target_01) where they were cast.</summary>
        void OnSpellObject(SpellObjectChanged e)
        {
            if (_objectFx.TryGetValue(e.Kind, out var old))
            {
                G3EffectPlayer.Stop(old);
                _objectFx.Remove(e.Kind);
            }
            if (!e.On) return;
            string effect = e.Kind == SpellObjectKind.Mist ? SpellEffectRules.MistEffect : SpellEffectRules.TimeBubbleEffect;
            var at = new Vector3(e.Pose.X, e.Pose.Y, e.Pose.Z);
            if (G3EffectPlayer.Has(effect)) _objectFx[e.Kind] = G3EffectPlayer.Start(effect, at, Quaternion.identity);
            else SpellFx.Burst(at + Vector3.up, e.Kind == SpellObjectKind.Mist ? new Color(0.8f, 0.8f, 0.85f) : SpellFx.Lightning,
                               (e.Kind == SpellObjectKind.Mist ? SpellEffectRules.MistRadius : SpellEffectRules.TimeBubbleRadius) / Cm);
        }

        // ------------------------------------------------------------------------------------------- frame

        void Update()
        {
            Rebind();
            var session = _bound;
            if (session == null) return;
            var world = session.SpellEffects.World;
            // ZS_Mist_Loop: the effect stops 5 s before the end.
            if (world.Mist != null && world.Mist.Remaining < MistEffectSeconds && _objectFx.TryGetValue(SpellObjectKind.Mist, out var fx))
            {
                G3EffectPlayer.Stop(fx);
                _objectFx.Remove(SpellObjectKind.Mist);
            }
            UpdateTimeBubble(world);
            UpdatePulls();
            // A sleeper whose view came back (pooled views stand up when shown) or that was hit lies down again.
            foreach (var sleeper in world.Sleepers)
                if (!sleeper.IsPlayer && _presenter.TryGet(sleeper, out var f) && !f.Animator.IsDown && !f.Animator.InReaction && !f.Animator.IsDead)
                {
                    f.Animator.FallDown("LieKnockDown", false);
                    if (_npcs != null && _npcs.TryGetView(sleeper, out var v) && v.TryGetComponent<NpcView>(out var nv)) nv.BodyTaken = true;
                }
            if (_swordFor != null && (Time.time > _swordUntil || _swordFor.TryReady(session.Items.Equipment.In(WornSlot.MeleeWeapon))))
                _swordFor = null;
        }

        /// <summary>The fighters inside the bubble move their motions at half speed; leaving it (or its end) restores them.</summary>
        void UpdateTimeBubble(WorldSpells world)
        {
            if (world.TimeBubble == null && _slowed.Count == 0) return;
            foreach (var f in _presenter.Fighters)
            {
                if (f == null || f.Frozen || f.Visual == null || !f.Visual.TryGetComponent<G3MotionPlayer>(out var motion)) continue;
                var p = f.transform.position;
                float scale = world.TimeScaleOf(f.Actor, new WorldPose(p.x, p.y, p.z, 0));
                if (scale < 1f) _slowed.Add(f);
                else if (!_slowed.Remove(f)) continue;
                motion.TimeScale = scale;
            }
            _slowed.RemoveWhere(f => f == null);
        }

        /// <summary>MagicTelekinesis: the item hovers to the caster's position + 2.5 m forward + 1.5 m up at 2.2 m/s, then drops.</summary>
        void StartPull(Transform item, Transform caster)
        {
            var to = caster.position + caster.forward * (SpellEffectRules.TelekinesisForward / Cm) + Vector3.up * (SpellEffectRules.TelekinesisUp / Cm);
            _pulls.Add(new Pull { item = item, to = to });
        }

        void UpdatePulls()
        {
            for (int i = _pulls.Count - 1; i >= 0; i--)
            {
                var pull = _pulls[i];
                if (pull.item == null)
                {
                    _pulls.RemoveAt(i);
                    continue;
                }
                pull.item.position = Vector3.MoveTowards(pull.item.position, pull.to, SpellEffectRules.TelekinesisSpeed / Cm * Time.deltaTime);
                if ((pull.item.position - pull.to).sqrMagnitude > 1e-4f) continue;
                // It falls to the ground below (G3: physics takes over).
                if (Physics.Raycast(pull.to, Vector3.down, out var hit, 5f, ~0, QueryTriggerInteraction.Ignore)) pull.item.position = hit.point;
                _pulls.RemoveAt(i);
            }
        }
    }
}
