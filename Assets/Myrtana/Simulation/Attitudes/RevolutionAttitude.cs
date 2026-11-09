using System;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// G3 GetRevolutionAttitude (attitudes.json revolutionAttitude; rule "revolution" after "outlaw"): how a member of an
    /// enclave that raids, fights or flees sees the player's side and the other people of its enclave.
    /// E = self's enclave; aS / aO = the political attitude of self's / other's own alignment toward E's alignment
    /// (Friendly, Neutral or Hostile; no alignment = no opinion). The player's side is the player, his party and
    /// Friend-type NPCs. Null = no opinion (the next rule decides).
    /// </summary>
    public static class RevolutionAttitude
    {
        /// <summary>Dancers keep dancing (attitudes.json ignoredNamesContaining).</summary>
        static readonly string[] IgnoredNames = { "Ass_Babe_Dance" };

        /// <param name="toward">Political attitude of an alignment toward an alignment (same alignment = Friendly).</param>
        public static Attitude? Of(in AttitudeProfile self, in AttitudeProfile other, IEnclaveStates enclaves,
                                   Func<string, string, Attitude?> toward)
        {
            if (enclaves == null || self.Enclave == null || self.IsPlayer) return null;
            var e = enclaves.Of(self.Enclave);
            if (e.IsQuiet) return null;
            string name = self.Id.Name;
            if (name != null)
                foreach (var ignored in IgnoredNames)
                    if (name.IndexOf(ignored, StringComparison.Ordinal) >= 0) return null;
            string enclaveAlignment = self.EnclaveAlignment;
            var aS = Side(toward(self.Alignment, enclaveAlignment)); // no own alignment: G3's 0 column (GetPoliticalAttitude None, fix-91)
            bool playerSide = other.IsPlayer || other.InPlayerParty || other.FriendType;
            if (aS == null)
            {
                // fix-94, G3's column for aS 0 (an NPC without an alignment of its own): toward the player's side it sides
                // with a revolution while the enclave fights and with the hero while it flees; else no opinion.
                if (!playerSide) return null;
                if (e.Status == EnclaveStatus.Attack) return e.Revolution ? Attitude.Friendly : (Attitude?)null;
                if (e.Status == EnclaveStatus.Flee) return Attitude.Friendly;
                return null;
            }

            if (playerSide)
            {
                switch (e.Status)
                {
                    case EnclaveStatus.Routine:
                        if (aS == Attitude.Hostile) return e.Raid ? Attitude.Friendly : (Attitude?)null;
                        if (aS == Attitude.Neutral) return e.Revolution ? Attitude.Friendly : (Attitude?)null;
                        // aS Friendly: only the untransformed player himself, and only outside each other's party.
                        bool partyTie = self.PartyLeader == other.Id || other.PartyLeader == self.Id;
                        return (e.Raid || e.Revolution) && other.IsPlayer && !other.Transformed && !partyTie
                            ? Attitude.Angry : (Attitude?)null;
                    case EnclaveStatus.Attack:
                        if (aS == Attitude.Friendly) return Attitude.Hostile;
                        if (aS == Attitude.Hostile) return Attitude.Friendly;
                        return e.Revolution ? Attitude.Friendly : (Attitude?)null;
                    default: // Flee
                        return aS == Attitude.Friendly ? Attitude.Panic : Attitude.Friendly;
                }
            }

            if (e.Status != EnclaveStatus.Attack) return null;
            var aO = Side(toward(other.Alignment, enclaveAlignment));
            if (aO == null) return null;
            switch (aS)
            {
                case Attitude.Friendly:
                    if (aO == Attitude.Friendly) return Attitude.Friendly;
                    if (aO == Attitude.Neutral) return e.Revolution ? Attitude.Hostile : (Attitude?)null;
                    return Attitude.Hostile;
                case Attitude.Neutral:
                    if (!e.Revolution) return null;
                    return aO == Attitude.Friendly ? Attitude.Hostile : Attitude.Friendly;
                default: // Hostile
                    if (aO == Attitude.Friendly) return Attitude.Hostile;
                    if (aO == Attitude.Neutral) return e.Revolution ? Attitude.Friendly : (Attitude?)null;
                    return Attitude.Friendly;
            }
        }

        /// <summary>Friendly / Neutral / Hostile as the table's columns; Angry counts as Hostile, anything else as no row.</summary>
        static Attitude? Side(Attitude? a)
        {
            switch (a)
            {
                case Attitude.Friendly: return Attitude.Friendly;
                case Attitude.Neutral: return Attitude.Neutral;
                case Attitude.Angry:
                case Attitude.Hostile: return Attitude.Hostile;
                default: return null;
            }
        }
    }
}
