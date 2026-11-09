using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// Save section "combatAi": the state of every NPC that is in a fight (fights against the player block saving, so
    /// these are fights between NPCs) and, since v2, the weapons taken from NPCs; v3 adds the attack clock (LastAttackEnd). Brains of NPCs no longer in the content are dropped; attack tokens are handed out
    /// again on the next tick.
    /// </summary>
    public sealed class NpcCombatSave : ISaveParticipant
    {
        readonly NpcCombatSystem _system;

        public NpcCombatSave(NpcCombatSystem system) => _system = system;

        public string Section => "combatAi";
        public int Version => 3;

        public void Write(ISaveWriter w)
        {
            var fighting = _system.Brains.Where(b => b.IsFighting).ToList();
            w.WriteInt32(fighting.Count);
            foreach (var b in fighting)
            {
                var s = b.State;
                w.WriteString(b.Self.Name);
                w.WriteInt32((int)s.Phase);
                w.WriteString(s.Target.IsEmpty ? "" : s.Target.Name);
                w.WriteInt32((int)s.Reason);
                w.WriteInt32((int)s.Weapon);
                w.WriteInt32((int)s.Move);
                w.WriteDouble(s.MoveStart);
                w.WriteDouble(s.MoveUntil);
                w.WriteBool(s.MoveRunning);
                w.WriteDouble(s.ChaseSeconds);
                w.WriteDouble(s.WaitDistance);
                w.WriteDouble(s.NextWaitCheck);
                w.WriteDouble(s.LastHitLanded);
                w.WriteDouble(s.LastHitTaken);
                w.WriteDouble(s.LastTick);
                w.WriteDouble(s.LastAttackEnd);
            }
            var lost = _system.LostItems.ToList();
            w.WriteInt32(lost.Count);
            foreach (var kv in lost)
            {
                w.WriteString(kv.Key.Name);
                w.WriteInt32(kv.Value.Count);
                foreach (var item in kv.Value) w.WriteString(item);
            }
        }

        public void Read(ISaveReader r, int version)
        {
            _system.Clear();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var id = new ActorId(r.ReadString());
                var phase = (CombatPhase)r.ReadInt32();
                string target = r.ReadString();
                var state = new NpcCombatState
                {
                    Phase = phase,
                    Target = string.IsNullOrEmpty(target) ? default : new ActorId(target),
                    Reason = (AttackReason)r.ReadInt32(),
                    Weapon = (WeaponMode)r.ReadInt32(),
                    Move = (CombatMove)r.ReadInt32(),
                    MoveStart = r.ReadDouble(),
                    MoveUntil = r.ReadDouble(),
                    MoveRunning = r.ReadBool(),
                    ChaseSeconds = (float)r.ReadDouble(),
                    WaitDistance = (float)r.ReadDouble(),
                    NextWaitCheck = r.ReadDouble(),
                    LastHitLanded = r.ReadDouble(),
                    LastHitTaken = r.ReadDouble(),
                    LastTick = r.ReadDouble(),
                };
                if (version >= 3) state.LastAttackEnd = r.ReadDouble();
                _system.Restore(id, state);
            }
            if (version < 2) return;
            int npcs = r.ReadInt32();
            for (int i = 0; i < npcs; i++)
            {
                var id = new ActorId(r.ReadString());
                int items = r.ReadInt32();
                for (int j = 0; j < items; j++) _system.TakeItem(id, r.ReadString());
            }
        }
    }
}
