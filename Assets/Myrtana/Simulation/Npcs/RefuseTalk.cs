namespace Myrtana.Simulation
{
    /// <summary>What <see cref="RefuseTalk.Line"/> needs to know about the NPC the player talks to.</summary>
    public struct RefuseTalkFacts
    {
        /// <summary>The NPC sleeps (G3 AI mode Sleep).</summary>
        public bool Asleep;
        /// <summary>The NPC is in the player's party, or leads it.</summary>
        public bool Companion;
        /// <summary>G3 gender Female.</summary>
        public bool Female;
        /// <summary>A guard of its enclave (G3 IsGuard).</summary>
        public bool Guard;
        /// <summary>A slave (G3 IsSlave: political alignment Slave).</summary>
        public bool Slave;
        /// <summary>The NPC's attitude to the player is Friendly.</summary>
        public bool Friendly;
        /// <summary>The NPC belongs to an enclave.</summary>
        public bool HasEnclave;
        /// <summary>The enclave changed sides (G3 PSEnclave Switched).</summary>
        public bool EnclaveSwitched;
        /// <summary>The enclave flees (G3 gEEnclaveStatus Flee: liberated).</summary>
        public bool EnclaveFled;
        /// <summary>The enclave is in its daily routine (G3 Status Routine).</summary>
        public bool EnclaveRoutine;
        /// <summary>The enclave's revolution has begun (G3 PSEnclave Revolution).</summary>
        public bool EnclaveRevolution;
        /// <summary>The NPC is politically friendly to its enclave (G3 GetPoliticalAttitude(npc, enclave) == Friendly).</summary>
        public bool LoyalToEnclave;
        /// <summary>Murders of the player the enclave's guards have talked about (G3 SuspectComment[Murder]).</summary>
        public int DiscussedMurders;
        /// <summary>Thefts of the player the enclave's guards have talked about (G3 SuspectComment[Theft]).</summary>
        public int DiscussedThefts;
        /// <summary>G3 use type name of the NPC's current routine destination ("Anvil", "Bed" …); null = none.</summary>
        public string Destination;
        /// <summary>The NPC carries something in its hands (G3 hand stance 3, at TakeStone/DropStone).</summary>
        public bool Carrying;
    }

    /// <summary>
    /// G3 <c>StartSayRefuseTalk</c> (Script_Game 0x10052280, CP 1.75): the shared (SVM) line an NPC with nothing to talk about
    /// says when the player talks to it; there is no dialog. A sleeper says "SleepRefuse"; otherwise one global counter
    /// cycles three variants (0, 1, 2, 0 …): the situational line (FUN_10051a30), a parting line (FUN_10052080) and a
    /// keep-away line (FUN_10052180).
    /// </summary>
    public sealed class RefuseTalk
    {
        int _variant;

        /// <summary>The variant the next refusal uses (0-2; G3 DAT_10118a84, not saved).</summary>
        public int NextVariant => _variant;

        /// <summary>The line for this refusal; advances the variant counter unless the NPC sleeps (as in G3).</summary>
        public string Next(in RefuseTalkFacts facts)
        {
            if (facts.Asleep) return "SleepRefuse";
            string line = Line(facts, _variant);
            _variant = (_variant + 1) % 3;
            return line;
        }

        public static string Line(in RefuseTalkFacts f, int variant)
        {
            switch (variant)
            {
                case 0: return Situational(f);
                case 1: return f.Female || f.Slave ? "BetterGoNow" : f.Friendly ? "SeeYouLater" : "CertainlyBusy";
                default: return f.Female || f.Slave ? "BetterNotTalk" : f.Friendly ? "TakeCare" : "DontDisturbMe";
            }
        }

        /// <summary>FUN_10051a30.</summary>
        static string Situational(in RefuseTalkFacts f)
        {
            if (f.Companion) return "NotNow";
            if (!f.Female && f.Guard)
            {
                if (f.DiscussedMurders < 1 && f.DiscussedThefts < 20) return f.Friendly ? "GuardRefuseFriend" : "GuardRefuse";
                return "NoMoreTroubleHere";
            }
            if (f.HasEnclave && (f.EnclaveSwitched || f.EnclaveFled && !f.LoyalToEnclave)) return "RevolutionHero";
            if (f.Slave && f.HasEnclave && f.EnclaveRoutine && !f.EnclaveSwitched && f.EnclaveRevolution) return "RevolutionReady";
            return ByDestination(f.Destination, f.Carrying);
        }

        /// <summary>The line by the use type of the NPC's current destination point (switch on gEUseType 57-107).</summary>
        public static string ByDestination(string useType, bool carrying)
        {
            switch (useType)
            {
                case "Anvil": case "Forge": return "Smith";
                case "GrindStone": return "Grind";
                case "Cauldron": return "Cook";
                case "Barbecue": return "Barbecue";
                case "Alchemy": return "Alchemist";
                case "Bookshelf": case "Bookstand": case "Stoneplate": return "Read";
                case "TakeStone": case "DropStone": return carrying ? "Carry" : "SlaveWorker";
                case "PickOre": return "Mine";
                case "PickGround": case "DigGround": case "Repair": case "SawLog": case "Lumberjack": return "WorkerRefuse";
                case "Field": return "Farmer";
                case "Bed": case "SleepGround": return "GotoBed";
                case "CleanFloor": return "Sweep";
                case "Pace": return "Think";
                case "Bard": case "OrcDance": case "OrcDrum": return "Drummer";
                case "Stool": case "Bench": case "SitGround": case "Stand": return "GotoBreak";
                case "Waterpipe": return "GotoSmoke";
                case "WaterBarrel": return "GotoDrink";
                case "SitCampfire": return "Campfire";
                case "Smalltalk": return "SmalltalkRefuse";
                case "Preach": case "Shrine": return "NotNow";
                case "Listener": return "Listener";
                case "OrcBoulder": return "Stone";
                case "Pee": return "GotoPee";
                default: return "WhatDoYouWant";
            }
        }
    }
}
