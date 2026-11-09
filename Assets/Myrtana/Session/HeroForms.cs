using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>The hero's body: his own, an animal's (transformation) or a ghost's (soul travel).</summary>
    public enum HeroFormKind { Hero, Animal, Ghost }

    /// <summary>
    /// The hero changed body (Docs/combat.md §12.5): <see cref="Kind"/> is the new one; for an animal its template NPC
    /// (visual, species). The presentation swaps the model, its motions and attacks, or shows the ghost.
    /// </summary>
    public readonly struct HeroFormChanged : IGameEvent
    {
        public HeroFormChanged(HeroFormKind kind, HeroFormKind from, NpcDef form, float seconds)
        {
            Kind = kind;
            From = from;
            Form = form;
            Seconds = seconds;
        }

        public HeroFormKind Kind { get; }
        public HeroFormKind From { get; }
        /// <summary>The animal's template NPC (null for the hero and the ghost).</summary>
        public NpcDef Form { get; }
        /// <summary>How long the form lasts (−1: until cancelled).</summary>
        public float Seconds { get; }
    }

    /// <summary>Dusk to Dawn started (<see cref="Running"/>) or the morning came.</summary>
    public readonly struct DuskToDawnChanged : IGameEvent
    {
        public DuskToDawnChanged(bool running) => Running = running;
        public bool Running { get; }
    }

    /// <summary>
    /// The spells that change the hero himself (Docs/combat.md §12.5; G3 CP 1.75 MagicTransform, MagicAnimalForm,
    /// TransformBack, MagicSoulTravel, ReturnToBody, PS_DuskToDawn). Pure.
    /// <list type="bullet">
    /// <item>Transformation (potions Spl_Transform_*, druid stones, Animal Form at a focused animal): the hero becomes the
    /// form for <see cref="TransformRules.DurationSeconds"/> (druid stones, artefacts: until cancelled). He keeps his
    /// actor, hit points, mana and stamina (G3 GetHitPointsMax reads the player memory) but fights with the form's NPC
    /// values (level, attributes, protection: <see cref="Combatant.IsTransformed"/>). NPCs see the form's species: no crimes,
    /// fame or grudges count (GetAttitudeToPlayer returns nothing for a transformed player). No inventory, pages, talk,
    /// weapons or spells meanwhile. Experience is kept; the level up waits for the return (at most one, AB +5 HP).</item>
    /// <item>Back to the hero: the confirm key, the timer (not when dead), entering water. CP: no HP refill.</item>
    /// <item>Soul travel: the hero's soul walks as a ghost, unlimited, unseen and unhurt; the body stays where it was.
    /// Back with the confirm key. Fights against the hero end [interp: his body is not attacked].</item>
    /// <item>Dusk to Dawn (cast from 20:00 to 06:00): the clock races to 08:00 while the hero stands still, then HP, SP and MP
    /// are full.</item>
    /// </list>
    /// </summary>
    public sealed class HeroForms : IDisposable, ISaveBlocker
    {
        readonly Hero _hero;
        readonly ProgressionService _progression;
        readonly ActorVitals _vitals;
        readonly IGameClock _clock;
        readonly NpcCombatSystem _combat;
        readonly RuleOptions _options;
        readonly IEventBus _events;
        readonly IGameLog _log;
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        float _clockFactor;

        public HeroForms(Hero hero, ProgressionService progression, ActorVitals vitals, IGameClock clock, NpcCombatSystem combat,
                         RuleOptions options, IEventBus events, IGameLog log)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _progression = progression ?? throw new ArgumentNullException(nameof(progression));
            _vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _combat = combat;
            _options = options ?? RuleOptions.Default;
            _events = events;
            _log = log;
            if (events != null) _subscriptions.Add(events.Subscribe<PlayerDefeated>(OnPlayerDefeated));
        }

        public HeroFormKind Kind { get; private set; }
        /// <summary>The animal form's template NPC (null unless transformed).</summary>
        public NpcDef Form { get; private set; }
        /// <summary>The form's NPC combat values (null unless transformed).</summary>
        public NpcCombatDef FormCombat => Form != null ? CombatWiring.CombatDefOf(Form) : null;
        /// <summary>Seconds left in the form (−1: until cancelled).</summary>
        public float Remaining { get; private set; }
        public bool Transformed => Kind == HeroFormKind.Animal;
        public bool IsGhost => Kind == HeroFormKind.Ghost;
        public bool DuskToDawn { get; private set; }
        /// <summary>No inventory, pages, talk, pickups, weapons or quick slots (transformed, ghost, Dusk to Dawn).</summary>
        public bool Restricted => Kind != HeroFormKind.Hero || DuskToDawn;
        /// <summary>The species NPCs see in the hero (null: a human).</summary>
        public string PerceivedSpecies => Transformed ? FormCombat?.Species : null;

        // ------------------------------------------------------------------------------------------- transformation

        /// <summary>
        /// MagicTransform / MagicAnimalForm: the hero becomes <paramref name="form"/> (an animal, not a pig: CanTransformInto).
        /// <paramref name="artefact"/>: a druid stone (item category Artefact) lasts until cancelled. False when the form is
        /// refused (CAST_FAIL) or the hero is not himself now.
        /// </summary>
        /// <summary>fix-193: whether <see cref="Transform"/> into <paramref name="form"/> would succeed now (checked before the drink).</summary>
        public bool CanTransform(NpcDef form)
        {
            var def = form != null ? CombatWiring.CombatDefOf(form) : null;
            return Kind == HeroFormKind.Hero && def != null && TransformRules.CanTransformInto(def.Species) && _vitals.StateOf(ActorId.Player) == VitalState.Alive;
        }

        public bool Transform(NpcDef form, int intelligence, bool artefact)
        {
            var def = form != null ? CombatWiring.CombatDefOf(form) : null;
            if (!CanTransform(form))
            {
                _log?.Info($"Transformation into {form?.Id.Name ?? "nothing"} refused (CAST_FAIL).");
                return false;
            }
            Form = form;
            Remaining = TransformRules.DurationSeconds(intelligence, def.Species, isPlayer: true, _hero.Knows, artefact);
            Kind = HeroFormKind.Animal;
            _progression.HoldLevelUps = true;
            _events?.Publish(new HeroFormChanged(HeroFormKind.Animal, HeroFormKind.Hero, form, Remaining));
            return true;
        }

        /// <summary>
        /// TransformBack (CP): the hero is himself again where the form stood, with the HP he has (no refill); experience
        /// gathered as the animal gives at most one level (+10 LP, +1 with Perk_Learn; AB also +5 max HP and +5 HP).
        /// </summary>
        public bool TransformBack()
        {
            if (Kind != HeroFormKind.Animal) return false;
            var form = Form;
            Kind = HeroFormKind.Hero;
            Form = null;
            Remaining = 0;
            _progression.HoldLevelUps = false;
            if (_progression.GainOneLevelIfDue())
            {
                var (max, hp) = TransformRules.LevelUpOnReturnBonus(_options);
                if (max != 0) _hero.Stats.Apply(ActorVitals.HitPointsId, ModifierOp.AddToMax, max);
                if (hp != 0) _vitals.AddHitPoints(ActorId.Player, hp);
            }
            _events?.Publish(new HeroFormChanged(HeroFormKind.Hero, HeroFormKind.Animal, form, 0));
            return true;
        }

        // ------------------------------------------------------------------------------------------- soul travel

        /// <summary>MagicSoulTravel: the soul leaves the body as a ghost (unlimited); fights against the hero end.</summary>
        public bool SoulTravel()
        {
            if (Kind != HeroFormKind.Hero || _vitals.StateOf(ActorId.Player) != VitalState.Alive) return false;
            Kind = HeroFormKind.Ghost;
            Remaining = -1;
            if (_combat != null)
                foreach (var brain in _combat.Brains)
                    if (brain.IsFighting && brain.State.Target.IsPlayer) brain.Disengage();
            _events?.Publish(new HeroFormChanged(HeroFormKind.Ghost, HeroFormKind.Hero, null, -1));
            return true;
        }

        /// <summary>ReturnToBody: the soul is back in the body (eff_magic_soultravel_end_01).</summary>
        public bool ReturnToBody()
        {
            if (Kind != HeroFormKind.Ghost) return false;
            Kind = HeroFormKind.Hero;
            Remaining = 0;
            _events?.Publish(new HeroFormChanged(HeroFormKind.Hero, HeroFormKind.Ghost, null, 0));
            return true;
        }

        /// <summary>OnPlayerConfirm: a ghost returns to its body, an animal turns back.</summary>
        public bool Confirm() => IsGhost ? ReturnToBody() : TransformBack();

        // ------------------------------------------------------------------------------------------- dusk to dawn

        /// <summary>
        /// PS_DuskToDawn: the clock races (3840 × the engine's 0.3 = 1152 game seconds per real second [interp: the engine's
        /// slow motion is folded into the clock]) until 08:00.
        /// </summary>
        public bool StartDuskToDawn()
        {
            if (DuskToDawn || Kind != HeroFormKind.Hero) return false;
            DuskToDawn = true;
            _clockFactor = _clock.TimeScale;
            _clock.TimeScale = SpellEffectRules.DuskToDawnClockFactor * SpellEffectRules.DuskToDawnEngineTimeScale;
            _events?.Publish(new DuskToDawnChanged(true));
            return true;
        }

        void EndDuskToDawn()
        {
            DuskToDawn = false;
            _clock.TimeScale = _clockFactor > 0 ? _clockFactor : 12f;
            _vitals.RestoreFully(ActorId.Player);
            _events?.Publish(new DuskToDawnChanged(false));
        }

        // ------------------------------------------------------------------------------------------- tick

        public void Tick(float realSeconds)
        {
            if (DuskToDawn && SpellEffectRules.DuskToDawnOver(_clock.HourOfDay)) EndDuskToDawn();
            if (Kind != HeroFormKind.Animal || Remaining < 0 || realSeconds <= 0) return;
            Remaining = Math.Max(0, Remaining - realSeconds);
            if (Remaining <= 0 && _vitals.StateOf(ActorId.Player) != VitalState.Dead) TransformBack();
        }

        /// <summary>The hero died in a form: the presentation shows the hero's own body again for the game over.</summary>
        void OnPlayerDefeated(PlayerDefeated e)
        {
            if (!e.Killed) return;
            if (Kind == HeroFormKind.Animal) TransformBack();
            else if (Kind == HeroFormKind.Ghost) ReturnToBody();
            if (DuskToDawn)
            {
                DuskToDawn = false;
                _clock.TimeScale = _clockFactor > 0 ? _clockFactor : 12f;
                _events?.Publish(new DuskToDawnChanged(false));
            }
        }

        /// <summary>Why the game cannot be saved now (a form or Dusk to Dawn is running), or null.</summary>
        public string SaveBlockReason => Restricted ? "spell" : null;

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
