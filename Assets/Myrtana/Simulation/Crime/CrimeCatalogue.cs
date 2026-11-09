using System;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// What counts as a crime, and against whom (Docs/perception-crime.md §3): the crime an attack reason stands for,
    /// ownership of items and objects, livestock, knock-outs and murders. Pure decisions over profiles and rules.
    /// </summary>
    public sealed class CrimeCatalogue
    {
        readonly CrimeRules _rules;
        readonly AttitudeService _attitudes;

        public CrimeCatalogue(CrimeRules rules, AttitudeService attitudes)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _attitudes = attitudes ?? throw new ArgumentNullException(nameof(attitudes));
        }

        public CrimeRules Rules => _rules;

        /// <summary>
        /// The crime an NPC remembers when it starts to fight the player for <paramref name="reason"/> (G3 AssessTarget):
        /// theft and using an owned object are Theft, a murder is Murder, everything else none. Livestock is never
        /// stored this way <b></b>.
        /// </summary>
        public static CrimeType CrimeOf(AttackReason reason)
        {
            switch (reason)
            {
                case AttackReason.Theft:
                case AttackReason.UseInteractObject:
                    return CrimeType.Theft;
                case AttackReason.Murder:
                    return CrimeType.Murder;
                default:
                    return CrimeType.None;
            }
        }

        /// <summary>Taking <paramref name="item"/> is never theft (plants, arrows, dropped items, a chest whose key the player carries).</summary>
        public bool IsFree(string item, string useType = null, bool dropped = false, bool lockedChestWithOwnKey = false) =>
            dropped || lockedChestWithOwnKey || _rules.Theft.IsFree(item, useType);

        /// <summary>
        /// G3 IsBotheredByPlayerTheftOf: a chest with a lock whose key item the player carries (PSLock key, HasItems 1) is
        /// free to open and loot, locked or not. <paramref name="playerHas"/> tests the player's inventory.
        /// </summary>
        public static bool ChestKeyCarried(LockDef chestLock, Func<string, bool> playerHas) =>
            chestLock != null && !string.IsNullOrEmpty(chestLock.Key) && playerHas != null && playerHas(chestLock.Key);

        /// <summary>Everything in <paramref name="zone"/> is free once its story flag is set (Tjalf_Room, <see cref="TheftRules.FreedZones"/>).</summary>
        public bool ZoneFreed(in OwnedZone zone, Func<string, bool> flagSet) => _rules.Theft.IsFreedZone(zone.Name, flagSet);

        /// <summary>
        /// Whether <paramref name="witness"/> minds the player taking <paramref name="item"/> (or using an object when
        /// <paramref name="interactObject"/>) in <paramref name="zone"/> (G3 IsBotheredByPlayerTheftOf). Friend-type NPCs
        /// and the player's party never mind, nor does anybody outside every AI zone; others only for zones of owners they are politically friendly to. A
        /// secured zone always counts; an unsecured one only near the witness's workplace, and only for objects and
        /// workplace items.
        /// </summary>
        /// <param name="distanceToWorkplace">Centimetres from the item to the witness's routine destination.</param>
        public bool Bothers(in AttitudeProfile witness, string item, bool interactObject, in OwnedZone zone, float distanceToWorkplace)
        {
            // fix-65: G3 IsBotheredByPlayerTheftOf first needs the thing to lie in an AI zone (PSAIZone::IsValid); outside
            // every zone (a chest in the wilderness) nobody minds
            if (zone.Id == null) return false;
            if (witness.FriendType || witness.InPlayerParty || witness.IsPlayer) return false;
            var carers = _rules.Theft.CarersOf(item);
            if (carers != null && !carers.Contains(witness.Alignment)) return false;
            if (zone.IsOwned && !FriendlyToOwner(witness, zone)) return false;
            if (zone.Security != ZoneSecurity.None) return true;
            if (distanceToWorkplace > _rules.Theft.WorkplaceRange) return false;
            return interactObject || (item != null && _rules.Theft.WorkplaceItems.Contains(item));
        }

        bool FriendlyToOwner(in AttitudeProfile witness, in OwnedZone zone) =>
            (witness.Enclave != null && witness.Enclave == zone.Owner) ||
            _attitudes.PoliticallyFriendly(witness.Alignment, zone.OwnerAlignment);

        /// <summary>The enclave a theft in <paramref name="zone"/> is counted against (seen or not); null = none.</summary>
        public string TheftCountedAgainst(in OwnedZone zone) => zone.Security != ZoneSecurity.None ? zone.Owner : null;

        /// <summary>AB: the master thief's thefts are never counted.</summary>
        public bool TheftHidden(RuleOptions options, Func<string, bool> playerKnows) =>
            _rules.MasterThiefHidesTheft.For(options) && _rules.MasterThiefPerk != null && playerKnows != null && playerKnows(_rules.MasterThiefPerk);

        /// <summary>Whether <paramref name="owner"/> regards <paramref name="animal"/> as its livestock (only humanoids own animals).</summary>
        public bool IsLivestock(in AttitudeProfile animal, in AttitudeProfile owner)
        {
            if (!owner.Humanoid || animal.Humanoid || animal.IsPlayer) return false;
            bool friendly = _attitudes.Of(owner, animal) == Attitude.Friendly;
            return _rules.Livestock.IsLivestockOf(animal.Id.Name, animal.Species, owner.Alignment, friendly);
        }

        /// <summary>
        /// Knocking out <paramref name="victim"/> is an Attack crime the victim remembers personally, unless it is a
        /// guard or a boss or the fight was a duel or in the arena <b></b>.
        /// </summary>
        public static bool KnockOutIsCrime(in AttitudeProfile victim, AttackReason fightReason) =>
            !victim.IsPlayer && !victim.Guard && !victim.Boss && fightReason != AttackReason.Arena && fightReason != AttackReason.Duel;

        /// <summary>
        /// Whether a kill is reported as a murder at all: not unaligned humans, orcs and deer, and not victims killed in
        /// a fight to the death or a revolution <b></b>.
        /// </summary>
        public static bool IsMurderWorthy(in AttitudeProfile victim, AttackReason killerReason)
        {
            if (killerReason == AttackReason.Kill || killerReason == AttackReason.Revolution) return false;
            if (victim.Alignment != null) return true;
            return victim.Species != "Human" && victim.Species != "Orc" && victim.Species != "Deer";
        }

        /// <summary>The kill raises the victim's enclave's murder count when the victim was loyal to it.</summary>
        public string MurderCountedAgainst(in AttitudeProfile victim) =>
            victim.Enclave != null && _attitudes.LoyalToEnclave(victim) ? victim.Enclave : null;

        /// <summary>
        /// The witness memorizes a crime by the player (G3 MemorizePlayerCrime): Theft (seen by a guard or boss) and
        /// Murder go to the witness's enclave when the witness is loyal to it and the crime is at least as bad as what
        /// the enclave knows; everything else stays in the witness's personal memory. Friend-type NPCs never tell.
        /// </summary>
        public void Memorize(CrimeLedger ledger, in AttitudeProfile witness, CrimeType crime)
        {
            if (crime == CrimeType.None || witness.FriendType || witness.IsPlayer) return;
            if (ReportsToEnclave(ledger, witness, crime)) ledger.Report(witness.Enclave, crime);
            else ledger.RememberPersonally(witness.Id, crime);
        }

        bool ReportsToEnclave(CrimeLedger ledger, in AttitudeProfile witness, CrimeType crime)
        {
            if (crime != CrimeType.Theft && crime != CrimeType.Murder) return false;
            if (witness.Enclave == null || crime < ledger.Enclave(witness.Enclave).Known) return false;
            if (crime == CrimeType.Theft && !witness.Guard && !witness.Boss) return false;
            return _attitudes.LoyalToEnclave(witness);
        }
    }
}
