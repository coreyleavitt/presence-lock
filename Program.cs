using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Control;
using Windows.Media.FaceAnalysis;
using WinFormsTimer = System.Windows.Forms.Timer;
using EnclosurePanel = Windows.Devices.Enumeration.Panel;
using Core = PresenceLock.Core;

[assembly: InternalsVisibleTo("PresenceLock.Tests")]

namespace PresenceLock;

static class Program
{
    internal static Mutex? SingleInstance;

    [STAThread]
    static void Main()
    {
        // SEC-1 (reframed): `new Mutex(true, name, out createdNew)` grants initial ownership
        // only when createdNew is true; when false (a name collision), initiallyOwned is
        // ignored and no wait is performed -- so this constructor can never throw
        // AbandonedMutexException on this path (that exception is only possible from a WaitOne
        // call contending for an already-held mutex, and there is no such call site for
        // SingleInstance anywhere in this project: it is only ever constructed here, then later
        // ReleaseMutex()'d/Dispose()'d from RestartProcess). What WAS broken: `if (!createdNew)
        // return;` used to exit completely silently, before WatcherContext's constructor (the
        // first other Log.Write call site) ever ran -- indistinguishable, from the log, between
        // "a legitimate second instance politely declined to start" and "something is squatting
        // the mutex name and this process can never run at all." This app's threat model is
        // same-user local code (see CLAUDE.md); that can always squat a well-known mutex name,
        // and this fix does not attempt to stop it -- it only removes the silent, trace-less
        // failure. `Log` is a fully self-contained static class (its own file path, its own
        // Directory.CreateDirectory) with no dependency on WatcherContext having been
        // constructed, so logging here needs no special arrangement.
        SingleInstance = new Mutex(initiallyOwned: true, @"Local\PresenceLock", out bool createdNew);
        if (!createdNew)
        {
            Log.Write("startup aborted: the Local\\PresenceLock single-instance mutex is already " +
                      "held -- either another instance is already running, or something else " +
                      "(legitimate or not) holds a mutex of that name; same-user code can always " +
                      "do this, so this line exists for diagnosability, not as a defense");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new WatcherContext());
    }
}

sealed class Config
{
    public double AwayThresholdSeconds { get; init; } = 5.0;
    // RFC 0002-environment-levels, "Verification harness": reads Core.Cadence's single shared
    // constant rather than a bare literal, so the shell and the test-side environment model can
    // never drift on the default sample cadence.
    public int SampleIntervalMs { get; init; } = Core.Cadence.DefaultSampleIntervalMs;
    public double GraceSeconds { get; init; } = 10.0;
    // Also require this much keyboard/mouse idle time before locking, so a
    // detector blinded by bad light can't lock out an actively working user.
    public double InputIdleSeconds { get; init; } = 10.0;
    // Empty = automatic: prefer an external USB camera, else the built-in front camera.
    public string CameraNameContains { get; init; } = "";
    // Mean 8-bit luminance below this is a blocked/dark camera, not an empty room.
    public double DarkFrameMeanThreshold { get; init; } = 6.0;

    // Six PolicyConfig-mapped fields (0001-core-brain.md, "Config" mapping table, extended by
    // the 2026-08-01 addendum's UpgradeCooldownMs). Existing on-disk files simply lack these
    // keys and get these defaults, exactly like every other absent field today — the schema
    // stays backward-compatible. Consumed only by PolicyBridge.BuildPolicyConfig; no shell
    // decision code reads them directly.
    public long NoSignalReportAfterMs { get; init; } = 10000;
    public long ReevaluateAfterMs { get; init; } = 20000;
    public long ReevaluateCooldownMs { get; init; } = 600000;
    public int RecoveryFailureThreshold { get; init; } = 3;
    public long RecoveryCooldownMs { get; init; } = 600000;
    // RFC addendum 2026-08-01, slice 10 (camera-arrival upgrade): minimum gap between
    // CameraUpgrade restarts. No Settings UI control, same as the other five.
    public long UpgradeCooldownMs { get; init; } = 600000;

    // RFC 0002-environment-levels, "Media provider (first inhibitor)": file-only kill switch,
    // no Settings UI, same no-Settings-UI precedent as the seven fields above -- this feature
    // deliberately weakens the lock (media playing vetoes an otherwise-earned Lock), so it gets
    // an off switch. Structural-absence choice (see WatcherContext's constructor): disabling
    // this means the media provider is never constructed and never registered with the
    // inhibitor registry, rather than registered-but-permanently-queried-and-ignored.
    public bool MediaInhibitorEnabled { get; init; } = true;

    public static Config Load()
    {
        // Prefer the user-writable copy; the MSIX install dir is read-only.
        string[] paths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PresenceLock", "presencelock.json"),
            Path.Combine(AppContext.BaseDirectory, "presencelock.json"),
        ];
        foreach (var path in paths)
        {
            try
            {
                // Bounds-checked before it ever reaches the timer (bug fix: WinFormsTimer.Interval
                // throws for SampleIntervalMs < 1, killing the app at startup before the tray icon
                // exists) -- every field of a hand-edited or corrupted on-disk config is sanitized
                // here, on every path that produced a deserialized value.
                if (File.Exists(path))
                    return PolicyBridge.SanitizeSensingConfig(JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config());
            }
            catch (Exception ex)
            {
                Log.Write($"config load failed ({path}): {ex.Message}");
            }
        }
        return new Config();
    }
}

static class Log
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "presencelock.log");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                {
                    // SEC-6: keep one rolled generation instead of outright deleting the only
                    // diagnostic trail on rotation. Any same-user process can still delete or
                    // truncate either file at will (no append-only/ACL protection attempted --
                    // that needs OS ACLs, out of scope) -- but an ordinary size-triggered
                    // rotation must not itself be what destroys the log.
                    //
                    // Rotation gets its OWN try/catch so that a failed rotate (e.g. another
                    // process holding `.1` open) degrades to "append to the oversized primary"
                    // rather than dropping this line AND every subsequent line until the lock
                    // clears -- the append below must run whether or not the roll succeeded.
                    try
                    {
                        string backupPath = FilePath + ".1";
                        File.Delete(backupPath); // no-op if absent; keep exactly one prior generation
                        File.Move(FilePath, backupPath);
                    }
                    catch
                    {
                        // Keep writing to the primary; it will simply exceed the cap until the
                        // next write that can successfully roll it.
                    }
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the watcher down.
        }
    }

    static readonly Dictionary<string, long> rateLimitedLastWriteAt = new();
    static readonly object RateLimitGate = new();

    // Minimal once-per-interval guard (RFC 0002-environment-levels names this precedent for
    // both the WTS-query failure line here and the slice-5 inhibitor-query failure line; no
    // rate-limited logging existed anywhere in the tree before this slice, so this is the
    // first implementation, not a reuse). Keyed rather than message-keyed: a WTS query failure
    // carries a varying Win32 error code/exception message, and keying on the raw message text
    // would let text variation defeat the rate limit entirely -- the caller supplies a stable
    // category key instead. `Environment.TickCount64` (monotonic, boot-relative) rather than
    // wall-clock, so the guard is immune to a system clock change mid-run.
    public static void WriteRateLimited(string key, string message, long intervalMs = 60_000)
    {
        long now = Environment.TickCount64;
        lock (RateLimitGate)
        {
            if (rateLimitedLastWriteAt.TryGetValue(key, out long last) && now - last < intervalMs)
                return;
            rateLimitedLastWriteAt[key] = now;
        }
        Write(message);
    }
}

// Media provider (RFC 0002-environment-levels, "Media provider (first inhibitor)"): the first
// registered LockInhibitor. GlobalSystemMediaTransportControlsSessionManager is acquired ONCE,
// asynchronously, off the tick path -- InitializeAsync is fired exactly once, from
// WatcherContext's constructor, never from the watchdog tick; IsActive (the Func<bool> query fed
// into `new LockInhibitor("media-playing", ...)`) performs only synchronous property reads over
// the cached manager/sessions -- never a blocking wait on a WinRT async call from the
// synchronous watchdog Tick handler (the classic STA sync-over-async deadlock the RFC names).
sealed class MediaInhibitorProvider(Func<bool> isExiting)
{
    // Sleep/resume manager-validity leg of the SMTC spike (RFC 0002-environment-levels, "Media
    // provider (first inhibitor)") is deferred to slice 6's live smoke -- deliberately NOT
    // pre-built here. If it later shows the cached manager going stale across a sleep/resume
    // cycle, the RFC's named fix is one `SystemEvents.PowerModeChanged` hook re-running
    // `InitializeAsync`; `manager` being a plain settable field (not `readonly`) is the seam
    // that leaves for that fix, without speculatively wiring the hook now.
    GlobalSystemMediaTransportControlsSessionManager? manager;

