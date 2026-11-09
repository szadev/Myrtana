using System;
using Myrtana.Core;

namespace Myrtana.Simulation
{
    /// <summary>The G3 routine tasks that walk between several spots (SelectRoutineTask by the routine point's use type).</summary>
    public enum WorkTask
    {
        None,
        /// <summary>ZS_Smith (Anvil, Forge): Forge → Anvil → Forge → Anvil → WaterBarrel (cool the blade).</summary>
        Smith,
        /// <summary>ZS_CarryStones (TakeStone, DropStone): carry a stone from a take spot to a drop spot and back.</summary>
        CarryStones,
        /// <summary>ZS_SlaveBabe_SweepFloor / ZS_Trader_SweepFloor (CleanFloor): sweep three spots in turn.</summary>
        SweepFloor,
        /// <summary>ZS_Farmer → RakeField or FieldWork (Field): work three spots of the field in turn.</summary>
        RakeField,
    }

    /// <summary>
    /// The multi-spot work cycles of G3's routine tasks (CP 1.75 Script_Game Routine_Routine_Tasks):
    /// <list type="bullet">
    /// <item><b>ZS_Smith</b>: TaskPosition 1..5 goes to the nearest point of use type Forge, Anvil, Forge, Anvil,
    /// WaterBarrel (in the anchor of the routine point); a missing forge or barrel is skipped, a missing anvil ends the task
    /// (ZS_Idiot). Actions: Forge 30 + R(31) s, Anvil (2 + R(3)) × 60 s, Cool 10 s; then ContinueRoutine.</item>
    /// <item><b>ZS_CarryStones</b>: to a DropStone point with It_CarryFront_Stone in the hands, to a TakeStone point with
    /// empty hands; on arrival ContinueRoutine (the take/drop clip plays) [interp: the next point is the nearest free one of
    /// the other kind, so the stones keep moving; G3's routine picks any free point of the anchor].</item>
    /// <item><b>SweepFloor / RakeField</b>: action 30 + R(31) s at the routine spot, then at further free spots of the same
    /// use type (the anchor's, or within 10 m in the same zone without an anchor; FUN_10008230 sorts them by distance from
    /// the NPC), three actions in all, then ContinueRoutine. RakeField is ZS_Farmer's 50 % branch (FieldWork the other,
    /// same cycle) [interp: step n takes the n-th spot of the sorted list (the own spot first); the index argument of
    /// FUN_10008490 is not known].</item>
    /// </list>
    /// Pure rules; <see cref="NpcSimulation"/> runs the cycle.
    /// </summary>
    public static class WorkCycles
    {
        public const string Forge = "Forge", Anvil = "Anvil", WaterBarrel = "WaterBarrel", TakeStone = "TakeStone",
                            DropStone = "DropStone", CleanFloor = "CleanFloor", Field = "Field";
        /// <summary>Spots without an anchor that a sweeper or farmer moves between lie this close (m).</summary>
        public const float SameKindReach = 10f;
        /// <summary>Spots of a sweep or field cycle.</summary>
        public const int SpotCount = 3;
        /// <summary>The take / drop clip at a stone spot (real seconds) [interp: G3 continues when the clip ends].</summary>
        public const float StoneSeconds = 4f;

        public static WorkTask TaskOf(string interaction)
        {
            switch (interaction)
            {
                case Anvil:
                case Forge: return WorkTask.Smith;
                case TakeStone:
                case DropStone: return WorkTask.CarryStones;
                case CleanFloor: return WorkTask.SweepFloor;
                case Field: return WorkTask.RakeField;
                default: return WorkTask.None;
            }
        }

        /// <summary>ZS_Smith's station kind at TaskPosition <paramref name="position"/> (1..5); null past the end.</summary>
        public static string SmithStep(int position)
        {
            switch (position)
            {
                case 1:
                case 3: return Forge;
                case 2:
                case 4: return Anvil;
                case 5: return WaterBarrel;
                default: return null;
            }
        }

        /// <summary>A missing station at this position ends the smith's task (an anvil); a forge or barrel is skipped.</summary>
        public static bool SmithStepRequired(int position) => position == 2 || position == 4;

        /// <summary>The station kind the task wants at <paramref name="position"/> (1-based), coming from <paramref name="current"/>; null = task done.</summary>
        public static string StepKind(WorkTask task, int position, string current)
        {
            switch (task)
            {
                case WorkTask.Smith: return SmithStep(position);
                case WorkTask.CarryStones: return current == TakeStone ? DropStone : TakeStone;
                case WorkTask.SweepFloor:
                case WorkTask.RakeField: return position <= SpotCount ? current : null;
                default: return null;
            }
        }

        /// <summary>Real seconds of the action at a station of <paramref name="interaction"/> within <paramref name="task"/>.</summary>
        public static float ActionSeconds(WorkTask task, string interaction, IRandom random)
        {
            int r(int n) => random?.Next(n) ?? 0;
            switch (task)
            {
                case WorkTask.Smith:
                    if (interaction == Forge) return 30 + r(31);
                    if (interaction == Anvil) return (2 + r(3)) * 60;
                    return 10; // ZS_Smith_Cool
                case WorkTask.CarryStones: return StoneSeconds;
                case WorkTask.SweepFloor:
                case WorkTask.RakeField: return 30 + r(31);
                default: return 0;
            }
        }
    }
}
