// tray_native.cs — Pre-compiled native/P-Invoke types for tray.ps1
//
// Replaces 5 inline Add-Type blocks that previously ran csc.exe at every
// app launch (cost: 10-25 s per startup). Build step in _full_rebuild.ps1
// compiles this to tray_native.dll once; tray.ps1 loads the DLL in ~50 ms.
//
// Types included:
//   MFM_Shell          — shell32: SetCurrentProcessExplicitAppUserModelID
//   MFM_MenuNative     — dwmapi/gdi32/user32: rounded corners, foreground focus
//   NativeMethods.GuiRes   — user32: GetGuiResources (GDI/User handle monitoring)
//   MasterFM.Win32Windows  — EnumWindows helpers (WMP window enumeration)
//   MasterFM.AudioPeak     — Core Audio peak-value detection (soundcloud-rpc pause)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// ── MFM_Shell ────────────────────────────────────────────────────────────────
// Sets the AppUserModelID so Windows groups the tray under the correct taskbar
// entry and Task Manager row.
public static class MFM_Shell {
    [DllImport("shell32.dll", PreserveSig = false)]
    public static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);
}

// ── MFM_MenuNative ───────────────────────────────────────────────────────────
// Rounded corners + foreground focus for the custom tray menu form.
public static class MFM_MenuNative {
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
    [DllImport("user32.dll")]
    public static extern bool SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
}

// ── NativeMethods.GuiRes ─────────────────────────────────────────────────────
// GDI/User object count monitoring (slow-leak canary).
namespace NativeMethods {
    public static class GuiRes {
        [DllImport("user32.dll")]
        public static extern int GetGuiResources(IntPtr hProcess, uint uiFlags);
    }
}

// ── MasterFM.Win32Windows + MasterFM.AudioPeak ──────────────────────────────
namespace MasterFM {

