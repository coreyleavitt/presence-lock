using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
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
        SingleInstance = new Mutex(initiallyOwned: true, @"Local\PresenceLock", out bool createdNew);
        if (!createdNew) return;

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
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the watcher down.
        }
    }
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
    // reconciliation" / "Pause ownership"): the shell owns these as plain bools, sampled fresh
    // into a `Core.StepInputs` on every `Advance` call via `BuildStepInputs` -- never folded
    // into `Core.State` itself. `sessionLocked` starts false; the startup WTS query that would
    // populate it correctly on a restart-while-locked process is slice 4, out of scope here.
    // `paused` starts from `ConsumePersistedPausedFlag` in the constructor.
    bool sessionLocked;
    bool paused;
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

    static long InputIdleMs()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        // On failure report zero idle time: fail open, never lock on bad data.
        if (!GetLastInputInfo(ref lii)) return 0;
        return unchecked((uint)Environment.TickCount - lii.dwTime);
    }

    // The ONE StepInputs-construction site (RFC 0002-environment-levels, "Construction
    // discipline"): used by Advance's input assembly and the startup Policy.start call, always
    // via named arguments -- the mitigation for StepInputs' three adjacent same-typed bools,
    // where a positional call could silently transpose two of them. LockInhibited is
    // hard-coded false until slice 5 wires the inhibitor registry aggregate in.
    Core.StepInputs BuildStepInputs() =>
        new(
            sessionLocked: sessionLocked,
            paused: paused,
            lockInhibited: false, // slice 5: LockInhibitorRegistry.Active
            inputIdleMs: InputIdleMs());

    public WatcherContext()
    {
        cfg = Config.Load();
        policyConfig = PolicyBridge.BuildPolicyConfig(cfg);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;

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
            // Live from cfg (not captured once) so a Settings change to SampleIntervalMs is
            // picked up on the very next tick.
            long starvationMs = Math.Max(10L * cfg.SampleIntervalMs, 15000);
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
                if (Advance(Core.Event.CaptureFailed)) return; // Action.Restart executed — process exiting
            }
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

        // Paused-flag consume-and-clear (0001-core-brain.md R2-11 / RFC 0002-environment-levels
        // "Pause ownership", pinned lifecycle): read → set the paused mirror → build the
        // initial StepInputs via the ONE helper → single Policy.start call, before the first
        // camera-acquisition attempt. Written only by RestartProcess, immediately before spawn
        // — so pause survives every self-restart, but a tray Exit or a normal launch never
        // inherits a stale pause. Inheritance is now ordinary input passing into Policy.start
        // (the refeed half of the old protocol -- an Event.Paused injection after start -- is
        // deleted): a restart-while-paused process renders Paused from its very first frame,
        // computed by Policy.start directly from these initial inputs. The sessionLocked mirror
        // starts false — the startup WTS query that would populate it correctly is slice 4.
        paused = PolicyBridge.ConsumePersistedPausedFlag();
        state = Core.Policy.start(
            Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64),
            PolicyBridge.LoadRestartStamps(),
            BuildStepInputs());
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

            var snap = Core.Policy.snapshot(policyConfig, state, Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64));
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
        if (currentStatus.Tag == Core.Status.Tags.Paused || currentStatus.Tag == Core.Status.Tags.SessionLocked) return;
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
                            Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64), frameTime.Value.Ticks);
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
                                Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64), box);
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
                sessionLocked = true;
                Advance(Core.Event.Reconcile);
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                sessionLocked = false;
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
        Debug.Assert(SynchronizationContext.Current == ui, "Advance must run on the UI SynchronizationContext");

        var inputs = BuildStepInputs();
        var ctx = new Core.StepContext(
            now: Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64),
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
        // WTS-reconciliation corrections slice 4 adds.
        bool wasSuppressed = PolicyBridge.IsSuppressedStatus(previousStatus);
        bool isSuppressed = PolicyBridge.IsSuppressedStatus(newStatus);
        if (wasSuppressed && !isSuppressed)
        {
            // Transition log lives here, not at the mirror-changing call sites, for the same
            // reason the rule itself does: one owner covers unlock, resume, and slice 4's
            // WTS-reconciliation corrections alike (the log is this project's incident-forensics
            // surface — a suppression exit must never be silent).
            Log.Write($"suppression exited: {previousStatus} -> {newStatus}");
            presenceFilterState = Core.PresenceFilter.initial;
            frameFreshnessState = Core.FrameFreshness.initial;
            sampleTimer.Start(); // unconditional -- idempotent if already running
            KickAcquisitionIfNeeded();
        }
        else if (!wasSuppressed && isSuppressed)
        {
            Log.Write($"suppression entered: {previousStatus} -> {newStatus}");
            sampleTimer.Stop();
        }

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
        var snap = Core.Policy.snapshot(policyConfig, state, Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64));
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
        string text = PolicyBridge.StatusText(status, lastObservationDark);
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
        var snap = Core.Policy.snapshot(policyConfig, state, Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64));
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
