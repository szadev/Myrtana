using System;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Combat for the session: resolves reported hits, applies them to <see cref="ActorVitals"/> and publishes the
    /// results. Also builds the rule snapshot of an NPC. The simulation's AI and the Unity presentation call into it;
    /// it calls nothing back.
    /// </summary>
    public sealed class CombatService
    {
        readonly RuleOptions _options;

        public CombatService(CombatRules rules, RuleOptions options, IRandom random, ActorVitals vitals)
        {
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
            Resolver = new CombatResolver(rules, options, random, Port);
        }

        public CombatRules Rules { get; }
        public CombatResolver Resolver { get; }
        /// <summary>The port's live combat options (menu rows), shared by the resolver and the NPC combat AI.</summary>
        public CombatPortOptions Port { get; } = new CombatPortOptions();
        public ActorVitals Vitals { get; }
        /// <summary>The player's swing counter (CP), read by the NPC combat AI.</summary>
        public AttackPressure PlayerPressure { get; } = new AttackPressure();

        /// <summary>Resolves a hit and applies it. <paramref name="byPlayerParty"/>: the attacker is the player or a companion.</summary>
        public HitOutcome Strike(HitContext hit, bool byPlayerParty)
        {
            var outcome = Resolver.Resolve(hit);
            Vitals.ApplyHit(hit, outcome, byPlayerParty);
            Status?.OnHit(hit.Victim.Id, outcome);
            return outcome;
        }

        /// <summary>Burning and freezing from hits (set by the magic wiring; null = hits leave no conditions).</summary>
        public StatusEffects Status { get; set; }

        /// <summary>
        /// The blow of a finishing move (OnAI_FinishingAttack, 1.2 s into the move, <see cref="FinishingStrike"/>): kills a
        /// knocked-out actor that can be killed and publishes it as a killing hit of <paramref name="attacker"/>. Down
        /// actors take no damage from ordinary hits, so the finishing move does not go through <see cref="Strike"/>.
        /// </summary>
        public bool Finish(ActorId attacker, Combatant victim, int playerLevel, bool byPlayerParty)
        {
            if (!Vitals.IsDown(victim.Id) || !Resolver.CanBeKilled(victim, playerLevel)) return false;
            Vitals.Finish(attacker, victim.Id, byPlayerParty);
            return true;
        }

        /// <summary>
        /// fix-241: a ranged hit (arrow, bolt, spell projectile) on a downed actor. With the port option "Arrows finish downed
        /// foes" and <see cref="CombatResolver.FinishesDowned"/> it kills the victim exactly as the finishing move does
        /// (<see cref="ActorVitals.Finish"/>: the attacker's killing hit, XP, crime, perception, enclave alarm, quest kills);
        /// otherwise nothing happens (G3: Down victims take no damage). Returns whether it killed.
        /// </summary>
        public bool FinishByRanged(HitContext hit, bool byPlayerParty)
        {
            if (hit?.Victim == null || !Vitals.IsDown(hit.Victim.Id) || !Resolver.FinishesDowned(hit, byPlayerParty)) return false;
            Vitals.Finish(hit.Attacker.Id, hit.Victim.Id, byPlayerParty);
            return true;
        }

        /// <summary>A move starts: pays its stamina and returns the move that actually starts (whirl fallback).</summary>
        public CombatAction StartMove(ActorId actor, CombatAction action, bool godMode = false)
        {
            int stamina = Vitals.Stamina(actor);
            int before = stamina;
            var started = StaminaRules.PayForMove(ref stamina, action, Rules, _options, godMode);
            Vitals.AddStamina(actor, stamina - before);
            if (actor.IsPlayer) PlayerPressure.OnAttackStarted(started);
            return started;
        }

        /// <summary>The rule snapshot of an NPC from its content facts and current vitals (equipment and stance are set by the caller).</summary>
        public Combatant NpcCombatant(ActorId id, NpcCombatDef def, int playerLevel)
        {
            int current = CombatStats.NpcCurrentLevel(def.Level, def.LevelMax, playerLevel, _options.Difficulty);
            int attribute = CombatStats.NpcAttribute(current);
            return new Combatant
            {
                Id = id,
                Species = def.Species,
                Humanoid = def.Humanoid,
                Evil = def.Evil,
                Animal = def.Animal,
                GameAnimal = def.GameAnimal,
                Boss = def.Boss,
                Immortal = def.Immortal,
                FriendType = def.FriendType,
                Mage = def.Mage,
                Level = CombatStats.NpcLevel(def.Level, _options.Difficulty),
                Strength = attribute,
                Dexterity = attribute,
                Intelligence = attribute,
                Protection = DamageTypeValues.All(CombatStats.NpcProtection(CombatStats.NpcLevel(def.LevelMax, _options.Difficulty))),
                HitPoints = Vitals.HitPoints(id),
                Stamina = Vitals.Stamina(id),
                Mana = Vitals.Mana(id),
                State = Vitals.StateOf(id),
            };
        }

        /// <summary>The rule snapshot of the player from the hero's attributes and perks (equipment and stance by the caller).</summary>
        public Combatant PlayerCombatant(Hero hero)
        {
            var stats = hero.Stats;
            var protection = new DamageTypeValues();
            foreach (DamageType type in Enum.GetValues(typeof(DamageType)))
                if (type != DamageType.None)
                    protection[type] = CombatStats.PlayerProtection(stats.Value(ProtectionAttribute(type)), type, _options, hero.Knows);
            var perks = new System.Collections.Generic.HashSet<string>(hero.ActiveSkills, StringComparer.Ordinal);
            return new Combatant
            {
                Id = ActorId.Player,
                IsPlayer = true,
                Level = hero.Level,
                Strength = stats.Value("STR"),
                Dexterity = stats.Value("DEX"),
                Intelligence = stats.Value("INT"),
                Protection = protection,
                Perks = perks,
                // the dev god: hits still land and get their survivor reactions (as the old 99999-HP god did), but the
                // resolver never sees a lethal hit and ActorVitals takes no HP (fix-132)
                HitPoints = Vitals.IsInvulnerable(ActorId.Player) ? ActorVitals.InvulnerableHitPoints : Vitals.HitPoints(ActorId.Player),
                Stamina = Vitals.Stamina(ActorId.Player),
                Mana = Vitals.Mana(ActorId.Player),
                State = Vitals.StateOf(ActorId.Player),
            };
        }

        /// <summary>The attribute that stores a protection ("PROT_BLADE" …).</summary>
        public static string ProtectionAttribute(DamageType type) => "PROT_" + type.ToString().ToUpperInvariant();
    }
}