    // EnumWindows helpers — finds every top-level HWND for wmplayer.exe and
    // for the soundcloud-rpc pause-override scanner.
    public static class Win32Windows {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int  GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static List<IntPtr> GetProcessWindows(uint pid) {
            var list = new List<IntPtr>();
            EnumWindows((h, l) => {
                uint wpid; GetWindowThreadProcessId(h, out wpid);
                if (wpid == pid) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static List<string> GetAllVisibleTitles() {
            var list = new List<string>();
            EnumWindows((h, l) => {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                var t = sb.ToString();
                if (!string.IsNullOrEmpty(t)) list.Add(t);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static string GetTitle(IntPtr h) {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            return sb.ToString();
        }

        public static string GetClass(IntPtr h) {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, 256);
            return sb.ToString();
        }
    }

    // Core Audio peak-value detection — used to detect soundcloud-rpc play/pause
    // state since it never updates SMTC PlaybackStatus.
    public static class AudioPeak {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection {
            [PreserveSig] int GetCount(out uint pcDevices);
            [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                                       [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionManager2 {
            int NotImpl_GetAudioSessionControl();
            int NotImpl_GetSimpleAudioVolume();
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEnumerator {
            [PreserveSig] int GetCount(out int SessionCount);
            [PreserveSig] int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl {
            int NotImpl_GetState();
            int NotImpl_GetDisplayName();
            int NotImpl_SetDisplayName();
            int NotImpl_GetIconPath();
            int NotImpl_SetIconPath();
            int NotImpl_GetGroupingParam();
            int NotImpl_SetGroupingParam();
            int NotImpl_RegisterAudioSessionNotification();
            int NotImpl_UnregisterAudioSessionNotification();
        }

        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionControl2 {
            int NotImpl_0(); int NotImpl_1(); int NotImpl_2();
            int NotImpl_3(); int NotImpl_4(); int NotImpl_5();
            int NotImpl_6(); int NotImpl_7(); int NotImpl_8();
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetProcessId(out uint pRetVal);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioMeterInformation {
            [PreserveSig] int GetPeakValue(out float pfPeak);
        }

        public static float GetPeakForProcessName(string nameContains) {
            try {
                var enumer = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                IMMDeviceCollection col;
                if (enumer.EnumAudioEndpoints(0, 1, out col) != 0 || col == null) return -1f;
                uint ndev; col.GetCount(out ndev);
                Guid iidMgr = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                float best = -1f;
                for (uint d = 0; d < ndev; d++) {
                    IMMDevice dev;
                    if (col.Item(d, out dev) != 0 || dev == null) continue;
                    object mgrObj;
                    if (dev.Activate(ref iidMgr, 1, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null) continue;
                    var mgr = (IAudioSessionManager2)mgrObj;
                    IAudioSessionEnumerator sessions;
                    if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                    int count; sessions.GetCount(out count);
                    for (int i = 0; i < count; i++) {
                        IAudioSessionControl ctl;
                        if (sessions.GetSession(i, out ctl) != 0 || ctl == null) continue;
                        var ctl2 = ctl as IAudioSessionControl2;
                        if (ctl2 == null) continue;
                        uint sessPid;
                        if (ctl2.GetProcessId(out sessPid) != 0) continue;
                        if (sessPid == 0) continue;
                        string pname = "";
                        try {
                            var p = System.Diagnostics.Process.GetProcessById((int)sessPid);
                            pname = p.ProcessName;
                        } catch { continue; }
                        if (pname.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var meter = ctl as IAudioMeterInformation;
                        if (meter == null) continue;
                        float peak;
                        if (meter.GetPeakValue(out peak) != 0) continue;
                        if (peak > best) best = peak;
                    }
                }
                return best;
            } catch { return -1f; }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MasterFM.SMTC — Event-driven SMTC watcher (v12.0.0)
//
// Subscribes to GlobalSystemMediaTransportControlsSessionManager events
// (SessionsChanged, CurrentSessionChanged) plus per-session events
// (MediaPropertiesChanged, PlaybackInfoChanged, TimelinePropertiesChanged) and
// maintains a thread-safe per-SAUMID snapshot dictionary plus a change-event
// queue. PowerShell's tick handler reads the snapshot directly and drains the
// queue once per tick — no synchronous SMTC ALPC calls needed in steady state.
//
// All WinRT type binding is done via reflection so this DLL doesn't need
// compile-time references to Windows.winmd. The PowerShell host already loads
// the WinRT types into the AppDomain via [Type, Assembly, ContentType=WindowsRuntime];
// reflection finds them at runtime.
// ─────────────────────────────────────────────────────────────────────────────
namespace MasterFM.SMTC {
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Threading;

    public class SMTCSessionSnapshot {
        public string Saumid;
        public string PlaybackStatusName;
        public int    PlaybackStatusValue;
        public long   PositionMs;
        public long   DurationMs;
        public long   LastUpdatedTimeUtcTicks;
        public string Title;
        public string Artist;
        public string AlbumTitle;
        public string AlbumArtist;
        // 0=Unknown, 1=Music, 2=Video, 3=Image. -1 means "not yet read".
        // Honest desktop apps (Netflix, VLC, Films & TV) set this to 2 for
        // video content; SmtcEventBridge.IsLikelyVideoContent uses this to
        // suppress movie/TV titles leaking into the now-playing display.
        // Chrome lies and reports everything as Music, so a title-prefix
        // fallback also exists in the bridge.
        public int    PlaybackType = -1;
        public object MediaPropertiesRcw;
        public object PlaybackInfoRcw;
        public object TimelinePropertiesRcw;
        public object SessionRef;
        public bool   IsPauseEnabledRaw;
        public bool   IsPlayEnabledRaw;
        public bool   HasMediaProps;
        public bool   HasPlaybackInfo;
        public bool   HasTimeline;
        public DateTime SnapshotUtc;
        // v12.0.0: rate-limit anchor ticks for event-driven ALPC reads.
        // Multiple WinRT events fire in rapid succession during a track change
        // (8+ PlaybackInfoChanged in <100ms is normal). Reading state for each
        // would defeat the whole architecture. Coalesce to one read per 100ms.
        public long   LastPlaybackReadTicks;
        public long   LastTimelineReadTicks;
        public long   LastMediaPropsReadTicks;
    }

    public enum SMTCEventKind {
        SessionAdded             = 1,
        SessionRemoved           = 2,
        PlaybackInfoChanged      = 3,
        MediaPropertiesChanged   = 4,
        TimelinePropertiesChanged= 5,
        CurrentSessionChanged    = 6,
        InitialEnumeration       = 7
    }

    public class SMTCChangeRecord {
        public SMTCEventKind Kind;
        public string Saumid;
        public DateTime UtcTime;
    }

    // Wraps a single WinRT event subscription. WinRT events can't be detached
    // via EventInfo.RemoveEventHandler (CLR throws "Adding or removing event
    // handlers dynamically is not supported on WinRT events"), so we capture
    // the EventRegistrationToken returned by add_X and call remove_X with it.
    internal class EventBinding {
        public Delegate   Handler;
        public object     Token;
        public MethodInfo RemoveMethod;
        public object     Source;
    }

    internal class SessionSubs {
        public object       Session;
        public string       Saumid;
        public EventBinding MediaHandler;
        public EventBinding PlaybackHandler;
        public EventBinding TimelineHandler;
    }

    public sealed class SMTCWatcher : IDisposable {
        private readonly ConcurrentDictionary<string, SMTCSessionSnapshot> _snaps =
            new ConcurrentDictionary<string, SMTCSessionSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<SMTCChangeRecord> _events =
            new ConcurrentQueue<SMTCChangeRecord>();
        private readonly ConcurrentDictionary<string, SessionSubs> _subs =
            new ConcurrentDictionary<string, SessionSubs>(StringComparer.OrdinalIgnoreCase);

        private object       _mgr;
        private EventBinding _mgrSessionsChangedBinding;
        private EventBinding _mgrCurrentSessionChangedBinding;
        private long     _eventsTotal;
        private long     _lastEventTicks;
        private string   _currentSaumid;
        private readonly object _initLock = new object();
        private volatile bool   _initialized;
        private volatile bool   _disposed;
        private string   _logPath;
        // Coalesce SessionsChanged: multiple events within 750ms collapse to one re-enumeration.
        private long _enumerateDeadlineTicks;
        private int  _enumerateInFlight;
        // Rate-limit per-session event-handler reads. 250ms is enough to absorb a
        // burst of 5-10 PlaybackInfoChanged events while keeping perceived latency low.
        private const long ReadCoolMs = 250;
        // Burst suppression: when SessionsChanged or CurrentSessionChanged fires, set a
        // window during which ALL ALPC reads (per-session AND manager-level) are skipped.
        // After the burst settles, the coalesced enumerate runs and re-syncs everything.
        // This removes the dominant remaining FPS-drop source during rapid track skipping.
        private long _burstUntilTicks;
        private const long BurstWindowMs = 800;
        private long _lastCurrentSessionReadTicks;

        private bool InBurstWindow() {
            long until = Interlocked.Read(ref _burstUntilTicks);
            return until != 0 && DateTime.UtcNow.Ticks < until;
        }
        private void ExtendBurstWindow() {
            long newUntil = DateTime.UtcNow.AddMilliseconds(BurstWindowMs).Ticks;
            // Only push the deadline forward, never back
            long cur;
            do { cur = Interlocked.Read(ref _burstUntilTicks); }
            while (cur < newUntil &&
                   Interlocked.CompareExchange(ref _burstUntilTicks, newUntil, cur) != cur);
        }

        public long EventsReceivedTotal {
            get { return Interlocked.Read(ref _eventsTotal); }
        }
        public DateTime LastEventUtc {
            get {
                long t = Interlocked.Read(ref _lastEventTicks);
                return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
            }
        }
        public string CurrentSaumid { get { return _currentSaumid; } }
        public int    SessionCount  { get { return _snaps.Count; } }

        // ── Initialization ──────────────────────────────────────────────────
        public void Initialize(object manager) {
            if (manager == null) throw new ArgumentNullException("manager");
            try {
                _logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MastersFM", "smtc_watcher.log");
                try { File.WriteAllText(_logPath,
                    string.Format("=== SMTCWatcher init {0:yyyy-MM-dd HH:mm:ss} ==={1}",
                        DateTime.Now, Environment.NewLine)); } catch { }
            } catch { }

            lock (_initLock) {
                if (_initialized) return;
                _mgr = manager;
                Type mgrT = manager.GetType();

                // Subscribe to manager-level events FIRST so we don't miss session adds
                _mgrSessionsChangedBinding = AttachEvent(mgrT, manager, "SessionsChanged",
                    new Action<object, object>(OnSessionsChanged));
                _mgrCurrentSessionChangedBinding = AttachEvent(mgrT, manager, "CurrentSessionChanged",
                    new Action<object, object>(OnCurrentSessionChanged));

                // Initial session enumeration + per-session subscribe
                EnumerateAndSubscribeSessions(mgrT);

                // Initial current session
                try {
                    object cur = mgrT.GetMethod("GetCurrentSession").Invoke(manager, null);
                    if (cur != null) {
                        _currentSaumid = GetSaumidSafe(cur);
                    }
                } catch (Exception ex) { SafeLog("init GetCurrentSession: " + ex.Message); }

                _initialized = true;
                EnqueueEvent(SMTCEventKind.InitialEnumeration, null);
                SafeLog(string.Format("Initialized: {0} sessions, current='{1}'",
                    _snaps.Count, _currentSaumid ?? ""));
            }
        }

        private void EnumerateAndSubscribeSessions(Type mgrT) {
            object sessionsList;
            try {
                sessionsList = mgrT.GetMethod("GetSessions").Invoke(_mgr, null);
            } catch (Exception ex) { SafeLog("GetSessions: " + ex.Message); return; }
            if (sessionsList == null) return;

            // sessionsList is IReadOnlyList<GlobalSystemMediaTransportControlsSession>
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            System.Collections.IEnumerable e = sessionsList as System.Collections.IEnumerable;
            if (e == null) return;
            foreach (object s in e) {
                if (s == null) continue;
                string saumid = GetSaumidSafe(s);
                if (string.IsNullOrEmpty(saumid)) continue;
                current.Add(saumid);
                SessionSubs existing;
                if (_subs.TryGetValue(saumid, out existing)) {
                    // Same SAUMID but possibly a different session OBJECT (soundcloud-rpc
                    // recycles its session on every track change). If the RCW identity
                    // differs, the old subscription is dead and needs to be replaced.
                    if (!ReferenceEquals(existing.Session, s)) {
                        UnsubscribeFromSession(saumid, fromSessionsChanged: false);
                        SubscribeToSession(s, saumid);
                    }
                    // else: same RCW, keep subscription as-is
                } else {
                    SubscribeToSession(s, saumid);
                }
            }
            // Detect removals
            foreach (var key in new List<string>(_subs.Keys)) {
                if (!current.Contains(key)) {
                    UnsubscribeFromSession(key, fromSessionsChanged: true);
                }
            }
        }

        private void SubscribeToSession(object session, string saumid) {
            try {
                Type sessT = session.GetType();
                var subs = new SessionSubs { Session = session, Saumid = saumid };
                // Attach events first — this is just delegate registration (microseconds)
                subs.MediaHandler = AttachEvent(sessT, session, "MediaPropertiesChanged",
                    new Action<object, object>(OnMediaPropertiesChanged));
                subs.PlaybackHandler = AttachEvent(sessT, session, "PlaybackInfoChanged",
                    new Action<object, object>(OnPlaybackInfoChanged));
                subs.TimelineHandler = AttachEvent(sessT, session, "TimelinePropertiesChanged",
                    new Action<object, object>(OnTimelinePropertiesChanged));
                _subs[saumid] = subs;

                // Initialize an empty snapshot — events will populate state.
                var snap = _snaps.GetOrAdd(saumid, k => new SMTCSessionSnapshot { Saumid = k });
                snap.SessionRef = session;
                snap.SnapshotUtc = DateTime.UtcNow;

                // v12.0.0 hot-path fix: avoid synchronous SMTC ALPC reads on the
                // subscribe path while soundcloud-rpc is still updating session state.
                // Defer initial-state capture to a thread-pool task with a 200ms grace
                // period — by then soundcloud-rpc has finished writing and the service
                // is idle. The events that fire in the meantime (rate-limited) update
                // state organically; PS-side fallback handles the brief cold-cache window.
                System.Threading.Tasks.Task.Run((Action)(() => {
                    try {
                        Thread.Sleep(200);
                        if (_disposed) return;
                        // Re-check the session is still in our subscription set (might have
                        // been replaced during the 200ms wait if soundcloud-rpc churned again).
                        SessionSubs cur;
                        if (!_subs.TryGetValue(saumid, out cur) || cur.Session != session) return;
                        // Capture cheap state (PlaybackInfo + Timeline) so PS fast-path is hot
                        CapturePlaybackInfo(session, snap);
                        CaptureTimeline(session, snap);
                        // MediaProperties is a roundtrip — capture it too, but do it last
                        object props = FetchMediaPropsSync(session, 1500);
                        if (props != null) {
                            ApplyMediaProperties(snap, props);
                            EnqueueEvent(SMTCEventKind.MediaPropertiesChanged, saumid);
                        }
                    } catch (Exception ex) {
                        SafeLog("deferred init [" + saumid + "]: " + ex.Message);
                    }
                }));

                EnqueueEvent(SMTCEventKind.SessionAdded, saumid);
                SafeLog("Subscribed to session: " + saumid);
            } catch (Exception ex) {
                SafeLog("SubscribeToSession [" + saumid + "]: " + ex.Message);
            }
        }

        private void UnsubscribeFromSession(string saumid, bool fromSessionsChanged) {
            SessionSubs subs;
            if (!_subs.TryRemove(saumid, out subs)) return;
            DetachBinding(subs.MediaHandler);
            DetachBinding(subs.PlaybackHandler);
            DetachBinding(subs.TimelineHandler);

            SMTCSessionSnapshot dummy;
            if (fromSessionsChanged) {
                _snaps.TryRemove(saumid, out dummy);
                EnqueueEvent(SMTCEventKind.SessionRemoved, saumid);
                SafeLog("Unsubscribed session: " + saumid);
            }
        }

        // ── Event handlers ──────────────────────────────────────────────────
        private void OnSessionsChanged(object sender, object args) {
            try {
                Bump();
                ExtendBurstWindow();
                if (_disposed || !_initialized) return;
                // Coalesce: extend deadline; if no worker running, start one.
                // 750ms catches rapid-skip bursts (user clicks every <1s) into one
                // enumeration. After the burst stops, work fires once. Trade-off:
                // metadata-update latency on a single skip is up to 750ms (acceptable
                // because OBS/Discord display feels instant under 1s).
                long newDeadline = DateTime.UtcNow.AddMilliseconds(750).Ticks;
                Interlocked.Exchange(ref _enumerateDeadlineTicks, newDeadline);
                if (Interlocked.CompareExchange(ref _enumerateInFlight, 1, 0) == 0) {
                    System.Threading.Tasks.Task.Run((Action)(() => {
                        try {
                            // Wait until deadline stops moving
                            while (true) {
                                long deadline = Interlocked.Read(ref _enumerateDeadlineTicks);
                                long now = DateTime.UtcNow.Ticks;
                                if (now >= deadline) break;
                                int waitMs = (int)Math.Max(10,
                                    Math.Min(300, (deadline - now) / TimeSpan.TicksPerMillisecond));
                                Thread.Sleep(waitMs);
                            }
                            if (_disposed) return;
                            lock (_initLock) {
                                if (_disposed) return;
                                EnumerateAndSubscribeSessions(_mgr.GetType());
                            }
                        } catch (Exception ex) {
                            SafeLog("CoalescedEnumerate: " + ex.Message);
                        } finally {
                            Interlocked.Exchange(ref _enumerateInFlight, 0);
                        }
                    }));
                }
            } catch (Exception ex) { SafeLog("OnSessionsChanged: " + ex.Message); }
        }

        private void OnCurrentSessionChanged(object sender, object args) {
            try {
                Bump();
                ExtendBurstWindow();
                if (_disposed || _mgr == null) return;
                EnqueueEvent(SMTCEventKind.CurrentSessionChanged, _currentSaumid);
                // Skip the GetCurrentSession ALPC during a burst — the coalesced
                // enumerate at the end of the burst will refresh _currentSaumid.
                if (InBurstWindow()) return;
                if (RateLimitSkip(ref _lastCurrentSessionReadTicks, ReadCoolMs)) return;
                object cur = null;
                try { cur = _mgr.GetType().GetMethod("GetCurrentSession").Invoke(_mgr, null); }
                catch (Exception ex) { SafeLog("CurrentSession get: " + ex.Message); }
                _currentSaumid = (cur == null) ? null : GetSaumidSafe(cur);
            } catch (Exception ex) { SafeLog("OnCurrentSessionChanged: " + ex.Message); }
        }

        // Rate-limit helper: returns true if we should skip the ALPC read because
        // another read for this metric on this session happened <100ms ago.
        // Updates the anchor on success.
        private static bool RateLimitSkip(ref long anchorField, long coolMs) {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref anchorField);
            if (last != 0 && (now - last) < coolMs * TimeSpan.TicksPerMillisecond) return true;
            Interlocked.Exchange(ref anchorField, now);
            return false;
        }

        private void OnMediaPropertiesChanged(object sender, object args) {
            try {
                Bump();
                if (_disposed) return;
                string saumid = GetSaumidSafe(sender);
                if (string.IsNullOrEmpty(saumid)) return;
                var snap = _snaps.GetOrAdd(saumid, k => new SMTCSessionSnapshot { Saumid = k, SessionRef = sender });
                snap.SessionRef = sender;
                EnqueueEvent(SMTCEventKind.MediaPropertiesChanged, saumid);
                if (InBurstWindow()) return;
                if (RateLimitSkip(ref snap.LastMediaPropsReadTicks, ReadCoolMs)) return;
                object props = FetchMediaPropsSync(sender, 1500);
                if (props != null) ApplyMediaProperties(snap, props);
            } catch (Exception ex) { SafeLog("OnMediaPropertiesChanged: " + ex.Message); }
        }

        private void OnPlaybackInfoChanged(object sender, object args) {
            try {
                Bump();
                if (_disposed) return;
                string saumid = GetSaumidSafe(sender);
                if (string.IsNullOrEmpty(saumid)) return;
                var snap = _snaps.GetOrAdd(saumid, k => new SMTCSessionSnapshot { Saumid = k, SessionRef = sender });
                snap.SessionRef = sender;
                EnqueueEvent(SMTCEventKind.PlaybackInfoChanged, saumid);
                if (InBurstWindow()) return;
                if (RateLimitSkip(ref snap.LastPlaybackReadTicks, ReadCoolMs)) return;
                CapturePlaybackInfo(sender, snap);
            } catch (Exception ex) { SafeLog("OnPlaybackInfoChanged: " + ex.Message); }
        }

        private void OnTimelinePropertiesChanged(object sender, object args) {
            try {
                Bump();
                if (_disposed) return;
                string saumid = GetSaumidSafe(sender);
                if (string.IsNullOrEmpty(saumid)) return;
                var snap = _snaps.GetOrAdd(saumid, k => new SMTCSessionSnapshot { Saumid = k, SessionRef = sender });
                snap.SessionRef = sender;
                EnqueueEvent(SMTCEventKind.TimelinePropertiesChanged, saumid);
                if (InBurstWindow()) return;
                if (RateLimitSkip(ref snap.LastTimelineReadTicks, ReadCoolMs)) return;
                CaptureTimeline(sender, snap);
            } catch (Exception ex) { SafeLog("OnTimelinePropertiesChanged: " + ex.Message); }
        }

        // ── Snapshot capture helpers (these run off the STA, brief sync OK) ──
        private void CapturePlaybackInfo(object session, SMTCSessionSnapshot snap) {
            try {
                object info = session.GetType().GetMethod("GetPlaybackInfo").Invoke(session, null);
                if (info == null) return;
                snap.PlaybackInfoRcw = info;
                Type infoT = info.GetType();
                object status = infoT.GetProperty("PlaybackStatus").GetValue(info);
                snap.PlaybackStatusName  = status.ToString();
                try { snap.PlaybackStatusValue = (int)status; } catch { snap.PlaybackStatusValue = -1; }
                try {
                    object controls = infoT.GetProperty("Controls").GetValue(info);
                    if (controls != null) {
                        Type cT = controls.GetType();
                        try { snap.IsPauseEnabledRaw = (bool)cT.GetProperty("IsPauseEnabled").GetValue(controls); } catch { }
                        try { snap.IsPlayEnabledRaw  = (bool)cT.GetProperty("IsPlayEnabled").GetValue(controls);  } catch { }
                    }
                } catch { }
                snap.HasPlaybackInfo = true;
                snap.SnapshotUtc = DateTime.UtcNow;
            } catch (Exception ex) { SafeLog("CapturePlaybackInfo: " + ex.Message); }
        }

        private void CaptureTimeline(object session, SMTCSessionSnapshot snap) {
            try {
                object tl = session.GetType().GetMethod("GetTimelineProperties").Invoke(session, null);
                if (tl == null) return;
                snap.TimelinePropertiesRcw = tl;
                Type tlT = tl.GetType();
                try {
                    object posTs = tlT.GetProperty("Position").GetValue(tl);
                    snap.PositionMs = (long)((TimeSpan)posTs).TotalMilliseconds;
                } catch { }
                try {
                    object endTs = tlT.GetProperty("EndTime").GetValue(tl);
                    object startTs = tlT.GetProperty("StartTime").GetValue(tl);
                    long endMs = (long)((TimeSpan)endTs).TotalMilliseconds;
                    long startMs = (long)((TimeSpan)startTs).TotalMilliseconds;
                    snap.DurationMs = endMs - startMs;
                } catch { }
                try {
                    object lastUpd = tlT.GetProperty("LastUpdatedTime").GetValue(tl);
                    snap.LastUpdatedTimeUtcTicks = ((DateTimeOffset)lastUpd).UtcTicks;
                } catch { }
                snap.HasTimeline = true;
                snap.SnapshotUtc = DateTime.UtcNow;
            } catch (Exception ex) { SafeLog("CaptureTimeline: " + ex.Message); }
        }

        private void ApplyMediaProperties(SMTCSessionSnapshot snap, object props) {
            try {
                Type pT = props.GetType();
                try { snap.Title       = pT.GetProperty("Title").GetValue(props) as string ?? ""; } catch { }
                try { snap.Artist      = pT.GetProperty("Artist").GetValue(props) as string ?? ""; } catch { }
                try { snap.AlbumTitle  = pT.GetProperty("AlbumTitle").GetValue(props) as string ?? ""; } catch { }
                try { snap.AlbumArtist = pT.GetProperty("AlbumArtist").GetValue(props) as string ?? ""; } catch { }
                // PlaybackType is a Nullable<MediaPlaybackType> enum projected as
                // Nullable<int>. Reflection sees the underlying int when set,
                // null when the source didn't tag the session. Defense in depth
                // for the video-streaming filter (cf. SmtcEventBridge.IsLikelyVideoContent).
                try {
                    var ptVal = pT.GetProperty("PlaybackType").GetValue(props);
                    snap.PlaybackType = ptVal == null ? 0 : Convert.ToInt32(ptVal);
                } catch { snap.PlaybackType = -1; }
                snap.MediaPropertiesRcw = props;
                snap.HasMediaProps = true;
                snap.SnapshotUtc = DateTime.UtcNow;
            } catch (Exception ex) { SafeLog("ApplyMediaProperties: " + ex.Message); }
        }

        // Polled sync wait for IAsyncOperation<MediaProperties>. Called only off
        // the STA tick thread (event-handler thread or Task.Run); blocking briefly here is fine.
        private object FetchMediaPropsSync(object session, int timeoutMs) {
            try {
                MethodInfo m = session.GetType().GetMethod("TryGetMediaPropertiesAsync");
                if (m == null) return null;
                object asyncOp = m.Invoke(session, null);
                if (asyncOp == null) return null;
                Type aoType = asyncOp.GetType();
                PropertyInfo statusProp = aoType.GetProperty("Status");
                MethodInfo   resultsM   = aoType.GetMethod("GetResults");
                MethodInfo   cancelM    = aoType.GetMethod("Cancel");
                if (statusProp == null || resultsM == null) return null;
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline) {
                    object status = statusProp.GetValue(asyncOp);
                    string sn = status == null ? "" : status.ToString();
                    if (sn == "Completed") return resultsM.Invoke(asyncOp, null);
                    if (sn == "Canceled" || sn == "Error") return null;
                    Thread.Sleep(5);
                }
                if (cancelM != null) { try { cancelM.Invoke(asyncOp, null); } catch { } }
                return null;
            } catch (Exception ex) {
                SafeLog("FetchMediaPropsSync: " + ex.Message);
                return null;
            }
        }

        // ── Public reader API (called from PowerShell tick) ─────────────────
        public SMTCSessionSnapshot GetSnapshot(string saumid) {
            if (string.IsNullOrEmpty(saumid)) return null;
            SMTCSessionSnapshot s;
            return _snaps.TryGetValue(saumid, out s) ? s : null;
        }

        public string[] GetSaumids() {
            var list = new List<string>(_snaps.Keys);
            return list.ToArray();
        }

        public object[] GetSessions() {
            var list = new List<object>(_snaps.Count);
            foreach (var kv in _snaps) {
                if (kv.Value != null && kv.Value.SessionRef != null)
                    list.Add(kv.Value.SessionRef);
            }
            return list.ToArray();
        }

        public SMTCChangeRecord[] DrainEvents() {
            var list = new List<SMTCChangeRecord>(16);
            SMTCChangeRecord rec;
            while (_events.TryDequeue(out rec)) list.Add(rec);
            return list.ToArray();
        }

        // v14.2.3: force a fresh GetTimelineProperties read for a specific saumid +
        // enqueue a synthetic TimelinePropertiesChanged event if the position drifted
        // beyond the wall-clock-expected amount. Used by SmtcEventBridge to work
        // around Chromium not firing TimelinePropertiesChanged on every scrub --
        // it only re-broadcasts on play/pause/track-change, so rapid mid-playback
        // scrubs (Prime Video / Netflix / YouTube progress bar dragging) leave the
        // tray's anchor stale and the overlay clock shows the wrong position until
        // the next pause. Polling at 2 s cadence from the bridge restores sync.
        // Returns true when a synthetic event was enqueued.
        public bool ForceTimelinePoll(string saumid) {
            if (string.IsNullOrEmpty(saumid)) return false;
            SessionSubs subs;
            if (!_subs.TryGetValue(saumid, out subs) || subs == null || subs.Session == null) return false;
            SMTCSessionSnapshot snap;
            if (!_snaps.TryGetValue(saumid, out snap) || snap == null) return false;

            long prevPosMs   = snap.PositionMs;
            long prevUpdTicks = snap.LastUpdatedTimeUtcTicks;
            DateTime prevSnapAt = snap.SnapshotUtc;
            try {
                CaptureTimeline(subs.Session, snap);
            } catch (Exception ex) {
                SafeLog("ForceTimelinePoll CaptureTimeline: " + ex.Message);
                return false;
            }

            // Did the SMTC anchor itself advance (LastUpdatedTime moved) OR did the
            // raw position jump beyond what wall-clock drift would explain? Either
            // way the prior anchor is stale and the bridge should reprocess.
            long newPosMs    = snap.PositionMs;
            long newUpdTicks = snap.LastUpdatedTimeUtcTicks;
            bool anchorMoved = newUpdTicks > 0 && newUpdTicks != prevUpdTicks;

            // Positions reported in ms; allow up to 2500 ms of natural drift between
            // polls (2 s cadence + slop). Anything beyond that on the raw position
            // means a real scrub happened during the silence.
            long delta = Math.Abs(newPosMs - prevPosMs);
            bool jumped = delta > 2500;

            if (anchorMoved || jumped) {
                EnqueueEvent(SMTCEventKind.TimelinePropertiesChanged, saumid);
                return true;
            }
            return false;
        }

        // ── Plumbing ────────────────────────────────────────────────────────
        private void Bump() {
            Interlocked.Increment(ref _eventsTotal);
            Interlocked.Exchange(ref _lastEventTicks, DateTime.UtcNow.Ticks);
        }

        private void EnqueueEvent(SMTCEventKind kind, string saumid) {
            _events.Enqueue(new SMTCChangeRecord {
                Kind = kind, Saumid = saumid ?? "", UtcTime = DateTime.UtcNow
            });
        }

        private static string GetSaumidSafe(object session) {
            try {
                return (string)session.GetType().GetProperty("SourceAppUserModelId").GetValue(session);
            } catch { return null; }
        }

        // Build a runtime delegate of the right TypedEventHandler<,> type, then
        // call add_X(handler) directly via reflection (bypassing the CLR's
        // EventInfo.AddEventHandler dispatch which throws "Adding or removing
        // event handlers dynamically is not supported on WinRT events"). The
        // add accessor returns an EventRegistrationToken which we capture so
        // remove_X(token) can be called at Dispose time.
        private static EventBinding AttachEvent(Type sourceType, object source, string eventName,
                                                Action<object, object> handler) {
            EventInfo evt = sourceType.GetEvent(eventName);
            if (evt == null) throw new MissingMemberException(sourceType.FullName, eventName);
            Type delegateType = evt.EventHandlerType;
            MethodInfo invokeMI = delegateType.GetMethod("Invoke");
            ParameterInfo[] parms = invokeMI.GetParameters();
            var p1 = Expression.Parameter(parms[0].ParameterType, "s");
            var p2 = Expression.Parameter(parms[1].ParameterType, "a");
            Expression body;
            if (handler.Target != null) {
                body = Expression.Call(
                    Expression.Constant(handler.Target),
                    handler.Method,
                    Expression.Convert(p1, typeof(object)),
                    Expression.Convert(p2, typeof(object)));
            } else {
                body = Expression.Call(
                    handler.Method,
                    Expression.Convert(p1, typeof(object)),
                    Expression.Convert(p2, typeof(object)));
            }
            // Swallow handler exceptions so they never escape into the WinRT runtime.
            var ex = Expression.Parameter(typeof(Exception), "ex");
            var safe = Expression.TryCatch(
                Expression.Block(typeof(void), body),
                Expression.Catch(ex, Expression.Empty()));
            var lambda = Expression.Lambda(delegateType, safe, p1, p2);
            Delegate del = lambda.Compile();

            MethodInfo addM = evt.GetAddMethod();
            MethodInfo removeM = evt.GetRemoveMethod();
            if (addM == null || removeM == null) {
                throw new InvalidOperationException("Event " + eventName + " has no accessor methods.");
            }
            object token = addM.Invoke(source, new object[] { del });
            return new EventBinding {
                Handler      = del,
                Token        = token,
                RemoveMethod = removeM,
                Source       = source
            };
        }

        private static void DetachBinding(EventBinding b) {
            if (b == null || b.RemoveMethod == null || b.Source == null) return;
            try { b.RemoveMethod.Invoke(b.Source, new object[] { b.Token }); }
            catch { /* best-effort */ }
        }

        private void SafeLog(string msg) {
            try {
                if (string.IsNullOrEmpty(_logPath)) return;
                File.AppendAllText(_logPath,
                    string.Format("[{0:HH:mm:ss.fff}] {1}{2}",
                        DateTime.Now, msg, Environment.NewLine));
            } catch { }
        }

        // ── Disposal ────────────────────────────────────────────────────────
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try {
                lock (_initLock) {
                    foreach (var key in new List<string>(_subs.Keys)) {
                        UnsubscribeFromSession(key, fromSessionsChanged: false);
                    }
                    DetachBinding(_mgrSessionsChangedBinding);
                    DetachBinding(_mgrCurrentSessionChangedBinding);
                }
            } catch (Exception ex) { SafeLog("Dispose: " + ex.Message); }
        }
    }
}

