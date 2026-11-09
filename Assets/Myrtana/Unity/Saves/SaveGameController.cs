using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Myrtana.Core;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace Myrtana.Unity
{
    /// <summary>
    /// Quicksave and quickload (session keys QuickSave / QuickLoad, G3 defaults F5 / F9) and the port's autosave
    /// (<see cref="AutosaveSchedule"/>; G3 has none). One file each, <c>quick</c> and <c>auto</c> (fix-107 [owner]: the autosave
    /// is the backup; fix-82's second files are folded in at start, <see cref="SaveSlotMigration"/>). Saving is refused while the session reports a blocker (conversation,
    /// fight, death, falling …); an autosave that falls due then waits until saving is possible again. Every save goes
    /// through the same <see cref="TrySave"/> (session serializer, header, picture). Messages are G3's own strings. The
    /// game menu saves to and loads named slots through the same controller; while it is open (<see cref="Suspended"/>)
    /// the keys and the autosave rest.
    /// fix-155 [owner]: crash recovery (<see cref="CrashRecovery"/>): every write is atomic with a backup
    /// (<see cref="SaveSlots"/>); a hidden snapshot is taken every 2 min of play and after key moments when saving is allowed
    /// (<see cref="RecoverySchedule"/>, <see cref="RecoveryMoments"/>) - serialized on the main thread, written on a worker;
    /// a session marker tells an unclean exit, and the title then offers the snapshot (<see cref="RecoveryOffer"/>).
    /// </summary>
    public sealed class SaveGameController : MonoBehaviour, ISessionView
    {
        static readonly TextId SavedText = new TextId("HUD_QuickSave");
        static readonly TextId SaveFailedText = new TextId("HUD_File_SaveFailed");
        static readonly TextId LoadFailedText = new TextId("HUD_File_LoadFailed");

        [Tooltip("Real minutes between autosaves (0 = off).")]
        public float autosaveMinutes = AutosaveSchedule.DefaultMinutes;

        GameBootstrap _game;
        SaveSlots _slots;
        HudMessages _messages;
        GameSession _session;
        readonly AutosaveSchedule _autosave = new AutosaveSchedule();
        bool _dueLogged;
        readonly RecoverySchedule _recovery = new RecoverySchedule();
        RecoveryMoments _moments;
        SessionMarker _marker;
        Task _recoveryWrite;
        bool _recoveryAnswered;

        /// <summary>The port option "Crash recovery" (fix-155, <see cref="PortOptionsApplier"/>): hidden snapshots and the title's question.</summary>
        public static bool CrashRecoveryEnabled { get; set; } = true;

        /// <summary>The game is in play (not the title, logos, intro or the loading of an area): snapshots may be taken (set by the bootstrap).</summary>
        public Func<bool> InPlay { get; set; }

        /// <summary>Main-thread milliseconds of the last snapshot's serialization (runner checks; -1: none yet).</summary>
        public double LastRecoveryMs { get; private set; } = -1;
        /// <summary>Bytes of the last snapshot (runner checks).</summary>
        public int LastRecoveryBytes { get; private set; }
        /// <summary>Snapshots taken in this run.</summary>
        public int RecoveryCount { get; private set; }
        public RecoverySchedule RecoveryTimer => _recovery;
        /// <summary>The previous run ended without a normal quit (its session marker was found at start-up).</summary>
        public bool UncleanExit => _marker != null && _marker.UncleanExit;
        /// <summary>The runner's stand-in for a crashed previous run (fix-155 scenario): the title question is asked as after one.</summary>
        public bool SimulateUncleanExit { get; set; }

        /// <summary>The quicksave file F9 loads, null when there is none.</summary>
        public string QuickLoadSlot => _slots.Exists(SaveSlots.QuickSlot) ? SaveSlots.QuickSlot : null;

        /// <summary>The file the last quicksave / autosave went to (runner checks), or null.</summary>
        public string LastWritten { get; private set; }

        /// <summary>Set while the game menu is open: no quick keys, no autosave.</summary>
        public bool Suspended { get; set; }

        /// <summary>Why saving is refused now (conversation, falling …), or null.</summary>
        public string SaveBlockReason => _session?.SaveBlockReason;

        public void Init(GameBootstrap game, SaveSlots slots, HudMessages messages)
        {
            _game = game;
            _slots = slots;
            _messages = messages;
            _slots.MigrateLegacySlots();
            _marker = new SessionMarker(Path.Combine(_slots.Directory, CrashRecovery.MarkerFile));
            try
            {
                if (_marker.Begin(DateTime.UtcNow, ProcessId()))
                    Debug.Log($"[Myrtana] crash recovery: the last run did not quit normally (marker {_marker.Path}); recovery save {(_slots.Exists(CrashRecovery.Slot) ? "found" : "none")}");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Myrtana] crash recovery marker {_marker.Path}: {ex.Message}");
            }
            Application.quitting += OnQuitting;
        }

        static int ProcessId()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch (Exception) { return 0; } // no process ids here: every leftover marker counts as a crash
        }

        void OnDestroy()
        {
            Application.quitting -= OnQuitting;
            _moments?.Dispose();
        }

        /// <summary>A normal quit: the pending snapshot finishes, then the snapshot and the marker go (fix-155).</summary>
        void OnQuitting()
        {
            try
            {
                _recoveryWrite?.Wait(5000);
                _slots.WaitForWrites("quit");   // fix-251: a save still on the write queue is finished first
                _slots.Delete(CrashRecovery.Slot);
                _marker?.End();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is AggregateException)
            {
                Debug.LogWarning($"[Myrtana] crash recovery on quit: {ex.Message}");
            }
        }

        public void Bind(GameSession session)
        {
            _session = session;
            _autosave.IntervalSeconds = autosaveMinutes * 60f;
            _autosave.Restart(Time.unscaledTime);
            _dueLogged = false;
            _moments?.Dispose();
            _moments = new RecoveryMoments(session.Events, session.Content.Database, OnKeyMoment);
            _recovery.Restart(Time.unscaledTime);
            WarmUpSerializer(session);
        }

        /// <summary>
        /// fix-155: one throw-away serialization while the new game is still behind the loading screen, so the first
        /// recovery snapshot in play does not pay the serializer's first-call (JIT) cost (~0.2 s measured).
        /// </summary>
        void WarmUpSerializer(GameSession session)
        {
            if (!CrashRecoveryEnabled || session.SaveBlockReason != null) return;
            try
            {
                var watch = Stopwatch.StartNew();
                int maps = SaveTitle.WarmUp(session); // fix-166: the place lookup of the title (no hero pose yet here)
                string title = SaveTitle.Of(session);
                double titleMs = watch.Elapsed.TotalMilliseconds;
                if (_recoveryWrite == null || _recoveryWrite.IsCompleted)
                {
                    _recoveryBuffer.SetLength(0);
                    session.Save(_recoveryBuffer, title, DateTime.UtcNow);
                }
                Debug.Log($"[Myrtana] recovery: serializer warmed up behind the loading screen ({watch.Elapsed.TotalMilliseconds:0.0} ms, " +
                          $"title {titleMs:0.0} ms with {maps} maps)");
            }
            catch (Exception ex) // best effort: a host section not ready yet only means no warm-up
            {
                Debug.LogWarning($"[Myrtana] recovery warm-up skipped: {ex.Message}");
            }
        }

        void OnKeyMoment(string reason)
        {
            if (!CrashRecoveryEnabled) return;
            _recovery.KeyMoment(Time.unscaledTime);
            Debug.Log($"[Myrtana] recovery snapshot soon: {reason}");
        }

        void Update()
        {
            RunMainThreadWork();
            DecodeThumbnails();
            if (_session == null || Suspended || LoadingHold.IsHeld) return; // no saves of a half-loaded area
            if (_autosave.Enabled && Time.unscaledTime >= _autosave.Due && !_dueLogged)
            {
                _dueLogged = true; // fix-112: one line when an autosave falls due, with what (if anything) holds it
                Debug.Log($"[Myrtana] autosave due{(_session.SaveBlockReason != null ? " - waits: " + _session.SaveBlockReason : "")}");
            }
            // Session keys QuickSave / QuickLoad (G3 defaults F5 / F9; the port used F8 for loading before par-controls).
            if (G3.Unity.G3Input.WasPressed(G3.Player.SessionKey.QuickSave)) QuickSave();
            else if (G3.Unity.G3Input.WasPressed(G3.Player.SessionKey.QuickLoad)) QuickLoad();
            else
            {
                _autosave.IntervalSeconds = autosaveMinutes * 60f;
                double now = Time.unscaledTime;
                // perf-3: the save blockers (dialog, combat, death …) are asked only once a save is due; before that
                // ShouldSave is false whatever they say
                if ((_autosave.Enabled && now >= _autosave.Due) || (CrashRecoveryEnabled && now >= _recovery.Due))
                {
                    string blocked = _session.SaveBlockReason;
                    if (_autosave.ShouldSave(now, blocked, paused: false)) Autosave();
                    else if (CrashRecoveryEnabled && _recovery.ShouldSave(now, blocked, paused: !RecoveryPossible))
                        WriteRecovery();
                }
            }
        }

        /// <summary>In play, not paused (tutorial box, intro), no snapshot still being written.</summary>
        bool RecoveryPossible => (InPlay == null || InPlay()) && !GamePause.IsPaused && (_recoveryWrite == null || _recoveryWrite.IsCompleted);

        /// <summary>
        /// fix-155: the hidden snapshot now (also the runner's <c>recovery</c> op): the game is serialized into memory on the
        /// main thread (timed: <see cref="LastRecoveryMs"/>) and written atomically on a worker thread. False while saving is
        /// blocked or the previous snapshot is still being written.
        /// </summary>
        public bool WriteRecovery()
        {
            if (_session == null || _session.SaveBlockReason != null) return false;
            if (_recoveryWrite != null && !_recoveryWrite.IsCompleted) return false;
            _recoverySections.Clear();
            int gcBefore = GC.CollectionCount(0);
            var watch = Stopwatch.StartNew();
            string title = SaveTitle.Of(_session);
            double titleMs = watch.Elapsed.TotalMilliseconds;
            // fix-166: one buffer for the game's life (was a fresh 1 MB stream + a copy per snapshot: garbage that made a GC
            // fall into a later snapshot, build-1019d 35 ms); the worker writes from it, and the next snapshot waits for that
            var buffer = _recoveryBuffer;
            // fix-171: the main thread writes the cheap sections and copies the heavy ones (npcs: ISaveCapture); the worker
            // encodes the copies and assembles the file (the same bytes as a normal save)
            var pending = _session.PrepareSave(title, DateTime.UtcNow, _recoveryBodies, _recoverySections);
            watch.Stop();
            LastRecoveryMs = watch.Elapsed.TotalMilliseconds;
            LastRecoverySections = RecoverySectionsText(_recoverySections, LastRecoveryMs, titleMs, GC.CollectionCount(0) - gcBefore);
            string sections = LastRecoverySections;
            RecoveryCount++;
            _recovery.Saved(Time.unscaledTime);
            var slots = _slots;
            double serializeMs = LastRecoveryMs;
            _recoveryWrite = Task.Run(() =>
            {
                var write = Stopwatch.StartNew();
                try
                {
                    buffer.SetLength(0);
                    pending.WriteTo(buffer);
                    int length = (int)buffer.Length;
                    LastRecoveryBytes = length;
                    double encodeMs = write.Elapsed.TotalMilliseconds;
                    slots.WriteBytes(CrashRecovery.Slot, buffer.GetBuffer(), length);
                    Debug.Log($"[Myrtana] recovery snapshot {length / 1024} KB: serialized in {serializeMs:0.0} ms (main thread; {sections}), " +
                              $"encoded {encodeMs:0.0} ms + written {write.Elapsed.TotalMilliseconds - encodeMs:0.0} ms (worker)");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"[Myrtana] recovery snapshot not written: {ex.Message}");
                }
            });
            return true;
        }

        readonly List<KeyValuePair<string, double>> _recoverySections = new List<KeyValuePair<string, double>>(64);
        /// <summary>fix-166: the snapshot's serialization buffer, reused (grows once to the save's size).</summary>
        readonly MemoryStream _recoveryBuffer = new MemoryStream(1 << 20);
        /// <summary>fix-171: the main thread's section bodies of the snapshot (<see cref="GameSession.PrepareSave"/>), reused.</summary>
        readonly MemoryStream _recoveryBodies = new MemoryStream(1 << 20);

        /// <summary>fix-166: the last snapshot's slowest sections (ms) and the GCs during it, for the log and the runner.</summary>
        public string LastRecoverySections { get; private set; } = "-";

        /// <summary>
        /// "slowest npcs 3.1, rules.items 0.8, rules.hero 0.1; other sections 0.3, title 0.1, outside 0.4; gc 0" — the three
        /// slowest sections, the other sections summed, the slot title (<see cref="SaveTitle.Of"/>), the rest (header, buffer).
        /// </summary>
        static string RecoverySectionsText(List<KeyValuePair<string, double>> sections, double totalMs, double titleMs, int gcs)
        {
            sections.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder("slowest ");
            double listed = 0;
            for (int i = 0; i < sections.Count && i < 3; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(sections[i].Key).Append(' ').Append(sections[i].Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                listed += sections[i].Value;
            }
            double sum = 0;
            foreach (var kv in sections) sum += kv.Value;
            sb.Append("; other sections ").Append((sum - listed).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
              .Append(", title ").Append(titleMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
              .Append(", outside ").Append(Math.Max(0, totalMs - sum - titleMs).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
              .Append("; gc ").Append(gcs);
            return sb.ToString();
        }

        /// <summary>Waits for a snapshot still being written (runner checks); true when none is pending.</summary>
        public bool WaitRecoveryWritten(int milliseconds) => _recoveryWrite == null || _recoveryWrite.Wait(milliseconds);

        /// <summary>
        /// fix-155: the hidden snapshot's save time (UTC) when the title should offer it (<see cref="CrashRecovery.ShouldOffer"/>:
        /// option on, the last run ended unclean, the snapshot newer than the newest normal save), else null. Asked once a run.
        /// </summary>
        public DateTime? RecoveryOffer()
        {
            bool unclean = UncleanExit || SimulateUncleanExit;
            if (_recoveryAnswered || !unclean) return null;
            var at = _slots.SavedAt(CrashRecovery.Slot);
            return CrashRecovery.ShouldOffer(CrashRecoveryEnabled, unclean, at, CrashRecovery.NewestNormal(_slots.List())) ? at : null;
        }

        /// <summary>
        /// fix-155, Yes: the snapshot becomes the visible slot <see cref="CrashRecovery.RecoveredSlot"/> ("Восстановление") and is
        /// loaded; the hidden one goes. False (G3's load-failed message) when it cannot be read.
        /// </summary>
        public bool RestoreRecovery()
        {
            _recoveryAnswered = true;
            try
            {
                foreach (var bytes in _slots.Versions(CrashRecovery.Slot))
                {
                    _slots.WriteBytes(CrashRecovery.RecoveredSlot, bytes);
                    _slots.WriteThumbnail(CrashRecovery.RecoveredSlot, null);
                    DropThumbnail(CrashRecovery.RecoveredSlot);
                    if (!TryLoad(CrashRecovery.RecoveredSlot)) continue;
                    _slots.Delete(CrashRecovery.Slot);
                    Debug.Log($"[Myrtana] crash recovery: restored into {CrashRecovery.RecoveredSlot}");
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Myrtana] crash recovery: {ex.Message}");
            }
            _messages.Show(Text(LoadFailedText), NoticeKind.Error);
            return false;
        }

        /// <summary>fix-155, No: the snapshot is discarded.</summary>
        public void DiscardRecovery()
        {
            _recoveryAnswered = true;
            try
            {
                _slots.Delete(CrashRecovery.Slot);
                Debug.Log("[Myrtana] crash recovery: declined, the recovery save was discarded");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Myrtana] crash recovery: {ex.Message}");
            }
        }

        public void QuickSave()
        {
            if (TrySave(SaveSlots.QuickSlot)) _messages.Show(Text(SavedText));
            else _messages.Show(Text(SaveFailedText), NoticeKind.Error);
        }

        public void QuickLoad()
        {
            string slot = QuickLoadSlot;
            if (slot != null) Load(slot);
        }

        /// <summary>Every readable save with its header (menu lists; fix-222: from the index, <paramref name="rescan"/> lists the directory first).</summary>
        public List<KeyValuePair<string, SaveHeader>> Slots(bool rescan = false) => _slots.List(rescan);

        /// <summary>Saves to a named slot ("save003"); false (nothing written) while a blocker forbids saving.</summary>
        public bool SaveTo(string slot)
        {
            if (!TrySave(slot)) return false;
            _messages.Show(Text(SavedText));
            return true;
        }

        /// <summary>Replaces the running game by the slot's save; false (with G3's message) when it cannot be read.</summary>
        public bool Load(string slot)
        {
            if (TryLoad(slot)) return true;
            _messages.Show(Text(LoadFailedText), NoticeKind.Error);
            return false;
        }

        /// <summary>The saves the main menu's Continue tries, newest first (fix-138, <see cref="ContinueGame"/>).</summary>
        public IReadOnlyList<string> ContinueSlots(bool rescan = false) => ContinueGame.Candidates(_slots.List(rescan));

        /// <summary>
        /// fix-138: loads the newest save of any kind; a corrupt or incompatible one is skipped for the next newest. False
        /// (no message: the menu tells) when none loads.
        /// </summary>
        public bool LoadNewest()
        {
            foreach (var slot in ContinueSlots(rescan: true)) // fix-222: the click checks the disk, the button showed the index
            {
                if (!TryLoad(slot)) continue;
                Debug.Log($"[Myrtana] continue: loaded {slot}");
                return true;
            }
            return false;
        }

        /// <summary>Loads the slot's file, or its backup when the file is damaged or fails to load (fix-155).</summary>
        bool TryLoad(string slot)
        {
            try
            {
                foreach (var bytes in _slots.Versions(slot))
                {
                    try
                    {
                        _game.LoadGame(bytes);
                        return true;
                    }
                    catch (Exception ex) when (IsUnreadable(ex))
                    {
                        Debug.LogWarning($"[Myrtana] loading {slot} failed: {ex.Message}");
                    }
                }
                Debug.LogWarning($"[Myrtana] loading {slot} failed: no whole version of the file");
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                Debug.LogWarning($"[Myrtana] loading {slot} failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// A save that cannot be read: missing or locked file, wrong format, truncated, or a damaged section whose reader
        /// meets values out of range (fix-138: Continue then falls back to the next save).
        /// </summary>
        static bool IsUnreadable(Exception ex) =>
            ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException ||
            ex is FormatException || ex is OverflowException || ex is IndexOutOfRangeException || ex is KeyNotFoundException;

        public void Delete(string slot)
        {
            _slots.Delete(slot);
            DropThumbnail(slot);
        }

        readonly Dictionary<string, Texture2D> _thumbnails = new Dictionary<string, Texture2D>();
        // fix-222: pictures are read on a worker thread and decoded a few per frame (the list shows at once, they follow)
        readonly Dictionary<string, int> _thumbnailRequests = new Dictionary<string, int>(); // slot -> generation in flight
        readonly System.Collections.Concurrent.ConcurrentQueue<(string slot, int generation, byte[] jpeg)> _thumbnailsRead =
            new System.Collections.Concurrent.ConcurrentQueue<(string, int, byte[])>();
        int _thumbnailGeneration;
        /// <summary>Most pictures decoded in one frame, and the time after which a frame stops decoding.</summary>
        const int ThumbnailsPerFrame = 8;
        const double ThumbnailFrameMs = 3.0;

        /// <summary>A save's picture arrived (<see cref="Thumbnail"/> now has it; null texture: the save has none).</summary>
        public event Action<string> ThumbnailReady;

        /// <summary>
        /// The save's picture for the save/load list (cached until the slot is written or deleted), or null: none, or not
        /// read yet. fix-222: a picture not read yet is requested (worker thread) and <see cref="ThumbnailReady"/> follows.
        /// </summary>
        public Texture2D Thumbnail(string slot)
        {
            if (_thumbnails.TryGetValue(slot, out var cached)) return cached;
            RequestThumbnail(slot);
            return null;
        }

        /// <summary>The picture is known (decoded, or the save has none).</summary>
        public bool HasThumbnail(string slot) => _thumbnails.ContainsKey(slot);

        void RequestThumbnail(string slot)
        {
            if (string.IsNullOrEmpty(slot) || _thumbnails.ContainsKey(slot) || _thumbnailRequests.ContainsKey(slot)) return;
            int generation = ++_thumbnailGeneration;
            _thumbnailRequests[slot] = generation;
            var slots = _slots;
            Task.Run(() =>
            {
                byte[] bytes = null;
                try { bytes = slots.ReadThumbnail(slot); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                _thumbnailsRead.Enqueue((slot, generation, bytes));
            });
        }

        /// <summary>Turns read pictures into textures, a few per frame.</summary>
        void DecodeThumbnails()
        {
            if (_thumbnailsRead.IsEmpty) return;
            var watch = Stopwatch.StartNew();
            for (int n = 0; n < ThumbnailsPerFrame && watch.Elapsed.TotalMilliseconds < ThumbnailFrameMs &&
                            _thumbnailsRead.TryDequeue(out var read); n++)
            {
                // a slot written or deleted since the request asks again; this result is stale
                if (!_thumbnailRequests.TryGetValue(read.slot, out int generation) || generation != read.generation) continue;
                _thumbnailRequests.Remove(read.slot);
                Texture2D texture = null;
                if (read.jpeg != null)
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "save " + read.slot };
                    if (!texture.LoadImage(read.jpeg, markNonReadable: true))
                    {
                        Destroy(texture);
                        texture = null;
                    }
                }
                _thumbnails[read.slot] = texture;
                ThumbnailReady?.Invoke(read.slot);
            }
        }

        void DropThumbnail(string slot)
        {
            _thumbnailRequests.Remove(slot);
            if (!_thumbnails.TryGetValue(slot, out var texture)) return;
            if (texture != null) Destroy(texture);
            _thumbnails.Remove(slot);
        }

        /// <summary>Writes the autosave now (also the runner's <c>autosave</c> op); false while a blocker forbids saving.</summary>
        public bool Autosave() => TrySave(SaveSlots.AutoSlot);

        /// <summary>Real time from which the next autosave is due (runner checks).</summary>
        public AutosaveSchedule AutosaveTimer => _autosave;

        bool TrySave(string slot)
        {
            if (_session.SaveBlockReason != null) return false;
            var session = _session;
            var watch = Stopwatch.StartNew();
            // fix-251 ("froze again after saving"): the main thread only snapshots the game (the cheap sections written,
            // the heavy ones copied: GameSession.PrepareSave, as the crash-recovery snapshot since fix-171) and renders the
            // picture; encoding, the atomic write with its backup, the index and the JPEG run on the slots' write queue.
            // Before, the whole save (encode + write + flush: 45-230 ms) and the JPEG encode (35-90 ms) ran in one frame.
            double waitMs = 0;
            if (_slots.Writing)
            {
                _slots.WaitForWrites("previous save");   // the reused buffers are still being written (two saves in a row)
                waitMs = watch.Elapsed.TotalMilliseconds;
            }
            _saveSections.Clear();
            int gcBefore = GC.CollectionCount(0);
            string title = SaveTitle.Of(session);
            var pending = session.PrepareSave(title, DateTime.UtcNow, _saveBodies, _saveSections);
            double prepareMs = watch.Elapsed.TotalMilliseconds - waitMs;
            _slots.Expect(slot, pending.Header);   // the save list shows it before the worker has written it
            var pixels = SaveThumbnail.CapturePixels(Camera.main, out double renderMs);
            int gcs = GC.CollectionCount(0) - gcBefore;
            DropThumbnail(slot);
            _autosave.Saved(Time.unscaledTime, slot == SaveSlots.AutoSlot); // fix-112: only the autosave moves the schedule
            LastWritten = slot;
            _recovery.Saved(Time.unscaledTime); // fix-155: a normal save is newer than any snapshot; the next one an interval later
            if (slot == SaveSlots.AutoSlot) _dueLogged = false;
            double mainMs = watch.Elapsed.TotalMilliseconds;
            G3.Unity.G3HitchLog.Mark($"save {slot} {mainMs:0} ms main thread");   // fix-251
            var slots = _slots;
            var buffer = _saveBuffer;
            string path = _slots.PathOf(slot);
            string sections = RecoverySectionsText(_saveSections, prepareMs, 0, gcs);
            _slots.Enqueue(() =>
            {
                var work = Stopwatch.StartNew();
                try
                {
                    buffer.SetLength(0);
                    pending.WriteTo(buffer);
                    int length = (int)buffer.Length;
                    double encodeMs = work.Elapsed.TotalMilliseconds;
                    slots.WriteBytes(slot, buffer.GetBuffer(), length);
                    double writeMs = work.Elapsed.TotalMilliseconds - encodeMs;
                    var jpeg = SaveThumbnail.Encode(pixels);
                    double jpegMs = work.Elapsed.TotalMilliseconds - encodeMs - writeMs;
                    slots.WriteThumbnail(slot, jpeg);
                    _mainThread.Enqueue(() => DropThumbnail(slot)); // the list re-reads the new picture
                    Debug.Log($"[Myrtana] saved to {path} ({length / 1024} KB): main thread {mainMs:0.0} ms (wait {waitMs:0.0}, snapshot {prepareMs:0.0}: " +
                              $"{sections}; picture render {renderMs:0.0}); worker: encode {encodeMs:0.0} + write {writeMs:0.0} + jpeg {jpegMs:0.0} " +
                              $"+ picture write {work.Elapsed.TotalMilliseconds - encodeMs - writeMs - jpegMs:0.0} ms");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Myrtana] save {path} not written: {ex.Message}");
                    _mainThread.Enqueue(() => _messages?.Show(Text(SaveFailedText), NoticeKind.Error));
                }
            });
            return true;
        }

        /// <summary>fix-251: the normal saves' snapshot buffers (reused; a save waits while the previous one is written).</summary>
        readonly MemoryStream _saveBodies = new MemoryStream(1 << 20), _saveBuffer = new MemoryStream(1 << 20);
        readonly List<KeyValuePair<string, double>> _saveSections = new List<KeyValuePair<string, double>>(64);
        /// <summary>fix-251: work the save queue hands back to the main thread (picture cache, failure notice).</summary>
        readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThread = new System.Collections.Concurrent.ConcurrentQueue<Action>();

        void RunMainThreadWork()
        {
            while (_mainThread.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        string Text(TextId id) => _session != null ? _session.Localizer.Text(id) : id.Value;
    }
}
