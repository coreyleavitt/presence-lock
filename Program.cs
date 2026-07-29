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
    public int SampleIntervalMs { get; init; } = 500;
    public double GraceSeconds { get; init; } = 10.0;
    // Also require this much keyboard/mouse idle time before locking, so a
    // detector blinded by bad light can't lock out an actively working user.
    public double InputIdleSeconds { get; init; } = 10.0;
    // Empty = automatic: prefer an external USB camera, else the built-in front camera.
    public string CameraNameContains { get; init; } = "";
    // Mean 8-bit luminance below this is a blocked/dark camera, not an empty room.
    public double DarkFrameMeanThreshold { get; init; } = 6.0;

    // Five new optional PolicyConfig-mapped fields (rfc-core-brain.md, "Config" mapping
    // table). Existing on-disk files simply lack these keys and get these defaults, exactly
    // like every other absent field today — the schema stays backward-compatible. Consumed
    // only by PolicyBridge.BuildPolicyConfig (shadow path); no legacy decision reads them.
    public long NoSignalReportAfterMs { get; init; } = 10000;
    public long ReevaluateAfterMs { get; init; } = 20000;
    public long ReevaluateCooldownMs { get; init; } = 600000;
    public int RecoveryFailureThreshold { get; init; } = 3;
    public long RecoveryCooldownMs { get; init; } = 600000;

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
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
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
    // Consecutive unusable samples before surfacing it in the tray.
    const int NoFrameReportThreshold = 20;
    // Consecutive unusable samples between camera re-evaluations (~20s at 500ms),
    // so docking/undocking moves the watcher to the right camera on its own.
    const int CameraReinitSamples = 40;

    Config cfg;
    // Shadow-mode core (rfc-core-brain.md, slice 8a): runs alongside the legacy decision
    // logic below on the same event stream, never executes an Action, and only ever logs
    // divergences. `policyConfig` is rebuilt whenever `cfg` changes (Settings commit) so the
    // shadow reads live config exactly like a real Advance would.
    Core.PolicyConfig policyConfig;
    Core.State shadowState;
    // Presence-stabilization filter (rfc-core-brain.handoff.md, "Burn-in incident 2026-07-28"):
    // raw per-frame FaceDetector output flickers false-positive on an empty scene under a
    // hunting auto-framing crop. Stepped in SampleAsync before ClassifySample/legacy/shadow
    // logic, so every downstream consumer sees the identical stabilized presence signal.
    // Reset to `Core.PresenceFilter.initial` wherever a fresh camera acquisition succeeds — see
    // `StartWatchingAsync` — since spatial coherence measured against a previous acquisition's
    // frames must never carry into a new one.
    Core.FilterState presenceFilterState;
    readonly Dictionary<string, int> shadowLogCounts = new();
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem statusItem;
    readonly ToolStripMenuItem pauseItem;
    readonly WinFormsTimer sampleTimer;
    readonly WinFormsTimer retryTimer;
    readonly SynchronizationContext ui;

    FaceDetector? detector;
    MediaCapture? capture;
    MediaFrameReader? reader;

    long lastFaceTick;
    long graceUntilTick;
    int noSignalStreak;
    int initFailStreak;
    byte[]? lumaBuf;
    bool paused;
    bool sessionLocked;
    bool sampling;
    bool starting;
    bool settingsOpen;
    // Locking is disabled until presence has been confirmed at least once per
    // camera acquisition: "never saw you" must fail open, only "lost you" locks.
    bool armed;

    // Sensing diagnostics: the log records decisions only, so a feed that keeps
    // "seeing" a face produces total silence while locks never fire. Logs
    // observation-kind transitions and frozen frame timestamps (a pipeline that
    // stops delivering frames makes TryAcquireLatestFrame re-serve the last
    // cached frame — indistinguishable from live presence without this).
    Core.Observation? diagLastObs;
    TimeSpan? diagLastFrameTime;
    int diagStaleRun;
    bool diagStaleLogged;

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

    public WatcherContext()
    {
        cfg = Config.Load();
        policyConfig = PolicyBridge.BuildPolicyConfig(cfg);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;

        statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
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
            if (!paused && !sessionLocked && reader is null) await StartWatchingAsync();
        };

        SystemEvents.SessionSwitch += OnSessionSwitch;

        Log.Write($"started (threshold {cfg.AwayThresholdSeconds}s, sample {cfg.SampleIntervalMs}ms)");

        // Policy.start is called exactly once per process, before the first camera-
        // acquisition attempt (rfc-core-brain.md, Policy.start doc comment). Stamps come from
        // the RFC's new stamps file if one exists, else empty — nothing writes that file yet
        // in shadow mode (8b/8c wire real Action.Restart execution and persistence).
        shadowState = Core.Policy.start(
            Core.MonotonicMs.NewMonotonicMs(Environment.TickCount64),
            PolicyBridge.LoadRestartStamps());
        presenceFilterState = Core.PresenceFilter.initial;

        _ = StartWatchingAsync();
    }

    async Task StartWatchingAsync()
    {
        if (starting) return;
        starting = true;
        long now = Environment.TickCount64;
        lastFaceTick = now;
        graceUntilTick = now + (long)(cfg.GraceSeconds * 1000);
        noSignalStreak = 0;
        armed = false;
        // Fresh camera = fresh filter: spatial coherence measured against the previous
        // acquisition's frames must never carry into this one.
        presenceFilterState = Core.PresenceFilter.initial;
        try
        {
            if (!FaceDetector.IsSupported)
            {
                Log.Write("FaceDetector.IsSupported == false — idle");
                SetStatus("Face detection unsupported — idle");
                return;
            }
            detector ??= await FaceDetector.CreateAsync();
            if (reader is null) await InitCameraAsync();
            initFailStreak = 0;
            // Baseline the grace window from successful acquisition, not from
            // when this attempt began — slow init must not consume the grace.
            now = Environment.TickCount64;
            lastFaceTick = now;
            graceUntilTick = now + (long)(cfg.GraceSeconds * 1000);
            ShadowAdvance(Core.Event.InitSucceeded);
            sampleTimer.Start();
            SetStatus("Watching");
        }
        catch (Exception ex)
        {
            initFailStreak++;
            Log.Write($"start failed ({initFailStreak}, hr=0x{ex.HResult:X8}, t{Environment.CurrentManagedThreadId}): {ex.Message.Trim()}");
            TeardownCamera();

            // Persistent E_HANDLE from a fresh MediaCapture means this
            // process's connection to the camera FrameServer is wedged
            // (seen after lock/unlock cycles); only a new process recovers.
            // Match on message too: the WinRT projection wraps the error and
            // does not always preserve the E_HANDLE HResult.
            // A persisted 10-minute cooldown lets recovery fire repeatedly over
            // time while stopping a broken camera from causing a restart loop.
            bool handleInvalid = PolicyBridge.IsHandleInvalid(ex);
            ShadowAdvance(Core.Event.NewInitFailed(handleInvalid));
            if (initFailStreak >= 3 && handleInvalid && RestartCooldownElapsed())
            {
                Log.Write("camera stack wedged — restarting process to recover");
                MarkRestart();
                RestartProcess();
                return;
            }

            SetStatus("Camera unavailable — retrying");
            retryTimer.Start();
        }
        finally
        {
            starting = false;
        }
    }

    // Rapid teardown → re-init cycles wedge the FrameServer connection
    // (persistent E_HANDLE); always let the capture stack settle between them.
    async Task RestartWatchingAsync()
    {
        sampleTimer.Stop();
        await TeardownCameraAsync();
        await Task.Delay(1500);
        if (!paused && !sessionLocked) await StartWatchingAsync();
    }

    // Stop the frame reader gracefully before disposing; hard-disposing an
    // actively streaming reader is another suspected wedge trigger.
    async Task TeardownCameraAsync()
    {
        var r = reader;
        if (r is not null)
        {
            try { await r.StopAsync(); }
            catch (Exception ex) { Log.Write($"reader stop: {ex.Message}"); }
        }
        TeardownCamera();
    }

    async Task InitCameraAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var candidates = groups
            .SelectMany(g => g.SourceInfos.Select(i => (Group: g, Info: i)))
            .Where(t => t.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("no color camera found");

        (MediaFrameSourceGroup Group, MediaFrameSourceInfo Info) chosen = default;
        if (!string.IsNullOrWhiteSpace(cfg.CameraNameContains))
            chosen = candidates.FirstOrDefault(t =>
                t.Group.DisplayName.Contains(cfg.CameraNameContains, StringComparison.OrdinalIgnoreCase));
        if (chosen.Group is null)
            chosen = candidates.OrderBy(t => SelectionRank(t.Info)).First();

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

    // External USB webcams report no enclosure panel; prefer them over the
    // built-in camera, which sees only the lid when the laptop is docked closed.
    static int SelectionRank(MediaFrameSourceInfo info)
    {
        var loc = info.DeviceInformation?.EnclosureLocation;
        if (loc is null || loc.Panel == EnclosurePanel.Unknown) return 0;
        return loc.Panel == EnclosurePanel.Front ? 1 : 2;
    }

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
    // is still visible in the log (rfc-core-brain.handoff.md, "Burn-in incident 2026-07-28").
    void LogSensingDiagnostics(bool haveFrame, bool dark, bool present, TimeSpan? frameTime, long idleMs, int rawFaceCount)
    {
        var obs = PolicyBridge.ClassifyObservation(haveFrame, dark, present);
        if (diagLastObs is null || !obs.Equals(diagLastObs))
        {
            Log.Write($"observation: {diagLastObs?.ToString() ?? "(start)"} -> {obs} (idle {idleMs}ms, raw={rawFaceCount} faces)");
            diagLastObs = obs;
        }
        if (!haveFrame || frameTime is null) return;
        if (frameTime == diagLastFrameTime)
        {
            diagStaleRun++;
            if (diagStaleRun == 10 && !diagStaleLogged)
            {
                diagStaleLogged = true;
                Log.Write($"frame timestamp frozen for {diagStaleRun} consecutive samples (t={frameTime}) — stale feed suspected");
            }
        }
        else
        {
            if (diagStaleLogged)
                Log.Write($"frame timestamp advancing again after {diagStaleRun} stale samples");
            diagStaleRun = 0;
            diagStaleLogged = false;
            diagLastFrameTime = frameTime;
        }
    }

    async Task SampleAsync()
    {
        if (sampling || paused || sessionLocked || reader is null || detector is null) return;
        sampling = true;
        try
        {
            bool haveFrame = false, present = false, dark = false;
            TimeSpan? frameTime = null;
            int rawFaceCount = 0;
            using (var frame = reader.TryAcquireLatestFrame())
            {
                frameTime = frame?.SystemRelativeTime;
                var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap is not null)
                {
                    haveFrame = true;
                    using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
                    dark = MeanLuma(gray) < cfg.DarkFrameMeanThreshold;
                    if (!dark)
                    {
                        var faces = await detector.DetectFacesAsync(gray);
                        rawFaceCount = faces.Count;
                        // Presence-stabilization filter (rfc-core-brain.handoff.md, "Burn-in
                        // incident 2026-07-28"): stepped here, upstream of ClassifySample and
                        // the legacy decision below, so `present` is the identical stabilized
                        // signal every downstream consumer (legacy and shadow alike) observes.
                        var box = PolicyBridge.LargestFaceBoxNormalized(
                            faces.Select(f => f.FaceBox).ToList(), (uint)gray.PixelWidth, (uint)gray.PixelHeight);
                        var filterResult = Core.PresenceFilter.step(Core.FilterConfig.Default, presenceFilterState, box);
                        presenceFilterState = filterResult.State;
                        present = filterResult.StablePresence;
                    }
                }
            }

            long now = Environment.TickCount64;
            // Shadow-only read (rfc-core-brain.md, slice 8a): the shared Sample event always
            // carries an inputIdleMs value, but legacy's own lock check below only reads idle
            // time lazily, inside its short-circuited &&-chain. GetLastInputInfo is a
            // stateless, side-effect-free query, so reading it here too does not change
            // legacy's observable behavior — it only feeds the shadow core.
            long shadowInputIdleMs = InputIdleMs();
            Core.Event shadowSampleEvent = PolicyBridge.ClassifySample(haveFrame, dark, present, shadowInputIdleMs);
            LogSensingDiagnostics(haveFrame, dark, present, frameTime, shadowInputIdleMs, rawFaceCount);

            if (!haveFrame || dark)
            {
                // Fail open: no frame (privacy shutter, exclusive use elsewhere,
                // warm-up) or a black feed (lid-closed internal camera, capped
                // lens) must never lock the machine.
                lastFaceTick = now;
                noSignalStreak++;
                if (noSignalStreak == NoFrameReportThreshold)
                {
                    Log.Write(dark ? "camera feed dark/blocked — failing open"
                                   : "camera delivering no frames — failing open");
                    SetStatus(dark ? "Camera dark/blocked — not locking"
                                   : "No camera frames — not locking");
                }
                if (noSignalStreak % CameraReinitSamples == 0)
                {
                    Log.Write("no usable signal — re-evaluating cameras");
                    _ = RestartWatchingAsync();
                }
                // Core never emits Action.Lock for NoFrame/DarkFrame (fail-open by design),
                // so legacy's would-lock is definitionally false here — no comparison needed.
                ShadowAdvance(shadowSampleEvent, legacyWouldLock: false);
                return;
            }
            if (noSignalStreak >= NoFrameReportThreshold) SetStatus("Watching");
            noSignalStreak = 0;

            bool legacyWouldLock = false;
            if (present)
            {
                if (!armed)
                {
                    armed = true;
                    Log.Write("presence confirmed — armed");
                }
                lastFaceTick = now;
            }
            else
            {
                legacyWouldLock = armed && now >= graceUntilTick &&
                    now - lastFaceTick >= (long)(cfg.AwayThresholdSeconds * 1000) &&
                    InputIdleMs() >= (long)(cfg.InputIdleSeconds * 1000);
                if (legacyWouldLock)
                {
                    Log.Write($"no face for {cfg.AwayThresholdSeconds}s, input idle — locking");
                    sessionLocked = true;
                    sampleTimer.Stop();
                    if (!LockWorkStation())
                    {
                        sessionLocked = false;
                        Log.Write($"LockWorkStation failed: {Marshal.GetLastWin32Error()}");
                        sampleTimer.Start();
                    }
                }
            }
            ShadowAdvance(shadowSampleEvent, legacyWouldLock);
        }
        catch (Exception ex)
        {
            Log.Write($"sample error: {ex.Message}");
        }
        finally
        {
            sampling = false;
        }
    }

    // SystemEvents raises SessionSwitch from its own broadcast thread.
    // MediaCapture init must happen on the STA/UI thread — every observed
    // FrameServer wedge followed a re-init from this handler — so marshal
    // the entire body onto the UI thread before touching the camera.
    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) =>
        ui.Post(async _ =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                sessionLocked = true;
                sampleTimer.Stop();
                SetStatus("Session locked — watching paused");
                ShadowAdvance(Core.Event.SessionLocked);
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                sessionLocked = false;
                // Fed to the shadow core unconditionally, even while paused: the
                // pause-precedence rule (SessionUnlocked while paused is a no-op) is core-
                // owned, not shell-owned — see rfc-core-brain.md's pause/unlock truth table.
                ShadowAdvance(Core.Event.SessionUnlocked);
                if (paused) return;
                if (reader is not null)
                {
                    // The camera deliberately stays alive across the lock:
                    // on this machine, tearing down and re-initializing
                    // MediaCapture wedges the FrameServer service itself
                    // (persistent E_HANDLE for every process until an elevated
                    // service restart). Initialize once, never re-initialize.
                    Log.Write("resumed after unlock (camera kept alive)");
                    ResumeSampling();
                }
                else
                {
                    await RestartWatchingAsync();
                }
            }
        }, null);

    void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
        ui.Post(_ =>
        {
            Log.Write($"capture failed: {args.Message}");
            // CaptureFailed is flagless (rfc-core-brain.md, "Notes: recovery boundary"): the
            // restart decision no longer depends on classification once InitSucceeded has
            // occurred. IsHandleInvalid(args.Code, args.Message) still exists and is unit-
            // tested (R2-7's args-form requirement) for the log-line mapping 8b adds.
            ShadowAdvance(Core.Event.CaptureFailed);
            TeardownCamera();
            if (!paused && !sessionLocked)
            {
                SetStatus("Camera failed — retrying");
                retryTimer.Start();
            }
        }, null);

    void TogglePause()
    {
        paused = !paused;
        if (paused)
        {
            sampleTimer.Stop();
            retryTimer.Stop();
            SetStatus("Paused — camera kept open");
            Log.Write("paused");
            ShadowAdvance(Core.Event.Paused);
        }
        else
        {
            Log.Write("resumed");
            ShadowAdvance(Core.Event.Resumed);
            if (reader is not null) ResumeSampling();
            else _ = StartWatchingAsync();
        }
    }

    // Re-baseline and resume sampling on an already-initialized camera.
    void ResumeSampling()
    {
        long t = Environment.TickCount64;
        lastFaceTick = t;
        graceUntilTick = t + (long)(cfg.GraceSeconds * 1000);
        armed = false;
        noSignalStreak = 0;
        sampleTimer.Start();
        SetStatus("Watching");
    }

    // Shadow-mode chokepoint (rfc-core-brain.md, slice 8a): every call site feeds the same
    // Event the real 8b Advance will eventually consume, via the shared event-construction
    // functions in PolicyBridge. This method holds its own core State and NEVER executes the
    // returned Action — legacy remains in full control of all real behavior. `legacyWouldLock`
    // is non-null only at the Sample call site; the RFC scopes divergence to exactly the
    // would-lock boolean, so every other call site passes null and skips the comparison.
    void ShadowAdvance(Core.Event evt, bool? legacyWouldLock = null)
    {
        long now = Environment.TickCount64;
        long nowWallMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var monotonic = Core.MonotonicMs.NewMonotonicMs(now);
        var result = Core.Policy.step(
            policyConfig, shadowState, monotonic, Core.WallClockMs.NewWallClockMs(nowWallMs), evt);
        shadowState = result.State;

        // F#-generated DU cases without data (NoAction/Lock) are exposed as static properties
        // of the union's own type, so `case Core.Action.Lock:` cannot be used as a C# type
        // pattern here (it resolves to the property, not a distinct type — CS9135). Switch on
        // the generated `.Tag`/`Tags` int constants instead; `Action.Restart` still downcasts
        // for its `reason` field.
        bool shadowWouldLock = false;
        switch (result.Action.Tag)
        {
            case Core.Action.Tags.NoAction:
                break;
            case Core.Action.Tags.Lock:
                shadowWouldLock = true;
                break;
            case Core.Action.Tags.Restart:
                // Restart-shaped decisions have no legacy analog by design (the RFC's two
                // deliberate behavior changes) — logged, never counted as a divergence.
                var restart = (Core.Action.Restart)result.Action;
                LogShadowExpected(restart.reason);
                break;
            default:
                throw new UnreachableException();
        }

        if (legacyWouldLock is bool legacyLock && legacyLock != shadowWouldLock)
        {
            var snap = Core.Policy.snapshot(policyConfig, shadowState, monotonic);
            string kind = shadowWouldLock ? "shadow-lock-legacy-noaction" : "legacy-lock-shadow-noaction";
            LogDivergence(kind,
                $"legacyWouldLock={legacyLock} shadowWouldLock={shadowWouldLock} " +
                $"armed={snap.Armed} inGrace={snap.InGrace} awayForMs={snap.AwayForMs} " +
                $"noSignalForMs={snap.NoSignalForMs} initFailStreak={snap.InitFailStreak}");
        }
    }

    static string RestartReasonTag(Core.RestartReason reason)
    {
        switch (reason.Tag)
        {
            case Core.RestartReason.Tags.CameraWedged: return "CameraWedged";
            case Core.RestartReason.Tags.CameraReevaluation: return "CameraReevaluation";
            default: throw new UnreachableException();
        }
    }

    void LogShadowExpected(Core.RestartReason reason)
    {
        string kind = $"SHADOW-EXPECTED[{RestartReasonTag(reason)}]";
        LogShadowOnce(kind, $"{kind}: shadow core requested Restart — no legacy analog, not a divergence");
    }

    void LogDivergence(string kind, string detail) =>
        LogShadowOnce($"SHADOW-DIVERGENCE[{kind}]", $"SHADOW-DIVERGENCE[{kind}]: {detail}");

    // Repeat-suppression (rfc-core-brain.md, slice 8a): the first occurrence of each distinct
    // kind this process run is logged in full; every later occurrence logs only a short
    // count line, so a cascading divergence can't blow the 1 MB log cap and evict its own
    // most diagnostic first occurrence.
    void LogShadowOnce(string kind, string fullDetail)
    {
        if (shadowLogCounts.TryGetValue(kind, out int count))
        {
            count++;
            shadowLogCounts[kind] = count;
            Log.Write($"{kind} (repeat #{count})");
        }
        else
        {
            shadowLogCounts[kind] = 1;
            Log.Write(fullDetail);
        }
    }

    void TeardownCamera()
    {
        if (capture is not null) capture.Failed -= OnCaptureFailed;
        try { reader?.Dispose(); } catch (Exception ex) { Log.Write($"reader dispose: {ex.Message}"); }
        reader = null;
        try { capture?.Dispose(); } catch (Exception ex) { Log.Write($"capture dispose: {ex.Message}"); }
        capture = null;
    }

    void SetStatus(string text)
    {
        statusItem.Text = text;
        pauseItem.Text = paused ? "Resume" : "Pause";
        var tip = $"PresenceLock — {text}";
        tray.Text = tip.Length <= 63 ? tip : tip[..63];
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
            // Live config (rfc-core-brain.md, "Notes"): PolicyConfig is read live on every
            // step, so the shadow core must see a committed Settings change on the very next
            // sample too, via the same shared mapping function file-load uses.
            policyConfig = PolicyBridge.BuildPolicyConfig(cfg);
            sampleTimer.Interval = cfg.SampleIntervalMs;
            Log.Write($"config updated (threshold {cfg.AwayThresholdSeconds}s, " +
                      $"input idle {cfg.InputIdleSeconds}s, sample {cfg.SampleIntervalMs}ms, " +
                      $"camera filter '{cfg.CameraNameContains}')");
            if (!string.Equals(oldFilter, cfg.CameraNameContains, StringComparison.OrdinalIgnoreCase))
            {
                // Switching cameras needs a re-init, and in-process re-init
                // wedges the FrameServer on this machine — restart the whole
                // process instead; a fresh process's first init is reliable.
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

    void RestartProcess()
    {
        Program.SingleInstance?.ReleaseMutex();
        Program.SingleInstance?.Dispose();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        ExitThread();
    }

    static readonly string RestartStampPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "last-restart.txt");

    static bool RestartCooldownElapsed()
    {
        try
        {
            return !File.Exists(RestartStampPath) ||
                DateTime.UtcNow - File.GetLastWriteTimeUtc(RestartStampPath) >= TimeSpan.FromMinutes(10);
        }
        catch
        {
            return false;
        }
    }

    static void MarkRestart()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RestartStampPath)!);
            File.WriteAllText(RestartStampPath, DateTime.UtcNow.ToString("o"));
        }
        catch
        {
            // Failing to stamp must not block recovery.
        }
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
        retryTimer.Stop();
        TeardownCamera();
        tray.Visible = false;
        tray.Dispose();
        Log.Write("exited");
        base.ExitThreadCore();
    }
}
