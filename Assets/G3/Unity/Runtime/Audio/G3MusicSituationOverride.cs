using G3.Audio;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Forced music situation, asked first by the director: story music (StoryCutsceneView, PlayMusic) and the start
    /// settings write <see cref="situation"/>; it can also be set in the inspector to test (fight, showdown, dead, …).
    /// Empty = no opinion; then the game's rule answers (Myrtana.Unity.SessionMusicSituation, G3's
    /// HandleMusicSituation) and finally explore.
    /// </summary>
    [AddComponentMenu("G3/Audio/G3 Music Situation Override")]
    public sealed class G3MusicSituationOverride : MonoBehaviour, IMusicSituationSource
    {
        public string situation = "";

        public string MusicSituation => isActiveAndEnabled ? situation : null;
    }
}