    // Threading invariant (RFC 0002-environment-levels, round 3): this continuation must resume
    // on the UI SynchronizationContext -- no ConfigureAwait(false) -- so the `manager` write
    // below never races the watchdog tick's synchronous IsActive reads. This relies on the same
    // mechanism StartWatchingAsync already depends on for `await FaceDetector.CreateAsync()`:
    // called from the UI thread (WatcherContext's constructor), the default awaiter captures
    // SynchronizationContext.Current and resumes the continuation there, with no explicit
    // ui.Post needed.
    public async Task InitializeAsync()
    {
        try
        {
            var acquired = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            // Shutdown-continuation guard (RFC: "a late-completing acquisition continuation
            // after tray Exit checks the existing shutdown/exiting state before touching any
            // field and no-ops" -- the RestartProcess-audited-ordering guard class): a slow
            // RequestAsync that completes after tray Exit/RestartProcess must not resurrect
            // state on a WatcherContext that is mid-teardown.
            if (isExiting()) return;
            manager = acquired;
        }
        catch (Exception ex)
        {
            // Acquisition failure fails open: IsActive below reports inactive (manager stays
            // null) rather than the provider ever reporting an inhibited-forever level. Also
            // covers the SMTC spike's still-open packaged-manifest-capability leg (slice 6 live
            // smoke): if RequestAsync ever needs a capability this build's manifest lacks, this
            // provider degrades to permanently-inactive instead of crashing startup.
            Log.Write($"media inhibitor: session manager acquisition failed: {ex.Message}");
        }
    }

    // The Func<bool> query fed into the registered LockInhibitor. Synchronous property reads
    // only -- no I/O -- so calling this from the watchdog tick's LockInhibitor.Refresh never
    // blocks. Active iff any session reports PlaybackStatus == Playing (RFC: "Browsers surface
    // YouTube/Netflix playback there; Spotify and native players likewise"). Before the manager
    // arrives (acquisition still pending, or failed), reports inactive -- fail-open, matching
    // LockInhibitor.Refresh's own contract.
    public bool IsActive() =>
        manager is not null &&
        manager.GetSessions().Any(s =>
            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
}

sealed class WatcherContext : ApplicationContext
{
    Config cfg;
    // The real decision core (0001-core-brain.md, slice 8b cutover): the sole decision-maker.
    // `Advance` is the only place `state` is reassigned. `policyConfig` is rebuilt whenever
    // `cfg` changes (Settings commit) so `step` always reads live config.
    Core.PolicyConfig policyConfig;
    Core.State state;
    // Environment-level mirrors (RFC 0002-environment-levels, "Session mirror with
    // reconciliation" / "Pause ownership"): the shell owns these, sampled fresh into a
    // `Core.StepInputs` on every `Advance` call via `BuildStepInputs` -- never folded into
    // `Core.State` itself. `sessionLockMirror` owns the session-lock mirror and its WTS-
    // reconciliation bookkeeping as one unit (`PolicyBridge.SessionLockMirror`, DES-2): what
    // used to be three loose fields here -- `sessionLocked`/
    // `ticksSinceLastSessionSwitchMirrorChange`/`sessionLockDisagreementCount` -- mutated from
    // three call sites (this constructor's startup seed, `OnSessionSwitch`, and the watchdog
    // tick's WTS reconciliation) with only a comment conceding "these change together" as
    // convention, not structure. One type now owns them structurally, mirroring
    // `LockInhibitorRegistry`'s precedent one slice later. `Locked` is seeded from the
    // constructor's synchronous startup WTS query (slice 4, `AssembleStartupInputs`) -- fail-
    // open, so a failed startup query leaves it at its safe default, false. Updated thereafter
    // by `OnSessionSwitch` and by watchdog-tick WTS reconciliation corrections
    // (`SessionLockMirror.Reconcile`, delegating to the unchanged `PolicyBridge.
    // ReconciliationStep`) -- both already UI-thread-only call sites (the threading invariant
    // `Advance` asserts), so `SessionLockMirror` itself needs no locking of its own. `paused`
    // starts from `ConsumePersistedPausedFlag` in the constructor.
    // DES-2b: this inline initializer IS unconditionally overwritten below, before any read,
    // by the real startup WTS query's result -- one throwaway `SessionLockMirror` allocated per
    // startup for no behavioral benefit. Deliberately kept anyway: this constructor's
    // watchdogTimer.Tick/OnSessionSwitch closures (declared earlier in the constructor, capturing
    // this field) are compiled against the field's nullable flow-state at their own textual
    // position, not their invocation time -- removing the initializer turns this field's
    // non-nullable `SessionLockMirror` type into a real CS8602-flagged "possibly null" at every
    // closure read (confirmed by trying it), exactly the same class of reasoning the
    // `inhibitorRegistry` field comment above documents for why IT is assigned early. Trading a
    // real, permanent warning for a one-time-per-process throwaway allocation is not a good
    // trade -- this is a trivial micro-opt, not worth the risk DES-2b itself says to weigh it
    // against.
    readonly SessionLockMirror sessionLockMirror = new(initialLocked: false);
    bool paused;
    // Inhibitor registry (RFC 0002-environment-levels, "Inhibitor registry"): built once in the
    // constructor from the configured providers (media playback, kill-switch gated -- see
    // MediaInhibitorProvider). `Refresh()` runs ONLY from the watchdog tick
    // (`WatchdogTickStep`'s middle step); `BuildStepInputs`/`Advance` read `.Active` and
    // `.ActiveNames` at any cadence off this cached snapshot -- the cached/slow-cadence
    // discipline lives in the registry's own type, not in prose here.
    readonly LockInhibitorRegistry inhibitorRegistry;
    // Presence-stabilization filter (0001-core-brain.handoff.md, "Burn-in incident 2026-07-28"):
    // raw per-frame FaceDetector output flickers false-positive on an empty scene under a
    // hunting auto-framing crop. Stepped in SampleAsync before ClassifySample/Advance, so every
    // downstream consumer sees the identical stabilized presence signal. Reset to
    // `Core.PresenceFilter.initial` at two kinds of site (RFC 0002-environment-levels, round 3
    // scoping): fresh-acquisition resets, orthogonal to suppression (`StartWatchingAsync`'s
    // init path and the constructor, both untouched by this RFC), and the single
    // Advance-internal suppression-transition rule, which fires this reset on every suppressed
    // -> unsuppressed transition (session unlock, pause resume, and — from slice 4 — a WTS-
    // reconciliation correction) — since spatial coherence measured against a previous
    // acquisition's or watching episode's frames must never carry into a new one.
    Core.FilterState presenceFilterState;
    // Frozen-frame staleness (0001-core-brain.md bug-fix note): tracks whether the frame
    // reader's SystemRelativeTime is still advancing. Stepped in SampleAsync alongside
    // presenceFilterState, and reset at exactly the same points — a fresh acquisition's or
    // watching episode's first frame must not be judged stale against a previous acquisition's
    // or episode's last-seen timestamp.
    Core.FreshnessState frameFreshnessState;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem statusItem;
    readonly ToolStripMenuItem pauseItem;
    readonly WinFormsTimer sampleTimer;
    readonly WinFormsTimer retryTimer;
    // Sampling watchdog (incident 2026-08-12): belt-and-suspenders for the whole sample-
    // starvation class -- a resume path that fails to restart sampleTimer, or a SampleAsync
    // pass hung forever with the `sampling` reentrancy guard stuck true. Always running (started
    // in the constructor, never stopped on pause/lock -- PolicyBridge.ExpectsSampling already
    // makes those states a no-op inside the pure step), stopped only at process exit alongside
    // sampleTimer. See lastSamplePassAt/watchdogStrikes below and PolicyBridge.SamplingWatchdogStep.
    readonly WinFormsTimer watchdogTimer;
    // Camera-arrival upgrade (0001-core-brain.md addendum 2026-08-01, slice 10): debounces a
    // burst of DeviceWatcher.Added events (a dock enumerates several devices over seconds)
    // into a single would-pick-now check, restarted on every Added event seen after
    // EnumerationCompleted.
    readonly WinFormsTimer deviceSettleTimer;
    readonly Windows.Devices.Enumeration.DeviceWatcher deviceWatcher;
    bool deviceEnumerationCompleted;
    readonly SynchronizationContext ui;

    FaceDetector? detector;
    MediaCapture? capture;
    MediaFrameReader? reader;
    // The in-use camera's MediaFrameSourceInfo.Id (0001-core-brain.md addendum 2026-08-01):
    // set on every successful InitCameraAsync, cleared on teardown. CheckForBetterCamera
    // compares this against SelectPreferredCamera's would-pick-now result.
    string? activeCameraId;

    byte[]? lumaBuf;
    bool sampling;
    // Watchdog stamp (Environment.TickCount64 domain), updated only at the end of a *completed*
    // SampleAsync pass -- where the `sampling` reentrancy guard is released -- never on the
    // early guard return that fires while a pass is already in flight. A hung
    // `await detector.DetectFacesAsync` must starve this stamp so the watchdog notices; a tick
    // that bounces off the stuck guard must not paper over that by refreshing it anyway.
    long lastSamplePassAt;
    // Consecutive starved watchdogTimer ticks since the last self-heal/escalation reset — the
    // two-strike counter PolicyBridge.SamplingWatchdogStep steps.
    int watchdogStrikes;
    bool starting;
    bool settingsOpen;
    // Shutdown/exiting guard (RFC 0002-environment-levels, "Media provider": "the same guard
    // class as RestartProcess's audited ordering"): set once, at the start of ExitThreadCore --
    // reached alike by tray Exit and by RestartProcess's own ExitThread() call, so one flag
    // covers both. MediaInhibitorProvider.InitializeAsync's continuation checks this before
    // writing its cached manager field, so a late-completing acquisition after teardown has
    // begun cannot resurrect state on a WatcherContext that is going away.
    bool exiting;
    // The shell's own current-sample dark-vs-no-frame knowledge (0001-core-brain.md:
    // "Status.NoSignal does not distinguish dark vs. no-frame ... the shell already computed
    // that distinction itself one line before constructing the Observation"). Set every sample
    // in SampleAsync, read only for rendering/logging the finer-grained NoSignal text — never
    // fed back into Core, never a decision input.
    bool lastObservationDark;
    // Dedup for the tray/status render (0001-core-brain.md: "the shell ... re-renders only when
    // the rendered string differs").
    string? lastRenderedStatusText;

