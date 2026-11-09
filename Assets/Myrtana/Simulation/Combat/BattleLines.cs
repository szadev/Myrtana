using System;
using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// The battle shouts of NPCs (Script_Game AI_AI_Commands: StartSayAttackReason, StartSayAttackEnd,
    /// StartSayDefeatComment; Docs/combat.md §13): shared SVM line names, chosen by the fight's reason and the target. The
    /// speaker must be humanoid (the say gate); a null line = silent. Pure.
    /// </summary>
    public static class BattleLines
    {
        /// <summary>G3 IsBig: trolls, T-rexes and dragons (species 7, 21, 52).</summary>
        public static bool IsBig(string species) => species == "Troll" || species == "Trex" || species == "Dragon";

        static string OneOf(IRandom random, string a, string b, string c)
        {
            switch (random?.Next(3) ?? 0)
            {
                case 0: return a;
                case 1: return b;
                default: return c;
            }
        }

        /// <summary>
        /// StartSayAttackReason, said when an NPC enters a fight (not already in AI mode Combat): "IGetYouStill" with a grudge;
        /// against a creature DieMonster_0x / DieBigMonster (reasons Enemy, Murder, GateGuard, Intruder, Livestock),
        /// "YouAskedForIt" (ReactToDamage); against a humanoid "ArenaAttack" (Arena, Duel), DieEnemy_0x (Enemy, Revolution,
        /// Kill), "Murder", "DirtyThief", "HandsOff", "Intruder", "YouAskedForIt" (GateGuard, ReactToWeapon),
        /// "WhatDidYouDoInThere" (LeaveZone), "StopThis" (Livestock), "YouDareHitMe" / DieEnemy_00 (ReactToDamage, by the
        /// target's weapon category), "WillYouStopFighting" (StopFight); else nothing.
        /// </summary>
        /// <param name="targetMelee">FUN_10003240(target) == 1: the target fights in melee [the category function is
        /// unnamed; 1 is read as the melee weapon category].</param>
        public static string AttackReasonLine(AttackReason reason, bool upset, bool targetHumanoid, string targetSpecies, bool targetMelee,
                                          IRandom random)
        {
            if (upset) return "IGetYouStill";
            if (!targetHumanoid)
            {
                switch (reason)
                {
                    case AttackReason.Enemy:
                    case AttackReason.Murder:
                    case AttackReason.GateGuard:
                    case AttackReason.Intruder:
                    case AttackReason.Livestock:
                        return IsBig(targetSpecies) ? "DieBigMonster" : OneOf(random, "DieMonster_00", "DieMonster_01", "DieMonster_02");
                    case AttackReason.ReactToDamage:
                        return "YouAskedForIt";
                    default:
                        return null;
                }
            }
            switch (reason)
            {
                case AttackReason.Arena:
                case AttackReason.Duel:
                    return "ArenaAttack";
                case AttackReason.Enemy:
                case AttackReason.Revolution:
                case AttackReason.Kill:
                    return OneOf(random, "DieEnemy_00", "DieEnemy_01", "DieEnemy_02");
                case AttackReason.Murder: return "Murder";
                case AttackReason.Theft: return "DirtyThief";
                case AttackReason.UseInteractObject: return "HandsOff";
                case AttackReason.Intruder: return "Intruder";
                case AttackReason.GateGuard:
                case AttackReason.ReactToWeapon:
                    return "YouAskedForIt";
                case AttackReason.LeaveZone: return "WhatDidYouDoInThere";
                case AttackReason.Livestock: return "StopThis";
                case AttackReason.ReactToDamage: return targetMelee ? "YouDareHitMe" : "DieEnemy_00";
                case AttackReason.StopFight: return "WillYouStopFighting";
                default: return null;
            }
        }

        /// <summary>
        /// StartSayAttackEnd, said by the token holder when a fight ends without a next enemy or a finishing move: a creature
        /// MonsterKilled_0x / BigMonsterKilled; a dead humanoid Victory_0x; a downed humanoid by the reason: "ThiefDown"
        /// (Theft), "NeverTouchOurStuffAgain" (UseInteractObject), "AndNowGetOutOfHere" / "NeverEnterAreaAgain" (Intruder,
        /// GateGuard, LeaveZone: by the zone's attitude to the player), "YouLearnedALesson" (Livestock), "NeverHitMeAgain"
        /// (ReactToDamage), "YouBetterShouldHaveListened" (ReactToWeapon), "ThereIsNoFightingHere" (StopFight); else nothing
        ///.
        /// </summary>
        /// <param name="zoneAngry">GetZoneAttitudeToPlayer == Angry [interp: the speaker's attitude to the player].</param>
        public static string AttackEndLine(AttackReason reason, bool targetHumanoid, string targetSpecies, VitalState targetState,
                                       bool zoneAngry, IRandom random)
        {
            if (!targetHumanoid)
                return IsBig(targetSpecies) ? "BigMonsterKilled" : OneOf(random, "MonsterKilled_00", "MonsterKilled_01", "MonsterKilled_02");
            if (targetState == VitalState.Dead) return OneOf(random, "Victory_00", "Victory_01", "Victory_02");
            if (targetState != VitalState.Down) return null;
            switch (reason)
            {
                case AttackReason.Theft: return "ThiefDown";
                case AttackReason.UseInteractObject: return "NeverTouchOurStuffAgain";
                case AttackReason.Intruder:
                case AttackReason.GateGuard:
                case AttackReason.LeaveZone:
                    return zoneAngry ? "AndNowGetOutOfHere" : "NeverEnterAreaAgain";
                case AttackReason.Livestock: return "YouLearnedALesson";
                case AttackReason.ReactToDamage: return "NeverHitMeAgain";
                case AttackReason.ReactToWeapon: return "YouBetterShouldHaveListened";
                case AttackReason.StopFight: return "ThereIsNoFightingHere";
                default: return null;
            }
        }

        /// <summary>
        /// StartSayDefeatComment: only a witness whose current or alternative target is the loser (a fight watcher:
        /// ZS_WatchFight targets both fighters) speaks, in 80 % of the cases; the loser without an attacker (or its own):
        /// "StupidMotherFucker"; a friend of the loser: "NotBad" when also a friend of the winner, else "FriendLost"; otherwise
        /// "FriendWon" when friendly or neutral to the winner, else "NotBad".
        /// </summary>
        public static string DefeatCommentLine(bool watchesLoser, bool winnerKnown, Attitude toLoser, Attitude toWinner, IRandom random)
        {
            if (!watchesLoser || (random?.Next(10) ?? 0) > 7) return null;
            if (!winnerKnown) return "StupidMotherFucker";
            if (toLoser == Attitude.Friendly) return toWinner == Attitude.Friendly ? "NotBad" : "FriendLost";
            return toWinner == Attitude.Friendly || toWinner == Attitude.Neutral ? "FriendWon" : "NotBad";
        }
    }

    /// <summary>
    /// G3's say gate for AI lines (FUN_1000bc20, called by the say helper FUN_1000baf0 that every AI SVM line of Script_Game
    /// goes through): only humanoids speak, and each line kind waits a global cool-down (shared by all NPCs) after it was
    /// last said. The clock is <c>Entity::GetPlayingTime</c>: hours + <b>whole game minutes</b> × 0.01 (HH.MM, no
    /// seconds), so a cool-down below 0.01 only blocks a line in the same game minute (5 real seconds at the clock
    /// factor 12), and the hour boundary jumps by 0.41 (x.59 → x+1.00). Kinds (the say helper's EAX) and cool-downs:
    /// 3 StartSayAttackReason/StartSayAttackEnd, 6 ZS_WatchFight/CheerFight, 8 zone, 9 ReactToWeapon, 10 ReactToDamage,
    /// 13 Flee: 0.0018; 1 OnFollowPlayer, 4 ZS_Unconscious_End/ReactToFootsteps/magic, 11 ReactToSneak: 0.006; 7
    /// ZS_Arena_Loop/StartSayDefeatComment, 14 Attack_WaitForFlee/OnReactToWeapon: 0.12; 12: 0.012; the others (0, 2:
    /// Dead/Down/Aargh, finishing move) are not gated.
    /// </summary>
    public sealed class SpeechGate
    {
        /// <summary>Kinds 3, 6, 8, 9, 10, 13: one line per game minute.</summary>
        public const double BattleCooldownHours = 0.0018;
        /// <summary>Kinds 1, 4, 11 (also below one minute step).</summary>
        public const double ShortCooldownHours = 0.006;
        /// <summary>Kinds 7, 14: 12 game minutes (fewer across an hour boundary).</summary>
        public const double LongCooldownHours = 0.12;

        readonly Dictionary<string, double> _last = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly Func<double> _gameHours;

        public SpeechGate(Func<double> gameHours) => _gameHours = gameHours ?? (() => 0);

        /// <summary>
        /// G3 <c>Entity::GetPlayingTime</c> of a continuous game time in hours: the whole hours plus the whole minutes as
        /// hundredths (HH.MM).
        /// </summary>
        public static double PlayingTime(double gameHours)
        {
            double hours = Math.Floor(gameHours);
            double minutes = Math.Floor((gameHours - hours) * 60 + 1e-6);
            if (minutes >= 60) { hours += 1; minutes = 0; }
            return hours + minutes * 0.01;
        }

        /// <summary>Whether <paramref name="kind"/> may be said now (and stamps it when so).</summary>
        public bool TrySay(string kind, double cooldownHours = BattleCooldownHours)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            double now = _gameHours();
            if (_last.TryGetValue(kind, out var last) && now >= last && PlayingTime(now) - PlayingTime(last) < cooldownHours - 1e-9)
                return false;
            _last[kind] = now;
            return true;
        }
    }

    /// <summary>
    /// Who watches which fight (ZS_WatchFight: current target = one fighter, alternative target = the other) for the defeat
    /// comment, and the say gate (Docs/combat.md §13). Fed by the reactions; not saved.
    /// </summary>
    public sealed class BattleTalk : IActorStore
    {
        /// <summary>A watcher remembers the fight this long (real seconds; G3 keeps the targets until the next task) [interp].</summary>
        public const double WatchMemorySeconds = 30;
        readonly Dictionary<ActorId, (ActorId a, ActorId b, double until)> _watching = new Dictionary<ActorId, (ActorId, ActorId, double)>();
        readonly Func<double> _now;

        public BattleTalk(Func<double> now, Func<double> gameHours)
        {
            _now = now ?? (() => 0);
            Gate = new SpeechGate(gameHours);
        }

        public SpeechGate Gate { get; }

        /// <summary>The witness watches the fight of <paramref name="a"/> and <paramref name="b"/>.</summary>
        public void Watch(ActorId witness, ActorId a, ActorId b) => _watching[witness] = (a, b, _now() + WatchMemorySeconds);

        /// <summary>The witness's current or alternative target is <paramref name="actor"/> (it watched that fight).</summary>
        public bool Watches(ActorId witness, ActorId actor)
        {
            if (actor.IsEmpty || !_watching.TryGetValue(witness, out var w)) return false;
            if (_now() > w.until)
            {
                _watching.Remove(witness);
                return false;
            }
            return w.a == actor || w.b == actor;
        }

        /// <summary>The witnesses that watch a fight of <paramref name="actor"/> now (into <paramref name="into"/>, cleared first).</summary>
        public void WatchersOf(ActorId actor, List<ActorId> into)
        {
            into.Clear();
            double now = _now();
            foreach (var kv in _watching)
                if (now <= kv.Value.until && (kv.Value.a == actor || kv.Value.b == actor)) into.Add(kv.Key);
            into.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
        }

        public void Forget(ActorId actor) => _watching.Remove(actor);

        public bool Holds(ActorId actor) => _watching.ContainsKey(actor);
    }
}
