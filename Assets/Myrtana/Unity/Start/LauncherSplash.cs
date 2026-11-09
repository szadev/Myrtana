using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-152: the player's half of the start-up splash (Docs/game-start.md "Start-up splash"). The launcher
    /// (<c>G3World.exe</c>, Tools/Launcher) shows G3's <c>splash.bmp</c> on the desktop as Gothic 3 does and starts this
    /// player with <see cref="EnvVar"/> naming an event; the player sets it once its first frame is on screen, and the
    /// launcher closes the splash (it also closes it when the player exits, or after 60 s). Started without the
    /// launcher (no variable), in the editor or on another platform: nothing.
    /// </summary>
    public sealed class LauncherSplash : MonoBehaviour
    {
        public const string EnvVar = "G3WORLD_SPLASH_EVENT";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Arm()
        {
            if (Application.isEditor) return;
            string name = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrEmpty(name)) return;
            var go = new GameObject("Launcher splash") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<LauncherSplash>().StartCoroutine(SignalAfterFirstFrame(go, name));
        }

        static IEnumerator SignalAfterFirstFrame(GameObject go, string name)
        {
            yield return new WaitForEndOfFrame(); // the first frame is rendered
            yield return null;                    // and presented: the game window shows the game, not an empty client
            bool done = Signal(name);
            Environment.SetEnvironmentVariable(EnvVar, null); // not inherited by anything the player starts
            StartupTimeline.Mark(done ? "launcher splash closed" : "launcher splash event not found");
            Destroy(go);
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        const uint EventModifyState = 0x0002;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetEvent(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        static bool Signal(string name)
        {
            IntPtr h = OpenEventW(EventModifyState, false, name);
            if (h == IntPtr.Zero) return false;
            try { return SetEvent(h); }
            finally { CloseHandle(h); }
        }
#else
        static bool Signal(string name) => false;
#endif
    }
}
