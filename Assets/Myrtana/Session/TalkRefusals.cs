using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using Myrtana.Story;

namespace Myrtana.Session
{
    /// <summary>
    /// fix-158: the answer of an NPC with nothing to talk about (<see cref="TalkRefused"/>): G3 AssessTalk calls
    /// <c>StartSayRefuseTalk</c> — a shared line of the NPC's voice set, no dialog — for a humanoid that is up, not
    /// fighting and not hostile (otherwise nothing happens). The line is chosen by <see cref="RefuseTalk"/> and played as
    /// an <see cref="NpcAmbientLine"/>.
    /// </summary>
    public sealed class TalkRefusals
    {
        readonly RefuseTalk _rule = new RefuseTalk();
        readonly IContentDatabase _content;
        readonly NpcSimulation _npcs;
        readonly ActorVitals _vitals;
        readonly NpcCombatSystem _combat;
        readonly CrimeServices _crime;
        readonly EnclaveStates _enclaves;
        readonly IEventBus _events;

        public TalkRefusals(IContentDatabase content, NpcSimulation npcs, ActorVitals vitals, NpcCombatSystem combat, CrimeServices crime,
                            EnclaveStates enclaves, IEventBus events)
        {
            _content = content;
            _npcs = npcs;
            _vitals = vitals;
            _combat = combat;
            _crime = crime;
            _enclaves = enclaves;
            _events = events;
            events.Subscribe<TalkRefused>(e => Refuse(e.Npc));
        }

        /// <summary>The last refusal line said (for tests and the dev probe); null before the first.</summary>
        public string LastLine { get; private set; }

        /// <summary>The line the NPC would say now and say it; null = it says nothing (no voice, down, fighting, hostile).</summary>
        public string Refuse(ActorId npc)
        {
            if (!_content.TryGet(npc.Name, out NpcDef def) || string.IsNullOrEmpty(def.VoiceSet)) return null;
            if (_vitals.StateOf(npc) != VitalState.Alive || _combat.Get(npc)?.IsFighting == true) return null;
            if (!_crime.Attitudes.Facts.TryGetProfile(npc, out var profile) || !profile.Humanoid) return null;
            var attitude = _crime.Attitudes.Of(npc, ActorId.Player);
            if (attitude == Attitude.Hostile) return null;
            var facts = FactsOf(npc, profile, attitude);
            // G3 PSNpc Gender is not imported; the women of G3 speak with the Hum_Female_* voice sets [interp]
            facts.Female = def.VoiceSet.IndexOf("Female", System.StringComparison.OrdinalIgnoreCase) >= 0;
            var line = _rule.Next(facts);
            LastLine = line;
            _events.Publish(new NpcAmbientLine(npc, line));
            return line;
        }

        RefuseTalkFacts FactsOf(ActorId npc, in AttitudeProfile p, Attitude attitude)
        {
            var facts = new RefuseTalkFacts
            {
                Companion = p.PartyLeader.IsPlayer,
                Guard = p.Guard,
                Slave = p.Alignment == CrimeSystem.SlaveAlignment,
                Friendly = attitude == Attitude.Friendly,
                HasEnclave = p.Enclave != null,
            };
            if (p.Enclave != null)
            {
                var state = _enclaves.Of(p.Enclave);
                facts.EnclaveSwitched = _enclaves.IsSwitched(p.Enclave);
                facts.EnclaveFled = state.Status == EnclaveStatus.Flee;
                facts.EnclaveRoutine = state.Status == EnclaveStatus.Routine;
                facts.EnclaveRevolution = state.Revolution;
                facts.LoyalToEnclave = _crime.Attitudes.LoyalToEnclave(p);
                var record = _crime.Ledger.Enclave(p.Enclave);
                facts.DiscussedMurders = record.DiscussedOf(CrimeType.Murder);
                facts.DiscussedThefts = record.DiscussedOf(CrimeType.Theft);
            }
            if (_npcs.TryGet(npc, out var agent))
            {
                facts.Asleep = _npcs.Sleeps(agent);
                // G3 GetCurrentDestinationPoint: the routine point the NPC is at or on its way to
                if (!agent.Station.IsNone && _npcs.Points.TryGetPoint(agent.Station.Point, out var point)) facts.Destination = point.Interaction;
            }
            return facts;
        }
    }
}
