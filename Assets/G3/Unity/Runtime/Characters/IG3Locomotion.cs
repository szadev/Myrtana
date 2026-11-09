using System;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Movement state a character controller exposes to its animation driver (<see cref="G3HeroAnimator"/>).
    /// The controller moves the entity (G3 clips are in place, Docs/formats-animation.md §2); the driver only reads this.
    /// </summary>
    public interface IG3Locomotion
    {
        /// <summary>Actual world velocity of the last movement step, m/s.</summary>
        Vector3 Velocity { get; }
        bool IsGrounded { get; }
        /// <summary>Sliding down a slope steeper than the walkable limit.</summary>
        bool IsSliding { get; }
        /// <summary>Seconds since the feet left the ground (0 while grounded).</summary>
        float AirTime { get; }
        /// <summary>False while the controller is switched off (fly camera, streaming hold).</summary>
        bool IsActive { get; }
        /// <summary>Sneaking: the locomotion uses the Sneak stance.</summary>
        bool Sneaking { get; }
        /// <summary>A jump was started this frame.</summary>
        event Action Jumped;
        /// <summary>Touched the ground again: (seconds in the air, downward speed at impact in m/s).</summary>
        event Action<float, float> Landed;
    }

    /// <summary>
    /// Optional water state of a controller (<see cref="G3.Unity.G3Player"/>): the animation picks the Wade / Swim stance
    /// and plays the dive-in (Docs/hero.md "Water and swimming").
    /// </summary>
    public interface IG3WaterLocomotion
    {
        G3.Player.WaterMode Water { get; }
        /// <summary>Fell into deep water (seconds of the fall).</summary>
        event Action<float> DivedIn;
    }

    /// <summary>A character model that renders differently when the camera sits inside its head or close to it.</summary>
    public interface IG3BodyView
    {
        /// <summary>First person: the head only casts shadows (the camera sits in it).</summary>
        void SetFirstPerson(bool firstPerson);
        /// <summary>The third-person camera is so close that G3 has faded the hero out: the whole model (and what he
        /// carries) only casts shadows (fix-94, CameraStability.HidesHero).</summary>
        void SetHidden(bool hidden);
    }
}
