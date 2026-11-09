using System;
using System.IO;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// fix-211: the files of <see cref="LaunchConfig"/> in <c>Application.persistentDataPath</c> (launch.cfg: the Video
    /// page's Graphics API for the launcher; the launcher's dx12 crash marker is its own business). IO errors are logged and ignored: the option is a convenience, the game never fails over it.
    /// </summary>
    public static class LaunchConfigFile
    {
        static GraphicsApi? _api;

        static string PathOf(string name) => Path.Combine(Application.persistentDataPath, name);

        /// <summary>The stored choice (read once; the launcher may have written dx11 at this start).</summary>
        public static GraphicsApi Api
        {
            get
            {
                if (_api == null)
                {
                    string text = null;
                    try { if (File.Exists(PathOf(LaunchConfig.FileName))) text = File.ReadAllText(PathOf(LaunchConfig.FileName)); }
                    catch (Exception e) { Debug.LogWarning("launch.cfg not read: " + e.Message); }
                    _api = LaunchConfig.ParseApi(text);
                }
                return _api.Value;
            }
            set
            {
                if (_api == value) return;
                _api = value;
                try
                {
                    Directory.CreateDirectory(Application.persistentDataPath);
                    // dx12-3: a gpudiag line set by hand stays
                    string old = null;
                    try { if (File.Exists(PathOf(LaunchConfig.FileName))) old = File.ReadAllText(PathOf(LaunchConfig.FileName)); }
                    catch (Exception) { }
                    File.WriteAllText(PathOf(LaunchConfig.FileName), LaunchConfig.Format(value, LaunchConfig.ParseGpuDiag(old)));
                }
                catch (Exception e) { Debug.LogWarning("launch.cfg not written: " + e.Message); }
                Changed?.Invoke();
            }
        }

        public static event Action Changed;
    }
}
