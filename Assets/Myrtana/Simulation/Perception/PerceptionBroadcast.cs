using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>An owned AI zone (a house, a camp, a city quarter) as the world reports it for the player's position.</summary>
    public struct OwnedZone
    {
        public string Id;
        /// <summary>The zone's entity name ("Tjalf_Room"); story rules address zones by it.</summary>
        public string Name;
        /// <summary>The enclave or political faction that owns the zone; null = nobody.</summary>
        public string Owner;
        /// <summary>The owner's political alignment (who is "politically friendly to the owner").</summary>
        public string OwnerAlignment;
        public ZoneSecurity Security;
        /// <summary>An indoor zone (a house): same-zone tests replace distance and line of sight.</summary>
        public bool Indoor;

        public bool IsOwned => Owner != null;
    }

    /// <summary>
    /// Something happened that NPCs nearby may notice (G3 SendPerception*): published on the event bus and delivered by
    /// <see cref="WitnessSystem"/> to every NPC that can receive it. Docs/perception-crime.md §4.
    /// </summary>
    public readonly struct PerceptionBroadcast : IGameEvent
    {
        public PerceptionBroadcast(BroadcastKind kind, ActorId source, ActorId victim, WorldPose origin, AttackReason reason = AttackReason.None,
                                   string item = null, OwnedZone zone = default, string sourceIndoorZone = null, WorldPose? sourcePose = null)
        {
            SourcePose = sourcePose;
            Kind = kind;
            Source = source;
            Victim = victim;
            Origin = origin;
            Reason = reason;
            Item = item;
            Zone = zone;
            SourceIndoorZone = sourceIndoorZone;
        }

        public BroadcastKind Kind { get; }
        /// <summary>Who did it: the attacker, winner, killer, thief, warner or intruder.</summary>
        public ActorId Source { get; }
        /// <summary>Who it was done to: the attacked, defeated or killed actor, the warned one; empty for thefts.</summary>
        public ActorId Victim { get; }
        /// <summary>Where it happened (receivers are measured from here).</summary>
        public WorldPose Origin { get; }
        /// <summary>The fight's reason (fight sound, defeat, murder, call for help).</summary>
        public AttackReason Reason { get; }
        /// <summary>The item taken or the object used.</summary>
        public string Item { get; }
        /// <summary>The zone of a theft or an entered zone.</summary>
        public OwnedZone Zone { get; }
        /// <summary>The indoor zone the event happened in (null = outdoors).</summary>
        public string SourceIndoorZone { get; }
        /// <summary>
        /// Where the source stands, when it matters: a fight sound is heard near the victim or near its attacker (CP
        /// AssessFightSound 0x1002a510 measures the receiver to both). Null = only <see cref="Origin"/>.
        /// </summary>
        public WorldPose? SourcePose { get; }

        public override string ToString() => $"{Kind} {Source}→{Victim} ({Reason})";
    }

    /// <summary>What an NPC is busy with, as far as receiving broadcasts matters.</summary>
    public enum ReceiverMode { Routine, Combat, Flee, Watching, Talking, Asleep, Down, Dead, FetchingWeapon }

    /// <summary>An NPC that might receive a broadcast, as the world sees it now.</summary>
    public struct BroadcastReceiver
    {
        public ActorId Id;
        public WorldPose Pose;
        public DetailLevel Detail;
        public ReceiverMode Mode;
        /// <summary>The indoor zone the NPC stands in (null = outdoors).</summary>
        public string IndoorZone;
        /// <summary>The zone of the NPC's sleeping place (an intruder there brings it home).</summary>
        public string HomeZone;
        /// <summary>Who the NPC fights (empty when not fighting).</summary>
        public ActorId CombatTarget;
    }

    /// <summary>
    /// Who receives a broadcast (perception.json eventPerceptions; Docs/perception-crime.md §4.1): awake NPCs that are
    /// not talking, down or dead, within the broadcast's range and, where required, with a free line of sight to the
    /// origin. Some kinds exclude bosses, friend-type NPCs or NPCs already busy with a fight.
    /// </summary>
    public static class BroadcastReception
    {
        /// <param name="revolution">
        /// The receiver's enclave is in Attack and its revolution attitude toward the victim is Friendly or Hostile: the fight
        /// sound carries <see cref="BroadcastRule.RevolutionRange"/> (CP AssessFightSound 4000).
        /// </param>
        /// <param name="ownSide">
        /// The sound comes from the receiver's own side (<see cref="OwnSide"/>): an enclave member hears its comrades' fight,
        /// warning or call for help through rock and ground like G3 (no occlusion), fix-216.
        /// </param>
        public static bool CanReceive(in BroadcastReceiver r, in PerceptionBroadcast b, in AttitudeProfile profile, PerceptionRules rules,
                                      ILineOfSight lineOfSight, bool revolution = false, bool ownSide = false)
        {
            if (r.Id.IsPlayer || r.Id == b.Source || r.Id == b.Victim) return false;
            if (!PerceptionRules.ReceivesBroadcasts(r.Detail)) return false;
            if (!Awake(r.Mode)) return false;
            var rule = rules.BroadcastOf(b.Kind);
            if (rule == null) return false;

            switch (b.Kind)
            {
                case BroadcastKind.Warn:
                    // CP AssessWarn: not friend-type NPCs, not the player's party members (party leader == player)
                    if (profile.FriendType || profile.InPlayerParty) return false;
                    break;
                case BroadcastKind.FightSound:
                    if (profile.Boss || r.Mode == ReceiverMode.Combat || r.Mode == ReceiverMode.Flee || r.Mode == ReceiverMode.Watching) return false;
                    if (!HearsFightIn(r.IndoorZone, b.SourceIndoorZone, b.Reason)) return false;
                    break;
                case BroadcastKind.Defeat:
                    if (profile.Boss) return false;
                    if (r.Mode == ReceiverMode.Combat && !r.CombatTarget.IsEmpty) return false;
                    break;
            }

            float range = revolution && rule.RevolutionRange is float far ? far : rule.Range;
            var heard = b.Origin;
            float distance = Senses.Distance(r.Pose, heard);
            if (b.Kind == BroadcastKind.FightSound && b.SourcePose is WorldPose at && Senses.Distance(r.Pose, at) < distance)
            {
                heard = at;
                distance = Senses.Distance(r.Pose, at);
            }
            if (distance > range) return false;
            // port (fix-204) muffles sounds through rock and ground; G3 has no occlusion, and fix-216 [owner: the orcs above
            // the Hammer clan's smelter did not come down to help]: a member always hears its own side's fight and shouts
            bool needsLine = rule.LineOfSight || IsSound(b.Kind) && !ownSide && distance > range * rules.OccludedHearingFactor;
            return !needsLine || Senses.SameIndoorZone(r.IndoorZone, b.SourceIndoorZone) ||
                   (lineOfSight ?? OpenLineOfSight.Instance).IsClear(r.Pose, heard);
        }

        /// <summary>
        /// The broadcast concerns the receiver's own side (fix-216): it shares an enclave with the source or the victim, or it
        /// belongs to the hero's party and the hero or a companion is involved. Such sounds are never muffled by obstacles.
        /// </summary>
        public static bool OwnSide(in AttitudeProfile receiver, in AttitudeProfile source, in AttitudeProfile victim)
        {
            if (receiver.Enclave != null && (receiver.Enclave == source.Enclave || receiver.Enclave == victim.Enclave)) return true;
            return receiver.InPlayerParty && (source.IsPlayer || victim.IsPlayer || source.InPlayerParty || victim.InPlayerParty);
        }

        /// <summary>
        /// Broadcasts that are heard, not seen (G3: no line of sight): the fight's noise, a warning shout, a call for help. Port
        /// (fix-204): through a wall or the ground they carry only <see cref="PerceptionRules.OccludedHearingFactor"/> of their
        /// range (G3 ignored obstacles; the owner heard fights start reactions on the far side of a rock).
        /// </summary>
        public static bool IsSound(BroadcastKind kind) =>
            kind == BroadcastKind.FightSound || kind == BroadcastKind.Warn || kind == BroadcastKind.CallHelp;

        /// <summary>
        /// The fight sound's zone test (CP AssessFightSound 0x1002a510): a fight whose victim stands in an indoor zone is
        /// heard only inside that zone; a fight outdoors is heard by everyone in range, indoors too; an arena fight skips
        /// the test. fix-147: the receiver's own indoor zone deafened it to outdoor fights before, so Potros in his tent
        /// (indoor zone 460f52ad) never heard the hero fight his camp.
        /// </summary>
        public static bool HearsFightIn(string receiverIndoorZone, string victimIndoorZone, AttackReason reason) =>
            reason == AttackReason.Arena || victimIndoorZone == null || Senses.SameIndoorZone(receiverIndoorZone, victimIndoorZone);

        /// <summary>Asleep, talking, down, dead or fetching a weapon: deaf to every broadcast.</summary>
        public static bool Awake(ReceiverMode mode) =>
            mode == ReceiverMode.Routine || mode == ReceiverMode.Combat || mode == ReceiverMode.Flee || mode == ReceiverMode.Watching;
    }
}
