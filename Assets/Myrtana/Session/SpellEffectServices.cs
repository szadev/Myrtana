using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Session
{
    /// <summary>Who and what a world spell is cast at (the presentation's focus and the caster's pose).</summary>
    public struct SpellTargets
    {
        /// <summary>The focused NPC (empty: none).</summary>
        public ActorId Target;
        /// <summary>The focused interactive object (Open Locks, Telekinesis on doors and levers), or null.</summary>
        public string Object;
        /// <summary>The focused world item (Telekinesis), or null.</summary>
        public string WorldItem;
        /// <summary>The caster's feet.</summary>
        public WorldPose CasterPose;
        /// <summary>The caster's INT (Word of Control, Tame Animal, Call Animals, transformation time).</summary>
        public int Intelligence;
    }

    /// <summary>
    /// The CP 1.75 spells beyond missiles, heals, area spells and summons (Docs/combat.md §12.5): the NPC and world spells
    /// (<see cref="WorldSpells"/>), the hero's forms (<see cref="HeroForms"/>) and the weapon blessing/poisoning of the
    /// inventory (<see cref="WeaponTreatments"/>), with the dispatch of a released spell by its G3 FuncOnTargetHit.
    /// </summary>
    public sealed class SpellEffectServices : IDisposable
    {
        const string TransformEffect = "MagicTransform", AnimalFormEffect = "MagicAnimalForm";

        readonly GameSession _session;
        Dictionary<string, NpcDef> _byTemplate, _bySpecies;

        readonly IGameLog _log;

        SpellEffectServices(GameSession session, WorldSpells world, HeroForms hero, WeaponTreatments weapons, IGameLog log)
        {
            _session = session;
            _log = log;
            World = world;
            Hero = hero;
            Weapons = weapons;
        }

        public WorldSpells World { get; }
        public HeroForms Hero { get; }
        public WeaponTreatments Weapons { get; }

        public static SpellEffectServices Create(GameSession s, IGameLog log)
        {
            var player = ActorId.Player;
            var world = new WorldSpells(s.Actors, s.Npcs, s.NpcCombat, s.Combat.Vitals, s.Party.Members, s.Random, s.Events, log)
            {
                Positions = id => s.Party.Positions.TryGetPose(id.Name, out var pose) ? pose : (WorldPose?)null,
                Attitudes = (self, other) => s.Crime.Attitudes.Of(self, other),
                Ledger = s.Crime.Ledger,
                IsGateGuard = id => s.Crime.Crime.GateGuards != null && s.Crime.Crime.GateGuards.IsGateGuard(id),
                DismissSummons = caster => s.Summoning.Dismiss(caster),
            };
            var hero = new HeroForms(s.Hero, s.Progression, s.Combat.Vitals, s.Clock, s.NpcCombat, s.Settings.Rules, s.Events, log);
            var weapons = new WeaponTreatments(s.Content.Database, s.Items.Inventories.Of(player), s.Hero.Knows,
                                               () => s.Combat.Vitals.Mana(player), mana => s.Combat.Vitals.AddMana(player, -mana), s.Events);
            s.NpcCombat.Paralysed = world.Paralysed;
            s.Crime.World.Paralysed = world.Paralysed;
            if (s.Crime.Attitudes.Facts is SessionAttitudeFacts facts) facts.PlayerForm = () => hero.PerceivedSpecies;
            s.AddSaveBlocker(hero);
            return new SpellEffectServices(s, world, hero, weapons, log);
        }

        public void Tick(float realSeconds)
        {
            World.Tick(realSeconds);
            Hero.Tick(realSeconds);
        }

        // ------------------------------------------------------------------------------------------- templates

        /// <summary>
        /// The world's template NPC of a creature template name ("Wolf" → the first "Wolf#…"), else the first NPC of that
        /// species (Waran → Varan as G3's species is named).
        /// </summary>
        public NpcDef TemplateFor(string template)
        {
            if (string.IsNullOrEmpty(template)) return null;
            if (_byTemplate == null)
            {
                _byTemplate = new Dictionary<string, NpcDef>(StringComparer.Ordinal);
                _bySpecies = new Dictionary<string, NpcDef>(StringComparer.Ordinal);
                foreach (var def in _session.Content.Database.All<NpcDef>().OrderBy(d => d.Id.Name, StringComparer.Ordinal))
                {
                    if (def.Combat == null || string.IsNullOrEmpty(def.Visual)) continue;
                    string name = def.Id.Name;
                    int hash = name.IndexOf('#');
                    string key = hash >= 0 ? name.Substring(0, hash) : name;
                    if (!_byTemplate.ContainsKey(key)) _byTemplate[key] = def;
                    if (def.Combat.Species != null && !_bySpecies.ContainsKey(def.Combat.Species)) _bySpecies[def.Combat.Species] = def;
                }
            }
            if (_byTemplate.TryGetValue(template, out var byName)) return byName;
            string species = template.Replace("Waran", "Varan");
            return _bySpecies.TryGetValue(species, out var bySpecies) ? bySpecies : null;
        }

        /// <summary>
        /// The transformed hero's combat snapshot: the form's NPC values (G3 GetStrength … for IsTransformed: level, attributes,
        /// protection from the form's levels) with the hero's own hit points; null when he is himself.
        /// </summary>
        public Combatant TransformedPlayer()
        {
            var form = Hero.FormCombat;
            if (form == null) return null;
            var c = _session.Combat.NpcCombatant(ActorId.Player, form, _session.Hero.Level);
            c.IsPlayer = true;
            c.IsTransformed = true;
            return c;
        }

        // ------------------------------------------------------------------------------------------- items used directly

        /// <summary>
        /// A transformation potion (an item whose spell is MagicTransform and that is drunk, not held like a spell): used from
        /// the inventory or a quick slot it transforms the hero at once (G3 PS_MagicPotion). Druid stones (use type Cast) are
        /// readied in the spell hand like spells and last until cancelled.
        /// </summary>
        public bool IsTransformItem(string item) =>
            _session.Magic.TryGetSpell(item, out var spell) && spell.Effect == TransformEffect &&
            !(_session.Content.Database.TryGet(item, out ItemDef def) && def.Kind == "Cast");

        /// <summary>
        /// fix-193: a transformation item that is drunk (use type Potion): the hero plays the potion use motion first and the
        /// transformation follows at its end, like any potion (G3 PS_QuickUse → _AI_UseInventoryItem → PS_MagicPotion).
        /// </summary>
        public bool DrinksToTransform(string item) =>
            IsTransformItem(item) && _session.Content.Database.TryGet(item, out ItemDef def) && def.Kind == "Potion";

        /// <summary>fix-193: whether <see cref="UseTransformItem"/> would transform the hero now (checked before the drink motion).</summary>
        public bool CanUseTransformItem(string item) =>
            IsTransformItem(item) && !Hero.Restricted && _session.Magic.TryGetSpell(item, out var spell) && Hero.CanTransform(TemplateFor(spell.Spawn));

        /// <summary>Uses a transformation item; false when it is not one or the hero cannot transform now.</summary>
        public bool UseTransformItem(string item)
        {
            if (!IsTransformItem(item) || Hero.Restricted) return false;
            _session.Magic.TryGetSpell(item, out var spell);
            var form = TemplateFor(spell.Spawn);
            bool artefact = IsArtefact(item);
            if (!Hero.Transform(form, _session.Hero.Stats.Value("INT"), artefact)) return false;
            if (spell.UsesUp(item)) _session.Items.Inventories.Consume(ActorId.Player, item, 1);
            _session.Events.Publish(new SpellCasted(ActorId.Player, spell.Id, item, false));
            return true;
        }

        bool IsArtefact(string item) => _session.Content.Database.TryGet(item, out ItemDef def) && def.Category == ItemCategory.Artefact;

        // ------------------------------------------------------------------------------------------- dispatch

        /// <summary>Whether <see cref="Deliver"/> handles this spell (by its G3 FuncOnTargetHit).</summary>
        public static bool Handles(SpellDef spell)
        {
            switch (spell?.Effect)
            {
                case "MagicSleep": case "MagicHypnosis": case "MagicWordOfControl": case "MagicTameAnimal": case "MagicCharm":
                case "MagicBloodlust": case "MagicCallAnimals": case "MagicSummonMist": case "MagicTimeBubble":
                case "MagicSummonFlameSword": case "MagicTelekinesis": case "MagicOpenLocks": case "MagicDuskToDawn":
                case "MagicSoulTravel": case TransformEffect: case AnimalFormEffect: case "MagicPoison": case "MagicDisease":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Spells whose focus is an object or a world item rather than an NPC (PS_Magic_Loop prefers them).</summary>
        public static bool TargetsObjects(SpellDef spell) => spell?.Effect == "MagicTelekinesis" || spell?.Effect == "MagicOpenLocks";

        /// <summary>
        /// The effect of a released spell (G3 MagicInstant → FuncOnTargetHit). Lines ("DoesntWork", "Impossible" …) are said
        /// through <see cref="CombatShout"/>. False when the spell is not one of these or did nothing.
        /// </summary>
        public bool Deliver(SpellCast cast, in SpellTargets t)
        {
            if (cast == null || !Handles(cast.Spell)) return false;
            var spell = cast.Spell;
            var caster = cast.Caster;
            bool perk = caster.IsPlayer && _session.Hero.Knows(SpellCasting.DeityPerk(spell.Deity) ?? "");
            switch (spell.Effect)
            {
                case "MagicSleep":
                    return World.Sleep(t.Target);
                case "MagicHypnosis":
                    return World.Hypnosis(caster).Count > 0;
                case "MagicWordOfControl":
                    return NeedsTarget(caster, t) && World.WordOfControl(caster, t.Target, t.Intelligence) == ControlOutcome.Works;
                case "MagicTameAnimal":
                    return NeedsTarget(caster, t) && World.TameAnimal(caster, t.Target, t.Intelligence) == ControlOutcome.Works;
                case "MagicCharm":
                    return NeedsTarget(caster, t) && World.Charm(caster, t.Target) == ControlOutcome.Works;
                case "MagicBloodlust":
                    return NeedsTarget(caster, t) && !World.Bloodlust(caster, t.Target).IsEmpty;
                case "MagicCallAnimals":
                    return NeedsTarget(caster, t) && World.CallAnimals(caster, t.Target, t.Intelligence).Count > 0;
                case "MagicSummonMist":
                    World.SummonMist(caster, t.CasterPose, perk);
                    return true;
                case "MagicTimeBubble":
                    World.StartTimeBubble(caster, t.CasterPose, perk);
                    return true;
                case "MagicSummonFlameSword":
                    return FlameSword(caster);
                case "MagicOpenLocks":
                    return OpenLocks(caster, t.Object);
                case "MagicTelekinesis":
                    return Telekinesis(caster, t);
                case "MagicDuskToDawn":
                    return caster.IsPlayer && Hero.StartDuskToDawn();
                case "MagicSoulTravel":
                    return caster.IsPlayer && Hero.SoulTravel();
                case TransformEffect:
                    return caster.IsPlayer && Hero.Transform(TemplateFor(spell.Spawn), t.Intelligence, IsArtefact(cast.Item));
                case AnimalFormEffect:
                    return caster.IsPlayer && AnimalForm(t);
                case "MagicPoison":
                case "MagicDisease":
                    // MagicPoison / MagicDisease: the status (when the target can catch it), then the target attacks the caster.
                    _session.Magic.ApplyInstant(cast, t.Target);
                    if (!t.Target.IsEmpty && !t.Target.IsPlayer) _session.NpcCombat.Engage(t.Target, caster, AttackReason.ReactToDamage);
                    return !t.Target.IsEmpty;
                default:
                    return false;
            }
        }

        bool NeedsTarget(ActorId caster, in SpellTargets t)
        {
            if (!t.Target.IsEmpty && !t.Target.IsPlayer) return true;
            Say(caster, "DoesntWork");
            return false;
        }

        /// <summary>
        /// MagicAnimalForm: the focused animal's template (the pre-check list; Meatbug passes it but CanTransformInto
        /// refuses: CAST_FAIL); a target outside the list → "DoesntWork".
        /// </summary>
        bool AnimalForm(in SpellTargets t)
        {
            if (t.Target.IsEmpty || !_session.Actors.TryGet(t.Target, out var state))
            {
                Say(ActorId.Player, "DoesntWork");
                return false;
            }
            var def = state.Definition;
            if (!TransformRules.AnimalFormTargetAllowed(CombatWiring.CombatDefOf(state)?.Species))
            {
                Say(ActorId.Player, "DoesntWork");
                return false;
            }
            string name = def.Id.Name;
            int hash = name.IndexOf('#');
            var form = TemplateFor(hash >= 0 ? name.Substring(0, hash) : name) ?? def;
            return Hero.Transform(form, t.Intelligence, artefact: false);
        }

        /// <summary>
        /// PS_Magic_FlameSword: the conjured sword It_1H_MagicFlameSword_01 (Burning) goes into the hero's hand (the item
        /// importer takes it in although it has no category, par-misc). An old content pack without it falls back to
        /// It_1H_FlameSword_01.
        /// </summary>
        public bool FlameSword(ActorId caster)
        {
            if (!caster.IsPlayer) return false;
            var db = _session.Content.Database;
            string item = db.TryGet(SpellEffectRules.FlameSwordItem, out ItemDef _) ? SpellEffectRules.FlameSwordItem : "It_1H_FlameSword_01";
            if (!db.TryGet(item, out ItemDef _)) return false;
            var inventory = _session.Items.Inventories.Of(caster);
            if (!inventory.Has(item)) inventory.Add(item, 1, SpellEffectRules.FlameSwordQuality);
            var result = _session.Items.Equipment.EquipConjured(item); // the spell replaces the sword's STR/skill requirements
            if (result != EquipResult.Done) _log?.Info($"Flame sword {item}: not put on ({result}).");
            else _log?.Info($"Flame sword {item}: in the melee slot.");
            return true;
        }

        /// <summary>MagicOpenLocks on the focused object; the refusals are said.</summary>
        public bool OpenLocks(ActorId caster, string objectId)
        {
            var objects = _session.Interaction?.Objects;
            if (objects == null || string.IsNullOrEmpty(objectId))
            {
                Say(caster, "DoesntWork");
                return false;
            }
            var outcome = objects.OpenByMagic(objectId);
            if (outcome == LockSpellOutcome.DoesntWork) Say(caster, "DoesntWork");
            else if (outcome == LockSpellOutcome.Impossible) Say(caster, "Impossible");
            return outcome == LockSpellOutcome.Opens;
        }

        /// <summary>
        /// MagicTelekinesis: a door or lever is toggled; an item lying in the world is pulled to the caster (the presentation
        /// moves it) and, when it belongs to someone, NPCs perceive a theft (SendPerceptionTakeItem).
        /// </summary>
        public bool Telekinesis(ActorId caster, in SpellTargets t)
        {
            var objects = _session.Interaction?.Objects;
            if (!string.IsNullOrEmpty(t.Object) && objects != null && objects.TryGet(t.Object, out var obj))
            {
                bool moved = obj.Kind == InteractiveKind.Switch ? objects.UseSwitch(t.Object) : objects.ToggleDoor(t.Object);
                if (!moved) Say(caster, "DoesntWork");
                return moved;
            }
            if (string.IsNullOrEmpty(t.WorldItem) || !_session.Content.Database.TryGet(t.WorldItem, out WorldItemDef item) ||
                !_session.Items.World.IsPresent(t.WorldItem))
            {
                Say(caster, "DoesntWork");
                return false;
            }
            if (caster.IsPlayer && !item.Owner.IsEmpty)
            {
                var p = item.Position;
                var where = p != null && p.Length >= 3 ? new WorldPose(p[0], p[1], p[2], 0) : t.CasterPose;
                _session.Crime.PlayerTookItem(item.Item, where);
            }
            return true;
        }

        void Say(ActorId speaker, string line) => _session.Events.Publish(new CombatShout(speaker, line));

        public void Dispose()
        {
            World.Dispose();
            Hero.Dispose();
        }
    }
}
