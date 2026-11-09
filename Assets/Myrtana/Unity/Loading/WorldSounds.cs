using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// The one owner of <see cref="AudioListener.pause"/>: the loading hold, the title and the game menu each mute the
    /// world's sounds (effects, ambience, voices) on their own behalf; they come back when the last of them lets go.
    /// The music voices and the video screen ignore the listener pause and keep playing — G3's menu mutes every channel
    /// group except the music (gCHUDMenuMain2 open/close), and its loading screen leaves the music on.
    /// </summary>
    public static class WorldSounds
    {
        static readonly HoldSet Owners = new HoldSet();

        public static bool IsMuted => Owners.IsHeld;

        public static void Mute(object owner, bool mute)
        {
            if (Owners.Set(owner, mute)) AudioListener.pause = Owners.IsHeld;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()   // domain reload is off in the editor
        {
            if (Owners.Clear()) AudioListener.pause = false;
        }
    }
}
