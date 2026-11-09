using System;
using System.IO;
using System.Runtime.InteropServices;
using Myrtana.Ui.Skin;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// rebrand: the demo build's stamp "yyyy-MM-dd commit", written by DemoBuild.Package into
    /// <c>StreamingAssets/build_stamp.txt</c> of the player; null in the editor or a build without it. Read once.
    /// </summary>
    public static class BuildStamp
    {
        public const string FileName = "build_stamp.txt";
        static bool _read;
        static string _value;

        public static string Value
        {
            get
            {
                if (_read) return _value;
                _read = true;
                try
                {
                    string path = Path.Combine(Application.streamingAssetsPath, FileName);
                    if (File.Exists(path)) _value = File.ReadAllText(path).Trim();
                }
                catch (Exception) { _value = null; }
                return _value;
            }
        }
    }

    /// <summary>
    /// rebrand: the player window's title "Gothic 3 Remaster". Unity titles the window with PlayerSettings.productName,
    /// which stays "G3World" so that Application.persistentDataPath (LocalLow/G3World/G3World: saves, Player.log) and the
    /// PlayerPrefs key (HKCU\Software\G3World\G3World) do not move; the title is set here instead (Windows player only).
    /// </summary>
    public static class WindowTitle
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr hWnd, string text);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();

        static uint s_pid;
        static int s_set;

        // IL2CPP marshals only static callbacks marked with MonoPInvokeCallback ("window title not set" in the il2cpp demo)
        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool Visit(IntPtr hWnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != s_pid) return true;
            var cls = new System.Text.StringBuilder(64);
            GetClassNameW(hWnd, cls, cls.Capacity);
            if (cls.ToString() == "UnityWndClass" && SetWindowTextW(hWnd, RemasterBrand.Title)) s_set++;
            return true;
        }

        static readonly EnumWindowsProc s_visit = Visit;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Apply()
        {
            try
            {
                s_pid = GetCurrentProcessId();
                s_set = 0;
                EnumWindows(s_visit, IntPtr.Zero);
                Debug.Log($"[Myrtana] window title \"{RemasterBrand.Title}\" ({s_set} window)");
            }
            catch (Exception ex) { Debug.LogWarning("[Myrtana] window title not set: " + ex.Message); }
        }
#endif
    }
}
