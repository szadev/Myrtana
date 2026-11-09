using System;
using System.Collections;
using UnityEngine;
using G3.Unity;
using UnityEngine.Networking;

namespace Myrtana.Unity
{
    /// <summary>
    /// Plays one voice clip at a time from a file (Ogg Vorbis, streamed by UnityWebRequest; no AudioClip assets) and
    /// reports when it has finished. A new request or <see cref="Stop"/> cancels the previous one without callback.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class VoicePlayer : MonoBehaviour
    {
        AudioSource _source;
        Coroutine _playing;
        AudioClip _clip;

        void Awake()
        {
            _source = GetComponent<AudioSource>();
            G3AudioMixer.Route(_source, G3AudioBus.Voice);
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
        }

        /// <summary>Plays the file; <paramref name="finished"/> is called when it ends, or at once if it cannot be loaded.</summary>
        public void Play(string path, Action finished)
        {
            Stop();
            Requested = path;
            Status = "loading";
            _playing = StartCoroutine(PlayRoutine(path, finished));
        }

        /// <summary>Path of the last clip asked for (null before the first; diagnostics).</summary>
        public string Requested { get; private set; }

        /// <summary>What happened to it: loading, playing, finished, stopped, failed: …, not started (diagnostics).</summary>
        public string Status { get; private set; } = "idle";

        /// <summary>Logs every clip request and its outcome ("[Myrtana] voice …").</summary>
        public bool LogPlayback { get; set; }

        /// <summary>A clip is being loaded or is within its length (fix-199: a loading voice is not free for another speaker).</summary>
        public bool Busy => _playing != null || ClipActive;

        /// <summary>A clip is sounding now.</summary>
        public bool IsPlaying => _source != null && _source.isPlaying;

        /// <summary>Playback position in the current clip, s (0 when nothing plays).</summary>
        public float PlaybackTime => IsPlaying ? _source.time : 0;

        /// <summary>
        /// A loaded clip is within its length — by the source, or by the clock when the source does not report playing
        /// (paused or muted audio output, e.g. an unfocused editor). The lips follow this, not the audible output.
        /// </summary>
        public bool ClipActive => IsPlaying || (_clipStarted >= 0 && Time.unscaledTime - _clipStarted < _clipLength);

        /// <summary>Position in the current clip, s: the source's, else the clock's (see <see cref="ClipActive"/>).</summary>
        public float ClipTime => IsPlaying ? _source.time : ClipActive ? Time.unscaledTime - _clipStarted : 0;

        float _clipStarted = -1, _clipLength;

        /// <summary>Counts started clips, so listeners can tell a new clip from the last one.</summary>
        public int ClipSerial { get; private set; }

        /// <summary>Length of the current (or last) clip, s.</summary>
        public float ClipLength => _clipLength;

        /// <summary>
        /// Where the speech of the current clip starts and ends, s (<see cref="Myrtana.Ui.Dialog.VoiceSpan"/>; the lips are
        /// fitted onto it). False when the clip's samples cannot be read or it is silent.
        /// </summary>
        public bool HasVoicedSpan { get; private set; }
        public float VoicedStart { get; private set; }
        public float VoicedEnd { get; private set; }
        /// <summary>The voiced stretches of the current clip, split at pauses of 0.15 s or more (empty when unknown).</summary>
        public System.Collections.Generic.IReadOnlyList<Myrtana.Ui.Dialog.VoiceSegment> VoicedSegments => _segments;
        System.Collections.Generic.List<Myrtana.Ui.Dialog.VoiceSegment> _segments = new System.Collections.Generic.List<Myrtana.Ui.Dialog.VoiceSegment>();

        /// <summary>Loudness of the output right now (RMS of the last samples, 0…1; drives the mouth without visemes).</summary>
        public float Level
        {
            get
            {
                if (!IsPlaying) return 0;
                _source.GetOutputData(_levelSamples, 0);
                float sum = 0;
                foreach (float v in _levelSamples) sum += v * v;
                return Mathf.Sqrt(sum / _levelSamples.Length);
            }
        }

        readonly float[] _levelSamples = new float[256];

        public void Stop()
        {
            if (_playing != null)
            {
                StopCoroutine(_playing);
                if (LogPlayback) Debug.Log($"[Myrtana] voice {Name(Requested)}: stopped at {Status}");
                Status = "stopped";
            }
            _playing = null;
            _source.Stop();
            ReleaseClip();
        }

        IEnumerator PlayRoutine(string path, Action finished)
        {
            using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[Myrtana] voice clip {path}: {request.error}");
                    Status = "failed: " + request.error;
                    _playing = null;
                    finished();
                    yield break;
                }
                _clip = DownloadHandlerAudioClip.GetContent(request);
            }
            while (_clip.loadState == AudioDataLoadState.Loading) yield return null;
            if (_clip.loadState == AudioDataLoadState.Loaded)
            {
                _source.clip = _clip;
                _source.Play();
                _clipStarted = Time.unscaledTime;
                _clipLength = _clip.length;
                FindVoicedSpan(_clip);
                ClipSerial++;
                Status = "playing";
                if (LogPlayback)
                    Debug.Log($"[Myrtana] voice {Name(path)}: play {_clip.length:0.0} s, source enabled {_source.isActiveAndEnabled} " +
                              $"volume {_source.volume:0.00} listener volume {AudioListener.volume:0.00} paused {AudioListener.pause} " +
                              $"listeners {FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length}");
                yield return WaitUntilFinished(_clip.length);
            }
            else
            {
                Debug.LogWarning($"[Myrtana] voice clip {path} could not be decoded ({_clip.loadState})");
                Status = "failed: " + _clip.loadState;
            }
            _playing = null;
            ReleaseClip();
            if (Status == "playing") Status = "finished";
            if (LogPlayback) Debug.Log($"[Myrtana] voice {Name(path)}: {Status}");
            finished();
        }

        void FindVoicedSpan(AudioClip clip)
        {
            HasVoicedSpan = false;
            _segments = new System.Collections.Generic.List<Myrtana.Ui.Dialog.VoiceSegment>();
            var samples = new float[clip.samples * clip.channels];
            if (!clip.GetData(samples, 0)) return;
            _segments = Myrtana.Ui.Dialog.VoiceSpan.FindSegments(samples, clip.channels, clip.frequency);
            HasVoicedSpan = _segments.Count > 0;
            VoicedStart = HasVoicedSpan ? _segments[0].Start : 0;
            VoicedEnd = HasVoicedSpan ? _segments[_segments.Count - 1].End : 0;
        }

        static string Name(string path) => path == null ? "-" : System.IO.Path.GetFileNameWithoutExtension(path);

        /// <summary>
        /// Until the source stops after having started, bounded by the clip length (the source may report
        /// "not playing" for a frame or two after Play, and an unfocused editor may never start it).
        /// </summary>
        IEnumerator WaitUntilFinished(float length)
        {
            float start = Time.unscaledTime;
            bool started = false;
            while (Time.unscaledTime - start < length + 0.2f)
            {
                if (_source.isPlaying) started = true;
                else if (started) yield break;
                yield return null;
            }
            if (!started)
            {
                Status = "not started";
                Debug.LogWarning($"[Myrtana] voice {Name(Requested)}: the audio source never reported playing " +
                                 $"(enabled {_source.isActiveAndEnabled}, listener paused {AudioListener.pause})");
            }
        }

        void ReleaseClip()
        {
            _clipStarted = -1;
            if (_clip == null) return;
            _source.clip = null;
            Destroy(_clip);
            _clip = null;
        }

        void OnDestroy() => ReleaseClip();
    }
}
