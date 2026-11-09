using System.Text;
using G3.Unity;
using UnityEditor;
using UnityEngine;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Checks the hand-written mixer asset (Resources/G3AudioMixer.mixer): loads, has Master/Music|Effects|Voice|Ambience
    /// and the exposed "&lt;Group&gt;Volume" parameters, and a set value reads back. Menu G3/Audio/Check Audio Mixer, or
    /// <c>G3.Unity.Editor.G3AudioMixerCheck.Run()</c> via eval (returns the report).
    /// </summary>
    public static class G3AudioMixerCheck
    {
        [MenuItem("G3/Audio/Check Audio Mixer")]
        static void Menu() => Debug.Log(Run());

        public static string Run()
        {
            var sb = new StringBuilder("[G3AudioMixer] ");
            var mixer = G3AudioMixer.Mixer;
            if (mixer == null) return sb.Append("FAIL: Resources/G3AudioMixer not loadable").ToString();
            bool ok = true;
            foreach (G3AudioBus bus in System.Enum.GetValues(typeof(G3AudioBus)))
            {
                var group = G3AudioMixer.Group(bus);
                bool param = mixer.GetFloat(bus + "Volume", out float db);
                bool set = param && mixer.SetFloat(bus + "Volume", -6f) && mixer.GetFloat(bus + "Volume", out float back) && Mathf.Abs(back + 6f) < 0.01f;
                if (param) mixer.ClearFloat(bus + "Volume");
                ok &= group != null && param && set;
                sb.Append($"{bus}: group {(group != null ? group.name : "MISSING")}, param {(param ? $"{db:0.#} dB" : "MISSING")}, set {(set ? "ok" : "FAIL")}; ");
            }
            sb.Append(ok ? "OK" : "FAIL");
            return sb.ToString();
        }
    }
}
