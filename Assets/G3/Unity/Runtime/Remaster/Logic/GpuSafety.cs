using System;
using System.Collections.Generic;
using System.Text;

namespace G3.Unity
{
    /// <summary>
    /// dx12-3: GPU objects (buffers, render textures, meshes, materials) let go of while the frame may still use them are
    /// released a few frames later instead of at once. On D3D12 a resource freed by the CPU is gone for every command the
    /// CPU records after that point, even when the recording belongs to the same frame: a Graphics.RenderMeshIndirect
    /// queued earlier in the frame (the save picture renders the main camera from Update) or a global buffer still bound
    /// would read freed memory (page fault = DXGI_ERROR_DEVICE_HUNG). D3D11 kept such resources alive by reference counting.
    /// Engine-free: the caller supplies the frame number and the release action.
    /// </summary>
    public sealed class DeferredRelease<T> where T : class
    {
        readonly Queue<(int frame, T item)> _queue = new Queue<(int, T)>();

        /// <summary>Frames an item waits (released when <c>frame - added >= Delay</c>).</summary>
        public int Delay { get; }

        public DeferredRelease(int delay) { Delay = Math.Max(1, delay); }

        public int Count => _queue.Count;

        public void Add(T item, int frame)
        {
            if (item != null) _queue.Enqueue((frame, item));
        }

        /// <summary>Releases the items old enough at <paramref name="frame"/> (oldest first); returns how many.</summary>
        public int Drain(int frame, Action<T> release)
        {
            int n = 0;
            while (_queue.Count > 0)
            {
                var head = _queue.Peek();
                // frame counter reset (a new Play session in the editor): everything older than "now" is due
                if (frame - head.frame < Delay && head.frame <= frame) break;
                _queue.Dequeue();
                n++;
                release?.Invoke(head.item);
            }
            return n;
        }

        /// <summary>Releases everything (quitting, end of Play).</summary>
        public int DrainAll(Action<T> release)
        {
            int n = 0;
            while (_queue.Count > 0)
            {
                var head = _queue.Dequeue();
                n++;
                release?.Invoke(head.item);
            }
            return n;
        }
    }

    /// <summary>
    /// dx12-3: the GPU diagnostics switch (<c>-g3gpudiag</c> on the player command line, or env <c>G3_GPUDIAG=1</c>; the
    /// launcher's option adds Unity's D3D12 debug layer / DRED flags as well) and the per-frame step counters it logs.
    /// </summary>
    public static class GpuDiagSwitch
    {
        public const string Switch = "-g3gpudiag";
        public const string EnvVar = "G3_GPUDIAG";

        public static bool Enabled(IReadOnlyList<string> args, string env)
        {
            if (args != null)
                foreach (var a in args)
                    if (string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(env)) return false;
            env = env.Trim();
            return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(env, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Counters of GPU-side steps within one frame, written as one line (<c>f123 grass.cull=4 relief.write=1</c>).</summary>
    public sealed class GpuDiagFrame
    {
        readonly List<string> _keys = new List<string>();
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public bool Empty => _keys.Count == 0;

        public void Add(string key, int n = 1)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_counts.TryGetValue(key, out int c)) _counts[key] = c + n;
            else { _counts[key] = n; _keys.Add(key); }
        }

        public int Get(string key) => key != null && _counts.TryGetValue(key, out int c) ? c : 0;

        /// <summary>The line for <paramref name="frame"/> (keys in first-use order), then cleared.</summary>
        public string Take(int frame)
        {
            var sb = new StringBuilder();
            sb.Append('f').Append(frame);
            foreach (var k in _keys) sb.Append(' ').Append(k).Append('=').Append(_counts[k]);
            _keys.Clear();
            _counts.Clear();
            return sb.ToString();
        }
    }
}
