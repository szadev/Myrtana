namespace G3.Audio
{
    /// <summary>
    /// AudioSource priorities of every sound kind (0 = most important, 256 = least; Unity/FMOD give the real voices — 32
    /// in ProjectSettings/AudioManager — to the most important playing sources first and only then to the loudest).
    /// fix-61: the placed world loops had 96 and the motion sounds 160, so in Faring (77 placed loops within 60 m of the
    /// grindstone: fire cages audible to 250 m, the waterfalls) every real voice went to a loop and the hero's tool sounds
    /// (grindstone wheel, anvil, saw) and the pooled effects were virtualised: silent. Gameplay sounds near the hero now
    /// rank above the many distant loops; G3 keeps the action audible over its ambience.
    /// </summary>
    public static class SoundPriority
    {
        /// <summary>Music voices (2D, never stolen).</summary>
        public const int Music = 0;
        /// <summary>The 2D ambience bed cross-fade pair.</summary>
        public const int AmbientBed = 32;
        /// <summary>The hero's own motion sounds: his tools at stations and objects, swings, eating (G3MotionSounds on the hero).</summary>
        public const int HeroMotion = 40;
        /// <summary>Speech and footsteps keep Unity's default.</summary>
        public const int Default = 128;
        /// <summary>Pooled effect one-shots: hits, doors, locks, spells (G3SoundPool).</summary>
        public const int Effect = 140;
        /// <summary>NPC and creature motion sounds (their tools, cries, creature steps).</summary>
        public const int CharacterMotion = 150;
        /// <summary>Random ambience one-shots (birds, wind gusts).</summary>
        public const int AmbientOneShot = 180;
        /// <summary>Placed world loops and repeats (eCAudioEmitter_PS: fires, waterfalls, wind): many and mostly far away.</summary>
        public const int WorldEmitter = 200;
    }
}
