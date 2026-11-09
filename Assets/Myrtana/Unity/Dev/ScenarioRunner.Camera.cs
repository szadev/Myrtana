#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using System.Collections;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    public sealed partial class ScenarioRunner
    {
        CameraJitterProbe _camProbe;
        string _camProbeLabel;

        /// <summary>'camprobe on|off [label=]': starts or stops the camera jitter probe; 'off' logs the metrics.</summary>
        IEnumerator CamProbeStep(Myrtana.Session.Dev.ScenarioStep st)
        {
            string mode = (st.Get("mode", "on") ?? "on").ToLowerInvariant();
            if (mode == "on" || mode == "start" || mode == "1")
            {
                if (_camProbe == null) _camProbe = gameObject.AddComponent<CameraJitterProbe>();
                _camProbe.hero = Hero;
                _camProbe.Stats.Clear();
                _camProbe.enabled = true;
                _camProbeLabel = st.Get("label", "camera");
                Log($"camprobe on ({_camProbeLabel})");
                yield return null;
                yield break;
            }
            if (_camProbe == null) { Fail("camprobe off: not started"); yield break; }
            _camProbe.enabled = false;
            Result($"camprobe {_camProbeLabel}: {_camProbe.Stats.Summary()}");
        }

        TorsoProbe _torsoProbe;
        string _torsoProbeLabel;

        /// <summary>'torsoprobe on|off [label=] [trace=1]' (fix-89): torso shape while running, plain vs with an overlay.</summary>
        IEnumerator TorsoProbeStep(Myrtana.Session.Dev.ScenarioStep st)
        {
            string mode = (st.Get("mode", "on") ?? "on").ToLowerInvariant();
            if (mode == "on" || mode == "start" || mode == "1")
            {
                if (_torsoProbe == null) _torsoProbe = gameObject.AddComponent<TorsoProbe>();
                _torsoProbe.hero = Hero;
                _torsoProbe.trace = st.Bool("trace", false);
                _torsoProbe.Stats.Clear();
                _torsoProbe.enabled = true;
                _torsoProbeLabel = st.Get("label", "torso");
                Log($"torsoprobe on ({_torsoProbeLabel})");
                yield return null;
                yield break;
            }
            if (_torsoProbe == null) { Fail("torsoprobe off: not started"); yield break; }
            _torsoProbe.enabled = false;
            Result($"torsoprobe {_torsoProbeLabel}: {_torsoProbe.Stats.Summary()}");
        }

        /// <summary>Condition 'torso:metric' (fix-89): the last torso probe's metric (TorsoPoseStats), "-" without one.</summary>
        string Torso(string metric)
        {
            if (_torsoProbe == null || _torsoProbe.Stats.Count == 0) return "-";
            float v = _torsoProbe.Stats.Metric(metric);
            return float.IsNaN(v) ? "-" : v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Condition 'camjitter:metric': the last probe's metric (see CameraJitterStats), "-" without one.</summary>
        string CamJitter(string metric)
        {
            if (_camProbe == null || _camProbe.Stats.Count == 0) return "-";
            float v = _camProbe.Stats.Metric(metric);
            return float.IsNaN(v) ? "-" : v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
#endif