    // Sensing diagnostics: the log records decisions only, so a feed that keeps
    // "seeing" a face produces total silence while locks never fire. Logs
    // observation-kind transitions and the authoritative Core.FrameFreshness
    // fresh<->stale transition (a pipeline that stops delivering frames makes
    // TryAcquireLatestFrame re-serve the last cached frame — SampleAsync's freshness
    // gate, not this field, is what stops that from being misread as live presence;
    // this field only remembers the previous verdict so the transition logs once).
    Core.Observation? diagLastObs;
    bool diagWasStale;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // De-duplicates `Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64)`, repeated verbatim
    // at every call site in this class that needs "now" in the monotonic domain (Advance's
    // StepContext, snapshot/log call sites, and the presence-filter/freshness steps in
    // SampleAsync). Pure de-duplication -- every call site reads the identical live
    // Environment.TickCount64, so this changes no behavior.
    static Core.MonotonicMs NowMonotonic() => Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64);

    static long InputIdleMs()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        // On failure report zero idle time: fail open, never lock on bad data.
        if (!GetLastInputInfo(ref lii)) return 0;
        return unchecked((uint)Environment.TickCount - lii.dwTime);
    }

    // WTS session-lock query (RFC 0002-environment-levels, "Session mirror with
    // reconciliation"): WTSQuerySessionInformation(WTSSessionInfoEx) against the local server
    // (IntPtr.Zero, the documented WTS_CURRENT_SERVER_HANDLE) for the calling process's own
    // session (WTS_CURRENT_SESSION), used both by the constructor's synchronous startup query
    // and by every watchdog-tick reconciliation.
    // CharSet.Unicode explicitly: wtsapi32.dll exports only the "A"/"W"-suffixed symbols, no
    // bare "WTSQuerySessionInformation" -- without an explicit CharSet the default P/Invoke
    // probe (CharSet.Ansi) would silently bind the "A" variant instead.
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr pMemory);

    enum WTS_INFO_CLASS
    {
        WTSSessionInfoEx = 25,
    }

    const int WTS_CURRENT_SESSION = -1;

    // WTSINFOEX layout (RFC 0002-environment-levels, "Session mirror with reconciliation",
    // spike deliverable -- marshaling trap verified LIVE on this machine): `Data` is a union
    // whose largest arm (WTSINFOEX_LEVEL1) holds LARGE_INTEGER fields further out, which forces
    // 8-byte alignment on the whole union -- so `Data` begins at byte offset 8, not 4, with 4
    // padding bytes after the leading `Level` DWORD. Within `Data`: SessionId sits at absolute
    // offset 8, SessionState at offset 12, SessionFlags at offset 16. An offset-4 read (the
    // naive "right after Level" guess) plausibly returns SessionState where SessionFlags is
    // expected -- this struct only maps the three fields this query needs, at their verified
    // absolute offsets, specifically to make that trap impossible to reintroduce silently.
    [StructLayout(LayoutKind.Explicit)]
    struct WTSINFOEX_LEVEL1_PARTIAL
    {
        [FieldOffset(0)] public int Level;
        [FieldOffset(8)] public int SessionId;
        [FieldOffset(12)] public int SessionState;
        [FieldOffset(16)] public int SessionFlags;
    }

    // The untestable OS-boundary half of the WTS query -- P/Invoke call, buffer lifetime, and
    // offset read -- kept as thin as InputIdleMs's precedent immediately above. The one
    // genuinely meaningful piece of logic (what a SessionFlags value MEANS) is
    // PolicyBridge.InterpretSessionFlags, pure and unit-tested; this method only gets bytes off
    // the wire and hands them off. Rate-limited on failure: called every watchdog tick (5s
    // default), so an unthrottled log line would spam during a genuine outage.
    static PolicyBridge.WtsLockQueryResult QuerySessionLocked()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero, WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSSessionInfoEx, out buffer, out int bytesReturned))
            {
                Log.WriteRateLimited("wts-query",
                    $"WTSQuerySessionInformation failed (Win32 error {Marshal.GetLastWin32Error()}) -- " +
                    "fail-open: sessionLocked mirror unchanged, reconciliation skipped this tick");
                return new PolicyBridge.WtsLockQueryResult(Succeeded: false, Locked: false);
            }
            // Sanity floor: SessionFlags sits at offset 16 and is itself 4 bytes, so a
            // legitimate buffer must be at least 20 bytes -- guards the Marshal.PtrToStructure
            // read below against a malformed/truncated response.
            if (bytesReturned < 20)
            {
                Log.WriteRateLimited("wts-query",
                    $"WTSQuerySessionInformation returned only {bytesReturned} bytes (need >= 20 for " +
                    "SessionFlags) -- fail-open: sessionLocked mirror unchanged, reconciliation skipped this tick");
                return new PolicyBridge.WtsLockQueryResult(Succeeded: false, Locked: false);
            }

            var info = Marshal.PtrToStructure<WTSINFOEX_LEVEL1_PARTIAL>(buffer);
            var result = PolicyBridge.InterpretSessionFlags(info.SessionFlags);
            if (!result.Succeeded)
                Log.WriteRateLimited("wts-query",
                    $"WTSQuerySessionInformation returned unrecognized SessionFlags {info.SessionFlags} -- " +
                    "fail-open: sessionLocked mirror unchanged, reconciliation skipped this tick");
            return result;
        }
        catch (Exception ex)
        {
            Log.WriteRateLimited("wts-query",
                $"WTS session query threw ({ex.Message}) -- fail-open: sessionLocked mirror unchanged, " +
                "reconciliation skipped this tick");
            return new PolicyBridge.WtsLockQueryResult(Succeeded: false, Locked: false);
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    // The ONE StepInputs-construction site (RFC 0002-environment-levels, "Construction
    // discipline"): used by Advance's input assembly and the startup Policy.start call, always
    // via named arguments -- the mitigation for StepInputs' three adjacent same-typed bools,
    // where a positional call could silently transpose two of them. This static overload is the
    // actual `new Core.StepInputs(...)` call site; the instance overload below and
    // AssembleStartupInputs's caller both route through it, so "exactly one construction site"
    // holds even though the startup path can't read the instance's live inhibitor-registry
    // snapshot (it hasn't run a single Refresh() yet when AssembleStartupInputs runs -- Refresh
    // rides the watchdog tick, which hasn't fired at startup -- so AssembleStartupInputs keeps
    // passing the literal `false` that a not-yet-refreshed registry would report anyway).
    // `internal` (slice 5): the same testability exception AssembleStartupInputs already has,
    // so `PresenceLock.Tests` can pin the registry-active -> LockInhibited-true sourcing
    // directly against this helper without a live WatcherContext.
    internal static Core.StepInputs BuildStepInputs(bool sessionLocked, bool paused, bool lockInhibited, long inputIdleMs) =>
        new(
            sessionLocked: sessionLocked,
            paused: paused,
            lockInhibited: lockInhibited,
            inputIdleMs: inputIdleMs);

    // RFC 0002-environment-levels, "Status": LockInhibited is sourced from the inhibitor
    // registry's own cached snapshot (slice 5) -- never from Policy.lockInhibited, which exists
    // for the verification harness/diagnostics only. `inhibitorRegistry.Active` is read fresh
    // here on every Advance call, matching every other level in StepInputs; only Refresh()
    // itself is watchdog-tick-cadence.
    Core.StepInputs BuildStepInputs() =>
        BuildStepInputs(sessionLockMirror.Locked, paused, lockInhibited: inhibitorRegistry.Active, inputIdleMs: InputIdleMs());

    // Startup input-assembly ordering (RFC 0002-environment-levels, "Construction discipline"
    // pinned test / "Session mirror with reconciliation" startup bullet): pause consume-and-
    // clear, THEN the WTS query, THEN BuildStepInputs -- in exactly that order, because a
    // misordered constructor would fail SILENTLY as "unsuppressed": sampling and decisions
    // proceeding as if the session weren't actually locked, on a restart-while-locked process,
    // for up to one watchdog interval. The constructor itself can't be driven under xunit
    // (WinForms/WinRT construction), so this is the testable seam the RFC calls for: a pure
    // orchestrator over injected delegates whose call order a test can record and assert
    // directly, with the constructor supplying the real
    // ConsumePersistedPausedFlag/QuerySessionLocked/InputIdleMs implementations. Returns the
    // values the constructor needs rather than mutating instance fields, so the ordering logic
    // itself has no WinForms-instance dependency at all. Fail-open at startup too (RFC): a
    // failed WTS query yields sessionLocked = false, matching QuerySessionLocked's
    // Succeeded = false contract -- a startup query never sets locked = true from bad data.
    internal static (bool Paused, bool SessionLocked, Core.StepInputs Inputs) AssembleStartupInputs(
        Func<bool> consumePersistedPausedFlag,
        Func<PolicyBridge.WtsLockQueryResult> queryWtsSessionLocked,
        Func<long> readInputIdleMs)
    {
        bool paused = consumePersistedPausedFlag();
        var wts = queryWtsSessionLocked();
        bool sessionLocked = wts.Succeeded && wts.Locked;
        var inputs = BuildStepInputs(
            sessionLocked: sessionLocked, paused: paused, lockInhibited: false, inputIdleMs: readInputIdleMs());
        return (paused, sessionLocked, inputs);
    }

    // Watchdog-tick composition order (RFC 0002-environment-levels, "Advance and timers":
    // "within one watchdog tick, WTS reconciliation runs first, then inhibitor
    // Refresh()/aggregation ..., and only then does SamplingWatchdogStep read Policy.status").
    // The real Tick handler can't be driven under xunit (WinForms Timer, live P/Invoke, live
    // WinRT) -- mirroring AssembleStartupInputs's precedent, this is a pure orchestrator over
    // injected delegates whose call order a test can record and assert directly; the real Tick
    // handler above supplies the real reconciliation/inhibitor-refresh/sampling-watchdog
    // closures over its own instance state.
    internal static void WatchdogTickStep(Action reconcile, Action refreshInhibitors, Action runSamplingWatchdogStep)
    {
        reconcile();
        refreshInhibitors();
        runSamplingWatchdogStep();
    }

    public WatcherContext()
    {
        cfg = Config.Load();
        policyConfig = PolicyBridge.BuildPolicyConfig(cfg);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;

        // Inhibitor registry (RFC 0002-environment-levels, "Inhibitor registry" / "Media
        // provider"): built here, early -- before watchdogTimer's Tick handler below is even
        // declared, so `inhibitorRegistry` is a definitely-assigned field at every point that
        // closure can read it (assigning it later, after the closure's declaration, compiles
        // identically at runtime but leaves the field's nullable flow-state "maybe unassigned"
        // at the closure's textual position, which is a real warning worth not having to
        // reason past on every future change to this constructor). Kill-switch structural
        // choice: when MediaInhibitorEnabled is false, the media provider is never constructed
        // and never registered -- rather than registered-but-permanently-queried-and-ignored --
        // so a disabled inhibitor costs nothing at watchdog cadence and can never appear in
        // ActiveNames even transiently. Refresh() runs ONLY from the watchdog tick below;
        // BuildStepInputs/Advance read Active/ActiveNames at any cadence off the cached
        // snapshot.
        var inhibitors = new List<LockInhibitor>();
        if (cfg.MediaInhibitorEnabled)
        {
            var mediaProvider = new MediaInhibitorProvider(isExiting: () => exiting);
            inhibitors.Add(new LockInhibitor("media-playing", mediaProvider.IsActive));
            // Async-once acquisition, off the tick path (RFC): fired here, at startup, and
            // awaited to completion by its own continuation -- never invoked from the
            // synchronous watchdog Tick handler. SynchronizationContext.Current is already `ui`
            // at this point (set immediately above), so the continuation resumes on the UI
            // thread per the threading invariant, with no explicit ui.Post needed.
            _ = mediaProvider.InitializeAsync();
        }
        inhibitorRegistry = new LockInhibitorRegistry(inhibitors);

        statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        lastRenderedStatusText = "Starting…";
        pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Lock now", null, (_, _) => LockWorkStation()));
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "PresenceLock",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => OpenSettings();

        sampleTimer = new WinFormsTimer { Interval = cfg.SampleIntervalMs };
        sampleTimer.Tick += async (_, _) => await SampleAsync();

        retryTimer = new WinFormsTimer { Interval = 5000 };
        retryTimer.Tick += async (_, _) =>
        {
            retryTimer.Stop();
            // Retry-timer gate (0001-core-brain.md R2-15): the complete, core-owned test —
            // replaces the legacy `!paused && !sessionLocked` shell-local booleans. This is one
            // of two call sites sharing the identical status gate (R2-4); the other is
            // KickAcquisitionIfNeeded, called once from Advance's suppression-transition rule
            // (RFC 0002-environment-levels) rather than hand-copied at every resume call site.
            if (reader is null && PolicyBridge.IsAcquiringOrRecovering(Core.Policy.status(state)))
                await StartWatchingAsync();
        };

        // Sampling watchdog (incident 2026-08-12) — see the field comment above and
        // PolicyBridge.SamplingWatchdogStep for the pure step this drives. Starts immediately
        // and always running: PolicyBridge.ExpectsSampling already makes paused/locked/acquiring
        // states a no-op inside the pure step, so there is no state in which this timer itself
        // needs to be stopped short of process exit.
        lastSamplePassAt = Environment.TickCount64;
        // RFC 0002-environment-levels, "Verification harness": reads Core.Cadence's single
        // shared constant -- the model's Tick delta set derives from the same value, so drift
        // between "what the shell ticks at" and "what the model explores" is impossible by
        // construction. `retryTimer` above is acquisition-retry cadence, a different concern
        // that happens to share the same numeric literal today -- it is NOT the watchdog and is
        // deliberately left as a bare literal.
        watchdogTimer = new WinFormsTimer { Interval = Core.Cadence.WatchdogIntervalMs };
        watchdogTimer.Tick += (_, _) =>
        {
            // Watchdog-tick composition order, pinned (RFC 0002-environment-levels, "Advance
            // and timers"): reconcile -> inhibitor Refresh()/aggregation -> SamplingWatchdogStep
            // -- routed through WatchdogTickStep (see its own comment) so this order is a
            // unit-tested seam, not merely this lambda's textual layout. Reconciliation runs
            // FIRST so a WTS correction's Advance(Reconcile) has already landed before inhibitor
            // refresh runs; inhibitor refresh runs before SamplingWatchdogStep so the watchdog's
            // starvation verdict always reads this tick's fully corrected Policy.status.
            WatchdogTickStep(
                reconcile: () =>
                {
                    var wtsResult = QuerySessionLocked();
                    // DES-2: SessionLockMirror.Reconcile owns the mirror/ticks/disagreement-count
                    // trio and delegates the actual decision to PolicyBridge.ReconciliationStep
                    // unchanged -- this closure only executes the verdict (log + Advance), exactly
                    // as it did against the loose fields before extraction.
                    var reconciliation = sessionLockMirror.Reconcile(
                        querySucceeded: wtsResult.Succeeded, queriedLocked: wtsResult.Locked);
                    if (reconciliation.CorrectionApplied)
                    {
                        // Corrections drive behavior, not just the bool (RFC): mirror update then
                        // Advance(Reconcile), the identical path a live SessionSwitch takes --
                        // timer stop/start, filter/freshness resets, and KickAcquisitionIfNeeded
                        // all ride the Advance-internal suppression-transition rule by
                        // construction, never a hand-copied chore here. Each correction logs a
                        // warning: it is itself a signal worth seeing (a missed SessionSwitch
                        // edge just self-healed).
                        Log.Write($"WARNING: WTS reconciliation correcting sessionLocked mirror " +
                                  $"{reconciliation.OldLocked} -> {reconciliation.NewLocked} (OS query disagreed for two " +
                                  "consecutive watchdog ticks)");
                        Advance(Core.Event.Reconcile);
                    }
                },
                refreshInhibitors: () =>
                {
                    // On a change, log the transition (RFC: "the log always explains a non-lock")
                    // then fire Advance(Reconcile) -- the identical corrections-drive-behavior
                    // contract WTS reconciliation follows above.
                    if (inhibitorRegistry.Refresh())
                    {
                        Log.Write(inhibitorRegistry.Active
                            ? $"lock inhibitors active: {string.Join(", ", inhibitorRegistry.ActiveNames)}"
                            : "lock inhibitors cleared");
                        Advance(Core.Event.Reconcile);
                    }
                },
                runSamplingWatchdogStep: () =>
                {
                    // Live from cfg (not captured once) so a Settings change to SampleIntervalMs
                    // is picked up on the very next tick. See
                    // PolicyBridge.SamplingStarvationThresholdMs for the rationale behind the
                    // 10x/15s-floor computation.
                    long starvationMs = PolicyBridge.SamplingStarvationThresholdMs(cfg.SampleIntervalMs);
                    var verdict = PolicyBridge.SamplingWatchdogStep(
                        expectsSampling: PolicyBridge.ExpectsSampling(Core.Policy.status(state)),
                        nowMs: Environment.TickCount64,
                        lastSamplePassMs: lastSamplePassAt,
                        strikes: watchdogStrikes,
                        starvationMs: starvationMs);
                    watchdogStrikes = verdict.Strikes;
                    lastSamplePassAt = verdict.StampMs;
                    if (verdict.StartSampleTimer)
                    {
                        Log.Write($"sampling watchdog: no completed sample pass for {starvationMs}ms — restarting sample timer");
                        sampleTimer.Start(); // idempotent if already running
                    }
                    else if (verdict.TreatAsCaptureFailed)
                    {
                        Log.Write("sampling watchdog: still starved after self-heal — treating as capture failure");
                        Advance(Core.Event.CaptureFailed); // Action.Restart executed => process exiting; nothing follows in this tick either way
                    }
                });
        };
        watchdogTimer.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Camera-arrival upgrade (0001-core-brain.md addendum 2026-08-01, slice 10): events
        // marshaled to the UI thread — same precedent as OnSessionSwitch — since DeviceWatcher
        // raises them from its own background thread. Ignore the initial-enumeration Added
        // backfill (guarded by deviceEnumerationCompleted below); react only to a genuine
        // post-enumeration arrival, debounced by deviceSettleTimer.
        deviceSettleTimer = new WinFormsTimer { Interval = 5000 };
        deviceSettleTimer.Tick += (_, _) =>
        {
            deviceSettleTimer.Stop();
            CheckForBetterCamera();
        };
        deviceWatcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(
            Windows.Devices.Enumeration.DeviceClass.VideoCapture);
        deviceWatcher.Added += (_, _) => ui.Post(_ =>
        {
            if (!deviceEnumerationCompleted) return;
            deviceSettleTimer.Stop();
            deviceSettleTimer.Start();
        }, null);
        deviceWatcher.EnumerationCompleted += (_, _) => ui.Post(_ => deviceEnumerationCompleted = true, null);
        deviceWatcher.Start();

        Log.Write($"started (threshold {cfg.AwayThresholdSeconds}s, sample {cfg.SampleIntervalMs}ms)");

        // Paused-flag consume-and-clear, THEN the synchronous startup WTS query, THEN the
        // initial StepInputs via the ONE helper, THEN the single Policy.start call -- pinned
        // order (0001-core-brain.md R2-11 / RFC 0002-environment-levels "Pause ownership" +
        // "Session mirror with reconciliation" startup bullet), assembled by
        // AssembleStartupInputs so the ordering itself is unit-tested. This all runs before the
        // first camera-acquisition attempt. The paused flag is written only by RestartProcess,
        // immediately before spawn — so pause survives every self-restart, but a tray Exit or a
        // normal launch never inherits a stale pause; inheritance is ordinary input passing
        // into Policy.start now (the refeed half of the old protocol -- an Event.Paused
        // injection after start -- is deleted). The startup WTS query is fail-open exactly like
        // every later reconciliation tick: a failed/uninterpretable query leaves sessionLocked
        // at false rather than guessing locked. Without querying WTS here, a restart-while-
        // locked process (CaptureFailed/BetterCameraAvailable restarts are not gated by session
        // state) would run unsuppressed with a wrong tray status for up to one watchdog
        // interval.
        var startup = AssembleStartupInputs(
            PolicyBridge.ConsumePersistedPausedFlag, QuerySessionLocked, InputIdleMs);
        paused = startup.Paused;
        sessionLockMirror = new SessionLockMirror(startup.SessionLocked);
        // SEC-4: an unexpected paused start must be diagnosable, not silent — see
        // PolicyBridge.StartupPauseInheritedWarning's comment for the full rationale.
        var pauseWarning = PolicyBridge.StartupPauseInheritedWarning(paused);
        if (pauseWarning is not null) Log.Write(pauseWarning);
        state = Core.Policy.start(
            NowMonotonic(),
            PolicyBridge.LoadRestartStamps(),
            startup.Inputs);
        presenceFilterState = Core.PresenceFilter.initial;
        frameFreshnessState = Core.FrameFreshness.initial;

        _ = StartWatchingAsync();
    }

    async Task StartWatchingAsync()
    {
        if (starting) return;
        starting = true;
        // Fresh camera = fresh filter: spatial coherence measured against the previous
        // acquisition's frames must never carry into this one. Same rationale for freshness:
        // a fresh acquisition's first timestamp must not be compared against the previous
        // acquisition's last one.
        presenceFilterState = Core.PresenceFilter.initial;
        frameFreshnessState = Core.FrameFreshness.initial;
        try
        {
            if (!FaceDetector.IsSupported)
            {
                Log.Write("FaceDetector.IsSupported == false — idle");
                SetRawStatusText("Face detection unsupported — idle");
                return;
            }
            detector ??= await FaceDetector.CreateAsync();
            if (reader is null) await InitCameraAsync();
            if (Advance(Core.Event.InitSucceeded)) return; // process restarting (unreachable for this event, but see Advance's contract)
            sampleTimer.Start();
        }
        catch (Exception ex)
        {
            TeardownCamera();

            // Persistent E_HANDLE from a fresh MediaCapture means this process's connection to
            // the camera FrameServer is wedged (seen after lock/unlock cycles); only a new
            // process recovers. Match on message too: the WinRT projection wraps the error and
            // does not always preserve the E_HANDLE HResult. Advance(InitFailed) is the core's
            // only gate on that decision now — streak + handle-invalid classification +
            // wall-clock cooldown persisted across restarts (0001-core-brain.md, "Notes:
            // recovery boundary").
            bool handleInvalid = PolicyBridge.IsHandleInvalid(ex);
            if (Advance(Core.Event.NewInitFailed(handleInvalid))) return; // Action.Restart executed — process exiting

            var snap = Core.Policy.snapshot(policyConfig, state, NowMonotonic());
            Log.Write($"start failed ({snap.InitFailStreak}, hr=0x{ex.HResult:X8}, t{Environment.CurrentManagedThreadId}): {ex.Message.Trim()}");

            if (reader is null && PolicyBridge.IsAcquiringOrRecovering(Core.Policy.status(state)))
                retryTimer.Start();
        }
        finally
        {
            starting = false;
        }
    }

    // Color-camera candidates as (Group, Info) pairs — the live WinRT handles selection needs
    // to actually open a camera. Shared by InitCameraAsync's startup selection and the device-
    // watcher's arrival-triggered settle check (0001-core-brain.md addendum 2026-08-01, slice
    // 10) so both enumerate identically; only the DTO projection below is what the pure
    // ranking function sees.
    static async Task<List<(MediaFrameSourceGroup Group, MediaFrameSourceInfo Info)>> ColorCameraCandidatesAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups
            .SelectMany(g => g.SourceInfos.Select(i => (Group: g, Info: i)))
            .Where(t => t.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();
    }

    static List<PolicyBridge.CameraCandidate> ToCandidateDtos(
        IEnumerable<(MediaFrameSourceGroup Group, MediaFrameSourceInfo Info)> candidates) =>
        candidates
            .Select(t => new PolicyBridge.CameraCandidate(t.Info.Id, t.Group.DisplayName, PanelOf(t.Info)))
            .ToList();

    async Task InitCameraAsync()
    {
        var candidates = await ColorCameraCandidatesAsync();
        if (candidates.Count == 0)
            throw new InvalidOperationException("no color camera found");

        // Camera *selection* stays shell-side sensing (0001-core-brain.md, "Notes"), but the
        // ranking itself is the one pure, unit-tested function shared with the device-watcher
        // settle check (0001-core-brain.md addendum 2026-08-01, slice 10) — behavior-preserving
        // refactor of what this method computed inline before this slice.
        var chosenId = PolicyBridge.SelectPreferredCamera(ToCandidateDtos(candidates), cfg.CameraNameContains);
        var chosen = candidates.First(t => t.Info.Id == chosenId);
        activeCameraId = chosen.Info.Id;

        capture = new MediaCapture();
        capture.Failed += OnCaptureFailed;
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = chosen.Group,
            // SharedReadOnly so a Teams/Zoom call owning the camera doesn't
            // fight us — we read whatever format the current owner negotiated.
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        });

        var source = capture.FrameSources[chosen.Info!.Id];
        reader = await capture.CreateFrameReaderAsync(source);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException($"frame reader start: {status}");

        Log.Write($"camera: {chosen.Group.DisplayName} (t{Environment.CurrentManagedThreadId})");
    }

    // Normalizes "no enclosure location at all" (external USB webcams) to Unknown, matching
    // PolicyBridge.PanelRank's treatment of Unknown as the best (external-preferred) rank —
    // the panel half of what SelectPreferredCamera now ranks over.
    static EnclosurePanel PanelOf(MediaFrameSourceInfo info) =>
        info.DeviceInformation?.EnclosureLocation?.Panel ?? EnclosurePanel.Unknown;

    double MeanLuma(SoftwareBitmap gray)
    {
        int len = gray.PixelWidth * gray.PixelHeight;
        // Oversized so CopyToBuffer never fails on row-padded layouts.
        if (lumaBuf is null || lumaBuf.Length < len * 2) lumaBuf = new byte[len * 2];
        gray.CopyToBuffer(lumaBuf.AsBuffer());
        long sum = 0;
        int samples = 0;
        for (int i = 0; i < len; i += 251) { sum += lumaBuf[i]; samples++; }
        return samples == 0 ? 0 : (double)sum / samples;
    }

    // Pure logging; reads no decision state and writes none — see the diag* field comments.
    // `present` here is the stabilized PresenceFilter output (SampleAsync sets it before this
    // call), so `obs`/`diagLastObs` transitions now reflect stabilized observations; `rawFaceCount`
    // is appended so the raw FaceDetector signal a stabilized FaceSeen/NoFace transition summarizes
    // is still visible in the log (0001-core-brain.handoff.md, "Burn-in incident 2026-07-28").
    // `frameIsFresh` is the authoritative Core.FrameFreshness verdict SampleAsync already
    // computed for this sample — `null` when freshness couldn't be assessed (no frame acquired,
    // or SystemRelativeTime unavailable) — this method only logs its fresh<->stale transition,
    // it does not independently detect staleness (the redundant parallel 10-count detector this
    // method used to keep for that is gone; SampleAsync's classification is the one source of
    // truth now).
    void LogSensingDiagnostics(bool haveFrame, bool dark, bool present, long idleMs, int rawFaceCount, bool? frameIsFresh)
    {
        var obs = PolicyBridge.ClassifyObservation(haveFrame, dark, present);
        if (diagLastObs is null || !obs.Equals(diagLastObs))
        {
            Log.Write($"observation: {diagLastObs?.ToString() ?? "(start)"} -> {obs} (idle {idleMs}ms, raw={rawFaceCount} faces)");
            diagLastObs = obs;
        }
        if (frameIsFresh is null) return;
        bool stale = !frameIsFresh.Value;
        if (stale && !diagWasStale)
            Log.Write($"frame timestamps frozen for >{Core.FreshnessConfig.Default.StaleAfterMs}ms — treating as no-frame");
        else if (!stale && diagWasStale)
            Log.Write("frame timestamps advancing again — no longer treating as no-frame");
        diagWasStale = stale;
    }

    async Task SampleAsync()
    {
        if (sampling || detector is null) return;
        // Defense in depth (0001-core-brain.md, "Sample events outside the watching window"):
        // Core already no-ops a Sample while Paused/SessionLocked, but discard here too rather
        // than pay for a frame acquisition and face-detection pass that can't matter — the
        // sampleTimer is stopped in both states anyway, so this only guards a narrow race.
        var currentStatus = Core.Policy.status(state);
        // DES-3: was an inline byte-duplicate of PolicyBridge.IsSuppressedStatus's priority-row
        // list -- that helper exists precisely so this list is never hand-copied a second time.
        if (PolicyBridge.IsSuppressedStatus(currentStatus)) return;
        sampling = true;
        try
        {
            bool haveFrame = false, present = false, dark = false;
            TimeSpan? frameTime = null;
            int rawFaceCount = 0;
            bool? frameIsFresh = null;
            if (reader is not null)
            {
                using var frame = reader.TryAcquireLatestFrame();
                frameTime = frame?.SystemRelativeTime;
                var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap is not null)
                {
                    // Frozen-frame staleness (bug fix): TryAcquireLatestFrame can re-serve a
                    // cached frame forever (known WinRT quirk). If SystemRelativeTime is
                    // unavailable, freshness cannot be assessed — frameIsFresh stays null and
                    // EffectiveHaveFrame's `true` branch below is reached unconditionally,
                    // matching this code's behavior before FrameFreshness existed.
                    if (frameTime.HasValue)
                    {
                        var freshness = Core.FrameFreshness.step(
                            Core.FreshnessConfig.Default, frameFreshnessState,
                            NowMonotonic(), frameTime.Value.Ticks);
                        frameFreshnessState = freshness.State;
                        frameIsFresh = freshness.IsFresh;
                    }
                    haveFrame = PolicyBridge.EffectiveHaveFrame(haveFrame: true, frameIsFresh: frameIsFresh ?? true);

                    if (haveFrame)
                    {
                        using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
                        dark = MeanLuma(gray) < cfg.DarkFrameMeanThreshold;
                        if (!dark)
                        {
                            var faces = await detector.DetectFacesAsync(gray);
                            rawFaceCount = faces.Count;
                            // Presence-stabilization filter (0001-core-brain.handoff.md, "Burn-in
                            // incident 2026-07-28"): stepped here, upstream of ClassifySample and
                            // Advance, so `present` is the identical stabilized signal every
                            // downstream consumer observes.
                            var box = PolicyBridge.LargestFaceBoxNormalized(
                                faces.Select(f => f.FaceBox).ToList(), (uint)gray.PixelWidth, (uint)gray.PixelHeight);
                            var filterResult = Core.PresenceFilter.step(
                                Core.FilterConfig.Default, presenceFilterState,
                                NowMonotonic(), box);
                            presenceFilterState = filterResult.State;
                            present = filterResult.StablePresence;
                        }
                    }
                    // A stale frame (haveFrame now false) falls straight through as
                    // Observation.NoFrame below — already the correct fail-open path.
                }
            }
            // `reader is null` here (a cooldown-suppressed CaptureFailed backstop —
            // 0001-core-brain.md, "Notes: recovery boundary") falls straight through as
            // haveFrame=false, i.e. Observation.NoFrame — the shell keeps sampleTimer running
            // so the NoFrame → re-evaluation backstop engages on the ordinary signal-health path,
            // with no separate mechanism. Every resume path now guarantees sampleTimer is running
            // whenever this branch can be reached (incident 2026-08-12) — the Advance-internal
            // suppression-transition rule (RFC 0002-environment-levels) restarts sampleTimer
            // unconditionally on every suppressed -> unsuppressed transition, covering both
            // OnSessionSwitch's unlock and TogglePause's resume by construction.

            // InputIdleMs no longer flows through the Sample payload (RFC 0002-environment-
            // levels): it lives in StepInputs, read fresh inside Advance's input assembly. This
            // local read is for LogSensingDiagnostics only -- a second, independent P/Invoke
            // call accepted explicitly by the RFC (the two values may differ by a few
            // milliseconds without consequence: this one is prose, Advance's is the decision
            // input).
            long idleMs = InputIdleMs();
            lastObservationDark = dark;
            Core.Event sampleEvent = PolicyBridge.ClassifySample(haveFrame, dark, present);
            LogSensingDiagnostics(haveFrame, dark, present, idleMs, rawFaceCount, frameIsFresh);
            Advance(sampleEvent);
        }
        catch (Exception ex)
        {
            Log.Write($"sample error: {ex.Message}");
        }
        finally
        {
            // Watchdog stamp — see the field comment on lastSamplePassAt: updated here, at the
            // guard's release, and nowhere else. A pass that hung inside the try block above
            // (e.g. a stuck detector.DetectFacesAsync) never reaches this finally until it
            // returns, so the stamp starves correctly for as long as the hang lasts.
            lastSamplePassAt = Environment.TickCount64;
            sampling = false;
        }
    }

    // SystemEvents raises SessionSwitch from its own broadcast thread.
    // MediaCapture init must happen on the STA/UI thread — every observed
    // FrameServer wedge followed a re-init from this handler — so marshal
    // the entire body onto the UI thread before touching the camera.
    // RFC 0002-environment-levels, "Session mirror with reconciliation": updates the
    // sessionLocked mirror and lets Advance(Reconcile) do the rest. The old pause-precedence
    // guard (a second Policy.status round-trip of the SessionUnlocked-while-paused shape) is
    // subsumed outright, not rewired: pause precedence is now the core edge rule (suppressed =
    // SessionLocked || Paused) plus the Advance-internal suppression-transition rule below --
    // there is nothing left for this call site to guard. Likewise the presence-filter/
    // freshness resets and the timer stop/start/KickAcquisitionIfNeeded that used to live here
    // ride that same Advance-internal rule now, so a WTS-reconciliation correction (slice 4)
    // gets them by construction instead of by a second hand-copied chore.
    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) =>
        ui.Post(_ =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                // DES-2/SHELL-1: SessionLockMirror.OnSessionSwitch owns the mirror-plus-skip-
                // window update -- round-3 boundary rule 2 (the skip window restarts ONLY when
                // this delivery actually changed the mirror; an idempotent duplicate
                // SessionSwitch re-delivery, which Windows demonstrably double-fires, must leave
                // the counter running, not restart it) lives there, delegating to the unchanged
                // `PolicyBridge.SessionSwitchTicksAfterDelivery`. Advance is gated on the
                // returned `changed` flag -- matching the other two mirror-changing sites (WTS
                // reconciliation gates on `CorrectionApplied`; inhibitor aggregation gates on
                // `Refresh()`'s own `changed`) -- so a duplicate/idempotent delivery, a genuine
                // no-op for this mirror, does no redundant work. A delivery that DOES change the
                // mirror always returns true here, so the suppression-exit transition (filter
                // reset, sample-timer restart, KickAcquisitionIfNeeded -- riding Advance's
                // internal transition rule) is never skipped for a real lock/unlock, only for a
                // true repeat.
                if (sessionLockMirror.OnSessionSwitch(locked: true))
                    Advance(Core.Event.Reconcile);
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                if (sessionLockMirror.OnSessionSwitch(locked: false))
                    Advance(Core.Event.Reconcile);
            }
        }, null);

    void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
        ui.Post(_ =>
        {
            // IsHandleInvalid still classifies here for this log line only (0001-core-brain.md,
            // "Notes: recovery boundary" / R2-7's args-form requirement) — the restart decision
            // itself no longer depends on the classification once InitSucceeded has occurred.
            bool handleInvalid = PolicyBridge.IsHandleInvalid(unchecked((int)args.Code), args.Message);
            Log.Write($"capture failed{(handleInvalid ? " (handle invalid)" : "")}: {args.Message}");
            // CaptureFailed is flagless: Advance requests Action.Restart CameraWedged
            // unconditionally on first occurrence post-success, subject only to
            // RecoveryCooldownMs — never gated by Paused/SessionLocked (failure events are
            // pause-immune by design, unlike Sample).
            if (Advance(Core.Event.CaptureFailed)) return; // Action.Restart executed — process exiting
            // Cooldown-suppressed backstop: tear down the dead capture (reader null) but keep
            // sampleTimer running — SampleAsync's null-reader path emits NoFrame, and the
            // ordinary signal-health machinery carries the rest (no new mechanism). The
            // Advance-internal suppression-transition rule (RFC 0002-environment-levels) starts
            // sampleTimer unconditionally on every resume/unlock, even when reader is null
            // (incident 2026-08-12), so this backstop can no longer be stranded by a pause/lock
            // that happens to land inside the restart cooldown. The sampling watchdog below is
            // the second line of defense for any other starvation path (e.g. a hung SampleAsync
            // pass) this comment doesn't cover.
            TeardownCamera();
        }, null);

    // RFC 0002-environment-levels, "Pause ownership": flips the shell-owned paused mirror and
    // calls Advance(Reconcile) -- the pause takes effect and renders synchronously with the
    // click, as before. The old resume branch's presence-filter/freshness resets and timer
    // stop/start/KickAcquisitionIfNeeded ride the Advance-internal suppression-transition rule
    // now (see Advance), so this call site no longer carries its own copy.
    void TogglePause()
    {
        paused = !paused;
        Advance(Core.Event.Reconcile);
    }

    // KickAcquisitionIfNeeded (0001-core-brain.md R2-4): the complete, core-owned gate for
    // "should we attempt (re)acquisition" — replaces the legacy shell-local
    // `!paused && !sessionLocked` booleans with the single status test shared by the retry
    // timer's Tick guard and Advance's suppression-transition rule (RFC 0002-environment-
    // levels), the latter now the single call site for every resume/unlock path.
    void KickAcquisitionIfNeeded()
    {
        if (reader is null && PolicyBridge.IsAcquiringOrRecovering(Core.Policy.status(state)))
            _ = StartWatchingAsync();
    }

    // The single Advance(Event) chokepoint (0001-core-brain.md, slice 8b cutover; RFC
    // 0002-environment-levels for the StepInputs/StepContext cutover): the only place `state`
    // is reassigned. Builds StepInputs via the one construction helper, calls Policy.step,
    // executes the returned Action, logs Policy.snapshot alongside Lock/Restart (R1-34),
    // implements the Advance-internal suppression-transition rule (round 3: filter/freshness
    // resets, unconditional sample-timer restart, and KickAcquisitionIfNeeded on a suppressed
    // -> unsuppressed transition; sample-timer stop on the reverse), logs the once-per-episode
    // dark-vs-no-frame transition into Status.NoSignal (R2-29), and re-renders tray/status +
    // pauseItem.Text from Policy.status — never from a shell-local bool. Returns true iff an
    // Action.Restart was executed (the process is exiting via RestartProcess/ExitThread) so
    // callers can skip any further work that assumes the process keeps running.
    bool Advance(Core.Event evt)
    {
        // Threading invariant (RFC 0002-environment-levels, round 3): the single-writer
        // soundness of the mirrors and `state` itself rests on every mirror mutation and
        // Advance call executing on the UI thread. Fail-loud backstop, not the enforcement
        // mechanism itself (the existing ui.Post discipline is).
        //
        // DES-1: a bare `Debug.Assert` is `[Conditional("DEBUG")]` and this app ships
        // `-c Release` with no DEBUG define, so a bare assert here compiles OUT of the shipped
        // binary entirely -- a real cross-thread call would corrupt the mirrors/registry/`state`
        // in complete silence, exactly the "expected behavior existed only as code" defect class
        // this codebase exists to eliminate. This is a genuine data-race invariant with no safe
        // continuation: `Policy.step` reassigning `state` (and any mirror mutation) off the UI
        // thread can race a concurrent UI-thread `Advance`/mirror write with no synchronization
        // between them, so a log-and-continue would let that race actually happen, just with a
        // line in the log after the fact -- strictly worse than crashing loudly before any
        // corruption occurs. Fail fast with `Environment.FailFast`, not a `throw`: three of
        // Advance's callers wrap it in a catch-all `try/catch` that logs and continues (e.g.
        // StartWatchingAsync's InitSucceeded/InitFailed paths), so a catchable exception would
        // be swallowed on exactly those paths and the "must not silently continue" guarantee
        // would hold only for some callers. FailFast is uncatchable and uniform: it bypasses
        // every enclosing catch/finally, writes to the Windows Event Log, and produces a crash
        // dump. Log first anyway, so the failure is diagnosable from Log's own file even if WER
        // capture is unavailable in some deployment; keep `Debug.Assert` alongside for its value
        // under a DEBUG-build debugger (breaks in place rather than tearing down).
        if (SynchronizationContext.Current != ui)
        {
            Log.Write("FATAL: Advance called off the UI SynchronizationContext -- the single-" +
                      "writer invariant over the mirrors/registry/state has been violated");
            Debug.Assert(false, "Advance must run on the UI SynchronizationContext");
            Environment.FailFast("Advance must run on the UI SynchronizationContext");
        }

        var inputs = BuildStepInputs();
        var ctx = new Core.StepContext(
            now: NowMonotonic(),
            nowWall: Core.WallClockMs.NewWallClockMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            inputs: inputs);
        var previousStatus = Core.Policy.status(state);

        var result = Core.Policy.step(policyConfig, state, ctx, evt);
        state = result.State;
        var newStatus = Core.Policy.status(state);

        switch (result.Action.Tag)
        {
            case Core.Action.Tags.NoAction:
                break;
            case Core.Action.Tags.Lock:
                ExecuteLock();
                break;
            case Core.Action.Tags.Restart:
                var restart = (Core.Action.Restart)result.Action;
                ExecuteRestart(restart.reason, ctx.Now);
                return true;
            default:
                throw new UnreachableException();
        }

        // Advance-internal suppression-transition rule (RFC 0002-environment-levels, round 3):
        // implemented once, here, instead of hand-copied at every mirror-changing call site.
        // Generalizes the 2026-07-28 burn-in fix (presence-filter/freshness resets) and the
        // 2026-08-12 incident's unconditional sample-timer restart (the 1.0.8.4 fix semantics)
        // to every suppressed -> unsuppressed transition Advance observes, including the
        // WTS-reconciliation corrections slice 4 adds. The decision itself is
        // PolicyBridge.SuppressionTransitionStep -- pure and independently unit-tested (the
        // harness models `step` semantics, not WinForms timers, so this is the only place the
        // sample-timer restart decision on a WTS-correction-driven unlock can be pinned) --
        // this method only executes the verdict against the real timer/filter state.
        var transition = PolicyBridge.SuppressionTransitionStep(previousStatus, newStatus);
        if (transition.TransitionLogMessage is not null)
            // Transition log lives here, not at the mirror-changing call sites, for the same
            // reason the rule itself does: one owner covers unlock, resume, and slice 4's
            // WTS-reconciliation corrections alike (the log is this project's incident-forensics
            // surface — a suppression exit must never be silent).
            Log.Write(transition.TransitionLogMessage);
        if (transition.ResetFilters)
        {
            presenceFilterState = Core.PresenceFilter.initial;
            frameFreshnessState = Core.FrameFreshness.initial;
        }
        if (transition.RestartSampleTimer) sampleTimer.Start(); // unconditional -- idempotent if already running
        if (transition.StopSampleTimer) sampleTimer.Stop();
        if (transition.KickAcquisition) KickAcquisitionIfNeeded();

        // Status/log-line mapping table (slice 8b deliverable): the once-per-episode
        // dark-vs-no-frame diagnostic fires exactly on the transition into Status.NoSignal,
        // from the shell's own current-sample classification (Core doesn't echo it back).
        if (newStatus.Tag == Core.Status.Tags.NoSignal && previousStatus.Tag != Core.Status.Tags.NoSignal)
        {
            Log.Write(lastObservationDark
                ? "camera feed dark/blocked — failing open"
                : "camera delivering no frames — failing open");
        }

        Render(newStatus);
        return false;
    }

    // Action.Lock execution discipline (0001-core-brain.md R2-18): stop sampleTimer
    // synchronously BEFORE calling LockWorkStation(), restarting it only if the Win32 call
    // fails — preserving the guard against a second timer tick re-deriving Lock in the window
    // before the real OS SessionLock notification arrives. Core takes no side-channel action on
    // a failed lock (Action.Lock is a stateless, re-derived request); the very next qualifying
    // Sample independently re-evaluates and re-emits Lock if conditions still hold.
    void ExecuteLock()
    {
        sampleTimer.Stop();
        var snap = Core.Policy.snapshot(policyConfig, state, NowMonotonic());
        Log.Write($"no face for {cfg.AwayThresholdSeconds}s, input idle — locking " +
                  $"(armed={snap.Armed} inGrace={snap.InGrace} awayForMs={snap.AwayForMs} " +
                  $"noSignalForMs={snap.NoSignalForMs} initFailStreak={snap.InitFailStreak})");
        if (!LockWorkStation())
        {
            Log.Write($"LockWorkStation failed: {Marshal.GetLastWin32Error()}");
            sampleTimer.Start();
        }
    }

    // Logs Policy.snapshot alongside the restart reason (R1-34), then hands off to the
    // mechanical RestartProcess() — RestartReason changes only the log line, never the restart
    // mechanism (0001-core-brain.md, "Shell changes").
    void ExecuteRestart(Core.RestartReason reason, Core.MonotonicMs monotonic)
    {
        var snap = Core.Policy.snapshot(policyConfig, state, monotonic);
        string message = reason.Tag switch
        {
            Core.RestartReason.Tags.CameraWedged =>
                $"camera stack wedged — restarting process to recover " +
                $"(initFailStreak={snap.InitFailStreak}, noSignalForMs={snap.NoSignalForMs})",
            Core.RestartReason.Tags.CameraReevaluation =>
                $"no usable signal — re-evaluating cameras (noSignalForMs={snap.NoSignalForMs})",
            Core.RestartReason.Tags.CameraUpgrade =>
                "preferred camera available — restarting process to switch cameras",
            _ => throw new UnreachableException(),
        };
        Log.Write(message);
        RestartProcess();
    }

    // Renders tray/status text and pauseItem.Text from Policy.status — the second render
    // target the RFC calls out explicitly as easy to drop silently (R2-... "SetStatus's second
    // render target is on the 8b checklist too"): pauseItem.Text derives from
    // Policy.status(state) = Status.Paused, never from a shell-local bool. Tray/status text is
    // re-rendered only when it actually changes (Policy.status is pulled, never pushed).
    void Render(Core.Status status)
    {
        // RFC 0002-environment-levels, "Status": the annotation is appended regardless of which
        // Status row is showing, and both halves (the bool and the names) are sourced from the
        // inhibitor registry's own cached snapshot -- never from Policy.lockInhibited (the core
        // echo exists for the verification harness/diagnostics only). Advance calls step and
        // this render synchronously back-to-back, so the registry snapshot read here is exactly
        // as current as it will ever be for this tick.
        string text = PolicyBridge.AppendInhibitionAnnotation(
            PolicyBridge.StatusText(status, lastObservationDark), inhibitorRegistry.Active, inhibitorRegistry.ActiveNames);
        if (text != lastRenderedStatusText)
        {
            lastRenderedStatusText = text;
            statusItem.Text = text;
            var tip = $"PresenceLock — {text}";
            tray.Text = tip.Length <= 63 ? tip : tip[..63];
        }
        pauseItem.Text = status.Tag == Core.Status.Tags.Paused ? "Resume" : "Pause";
    }

    // Raw text setter for the one hard-stop path outside the Status model: FaceDetector not
    // being supported on this machine at all, before any camera-acquisition attempt is even
    // made (Policy never sees an event in this case).
    void SetRawStatusText(string text)
    {
        lastRenderedStatusText = text;
        statusItem.Text = text;
        var tip = $"PresenceLock — {text}";
        tray.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    void TeardownCamera()
    {
        if (capture is not null) capture.Failed -= OnCaptureFailed;
        try { reader?.Dispose(); } catch (Exception ex) { Log.Write($"reader dispose: {ex.Message}"); }
        reader = null;
        try { capture?.Dispose(); } catch (Exception ex) { Log.Write($"capture dispose: {ex.Message}"); }
        capture = null;
        activeCameraId = null;
    }

    // Camera-arrival upgrade settle check (0001-core-brain.md addendum 2026-08-01, slice 10):
    // fires ~5s after the last DeviceWatcher.Added event in a burst. Compares the in-use
    // camera against SelectPreferredCamera's would-pick-now result over a fresh enumeration —
    // the identical ranking InitCameraAsync uses — and, on a mismatch, lets the core decide
    // via Advance(Event.BetterCameraAvailable) rather than restarting unconditionally here.
    // Only meaningful once a camera is actually in use: pre-acquisition, the retry loop
    // already re-runs selection on every attempt.
    async void CheckForBetterCamera()
    {
        if (reader is null) return;
        try
        {
            var candidates = await ColorCameraCandidatesAsync();
            if (candidates.Count == 0) return;
            var wouldPickId = PolicyBridge.SelectPreferredCamera(ToCandidateDtos(candidates), cfg.CameraNameContains);
            if (wouldPickId is not null && wouldPickId != activeCameraId)
            {
                Log.Write($"preferred camera changed (would-pick {wouldPickId} != in-use {activeCameraId}) — requesting upgrade restart");
                if (Advance(Core.Event.BetterCameraAvailable)) return; // Action.Restart executed — process exiting
            }
        }
        catch (Exception ex)
        {
            Log.Write($"device-arrival camera check failed: {ex.Message}");
        }
    }

    async void OpenSettings()
    {
        if (settingsOpen) return;
        settingsOpen = true;
        try
        {
            List<string> cameraNames;
            try
            {
                var groups = await MediaFrameSourceGroup.FindAllAsync();
                cameraNames = groups
                    .Where(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color))
                    .Select(g => g.DisplayName)
                    .Distinct()
                    .ToList();
            }
            catch
            {
                cameraNames = new List<string>();
            }

            using var form = new SettingsForm(cfg, cameraNames);
            if (form.ShowDialog() != DialogResult.OK || form.Result is null) return;

            var oldFilter = cfg.CameraNameContains;
            cfg = form.Result;
            SaveConfig(cfg);
            // Live config (0001-core-brain.md, "Notes"): PolicyConfig is read live on every
            // step, so a committed Settings change must be visible on the very next sample too,
            // via the same shared mapping function file-load uses (BuildPolicyConfig) — the
            // Settings commit path routes through it already; nothing else to wire here.
            policyConfig = PolicyBridge.BuildPolicyConfig(cfg);
            sampleTimer.Interval = cfg.SampleIntervalMs;
            Log.Write($"config updated (threshold {cfg.AwayThresholdSeconds}s, " +
                      $"input idle {cfg.InputIdleSeconds}s, sample {cfg.SampleIntervalMs}ms, " +
                      $"camera filter '{cfg.CameraNameContains}')");
            if (!string.Equals(oldFilter, cfg.CameraNameContains, StringComparison.OrdinalIgnoreCase))
            {
                // Switching cameras needs a re-init, and in-process re-init wedges the
                // FrameServer on this machine — restart the whole process instead; a fresh
                // process's first init is reliable. Shell-only mechanical decision (trivial
                // string comparison, not policy) — not routed through Policy, and
                // RestartReason is not extended for it (0001-core-brain.md, "Shell changes").
                Log.Write("camera filter changed — restarting process to switch cameras");
                RestartProcess();
            }
        }
        finally
        {
            settingsOpen = false;
        }
    }

    static Icon LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(path)) return new Icon(path, 16, 16);
        }
        catch (Exception ex)
        {
            Log.Write($"tray icon load failed: {ex.Message}");
        }
        return SystemIcons.Application;
    }

    // Mechanical restart, shared by every restart trigger — core-driven (via ExecuteRestart)
    // and shell-driven (the Settings camera-filter-change path) alike: RestartReason changes
    // only the log line, never this mechanism. Ordering audited per 0001-core-brain.md
    // R1-23/R2-25: stop timers → write stamps/paused flag → release mutex → spawn →
    // ExitThread() — timers stop before the mutex is released and the new process is spawned
    // (closing the handoff window where both processes could theoretically be live
    // simultaneously), and the stamps-file write completes before Process.Start() (the
    // child's Policy.start correctness depends on what is on disk at spawn time).
    void RestartProcess()
    {
        sampleTimer.Stop();
        retryTimer.Stop();
        deviceSettleTimer.Stop();

        // RFC 0002-environment-levels, "Pause ownership": reads the shell's own pause mirror
        // directly -- no more round-tripping through Policy.status.
        bool isPaused = paused;
        var snap = Core.Policy.snapshot(policyConfig, state, NowMonotonic());
        // The shell writes only the stamp for the reason it executed (R1-29): Policy.step
        // itself only ever bumps the one RestartStamps field matching the fired reason, so
        // snap.LastWedgeRestartAt/LastReevalRestartAt already carry that property — a plain
        // round-trip here can't poison the other reason's cooldown.
        var stamps = new Core.RestartStamps(
            wedgeAt: snap.LastWedgeRestartAt, reevalAt: snap.LastReevalRestartAt, upgradeAt: snap.LastUpgradeRestartAt);
        PolicyBridge.SaveRestartStamps(stamps, isPaused);

        Program.SingleInstance?.ReleaseMutex();
        Program.SingleInstance?.Dispose();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        ExitThread();
    }

    static void SaveConfig(Config c)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PresenceLock");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "presencelock.json"),
                JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"config save failed: {ex.Message}");
        }
    }

    void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"open log failed: {ex.Message}");
        }
    }

    protected override void ExitThreadCore()
    {
        // Shutdown/exiting guard (RFC 0002-environment-levels, "Media provider"): set FIRST,
        // before any other teardown step, so a MediaInhibitorProvider.InitializeAsync
        // continuation that lands concurrently with (or after) this method observes it and
        // no-ops rather than writing its cached manager field into a WatcherContext that is
        // going away. Reached alike by tray Exit and by RestartProcess's own ExitThread() call.
        exiting = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        sampleTimer.Stop();
        watchdogTimer.Stop();
        retryTimer.Stop();
        deviceSettleTimer.Stop();
        try { deviceWatcher.Stop(); } catch (Exception ex) { Log.Write($"device watcher stop: {ex.Message}"); }
        TeardownCamera();
        tray.Visible = false;
        tray.Dispose();
        Log.Write("exited");
        base.ExitThreadCore();
    }
}
