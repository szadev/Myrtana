using System;

namespace Myrtana.Rules
{
    public enum ApproachStep
    {
        /// <summary>Wait: the hero still moves (G3 PS_Interact: sAIWaitInstr 500 ms while the velocity is not zero).</summary>
        Settle,
        /// <summary>Walk on towards the use spot.</summary>
        Walk,
        /// <summary>At the spot: snap the last centimetres and face the object's use direction.</summary>
        Arrived,
        /// <summary>No progress (the object or a wall in the way) or the time is up: the rest is placed at once [interp].</summary>
        GiveUp,
        /// <summary>The secondary button (G3 action 0x66) ends the use before it began: no motion, the slot is freed.</summary>
        Cancelled,
    }

    /// <summary>
    /// The hero's walk to an object he uses (fix-75). G3 CP 1.75 Script_Game <c>PS_Interact</c> (0x1006da40): the hero stops
    /// (waits up to 500 ms while he still moves), sets <c>OnInteract_Loop</c> as the local callback and runs
    /// <c>_AI_Goto_Ext</c> (0x10042220) to the object: the destination point is set (the hero becomes its user at once),
    /// the collision with the object is switched off during the walk, the engine's goto (<c>sAIGotoInstr</c>) walks him to
    /// its interaction pose and aligns him with it; then <c>PS_Interact_&lt;UseType&gt;</c> starts (the chest: kneel, open).
    /// A press of the secondary button in task PS_Interact queues action 0x66 (<c>OnPlayerSecondaryAction</c> 0x10062ff0),
    /// which <c>OnInteract_Loop</c> / <c>PS_Interact_WaitForExit</c> turn into <c>PS_Interact_End</c>: the walk or the use
    /// ends. Pure: the Unity side feeds the distance left and the time.
    /// </summary>
    public sealed class UseApproach
    {
        /// <summary>Close enough: the rest is snapped (G3's goto ends on its own tolerance) [interp].</summary>
        public const float ArriveDistance = 0.12f;
        /// <summary>G3 PS_Interact's wait for the hero to stop.</summary>
        public const float SettleSeconds = 0.5f;
        /// <summary>No progress of <see cref="ProgressDistance"/> in this time: stuck [interp].</summary>
        public const float StuckSeconds = 0.6f, ProgressDistance = 0.05f;

        readonly float _speed;
        double _start, _settledAt = double.NaN, _progressAt, _deadline;
        float _best = float.MaxValue;
        bool _started;

        /// <param name="walkSpeed">the hero's walk speed, m/s (G3 walks to a used object [interp: the gait is a goto argument that is not known])</param>
        public UseApproach(float walkSpeed) => _speed = Math.Max(0.1f, walkSpeed);

        /// <summary>The longest walk allowed for a distance: twice the walk time and a second [interp].</summary>
        public double MaxSeconds(float distance) => distance / _speed * 2.0 + 1.0;

        /// <param name="distance">horizontal distance to the use spot, m</param>
        /// <param name="moving">the hero's own speed is not zero yet (only read before the walk)</param>
        /// <param name="cancel">the secondary button went down this frame</param>
        public ApproachStep Next(double time, float distance, bool moving, bool cancel)
        {
            if (cancel) return ApproachStep.Cancelled;
            if (!_started)
            {
                _started = true;
                _start = time;
            }
            if (double.IsNaN(_settledAt))
            {
                if (moving && time - _start < SettleSeconds) return ApproachStep.Settle;
                _settledAt = time;
                _progressAt = time;
                _best = distance;
                _deadline = time + MaxSeconds(distance);
            }
            if (distance <= ArriveDistance) return ApproachStep.Arrived;
            if (distance < _best - ProgressDistance)
            {
                _best = distance;
                _progressAt = time;
            }
            if (time - _progressAt > StuckSeconds || time > _deadline) return ApproachStep.GiveUp;
            return ApproachStep.Walk;
        }
    }
}
