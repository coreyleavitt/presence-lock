using System.Diagnostics;
using System.Text.Json;
using Windows.Graphics.Imaging;
using EnclosurePanel = Windows.Devices.Enumeration.Panel;
using Core = PresenceLock.Core;

namespace PresenceLock;

/// Pure, shell-side helpers that bridge legacy sensing data to the `PresenceLock.Core` API
/// (rfc-core-brain.md, slice 8a). Everything here is sensing/mapping, not policy — it stays
/// on the C# side of the boundary by design (RFC "Notes: recovery boundary" /
/// "Config: file schema, mapping, validation") — but each function is exactly the class of
/// "expected behavior existed only as code" defect the RFC's Motivation cites, so each is
/// named and unit-tested (see PresenceLock.Tests) rather than left inline.
static class PolicyBridge
{
    /// Built-in defaults for all nine `PolicyConfig` fields (the ninth, `UpgradeCooldownMs`,
    /// added by the 2026-08-01 addendum), applied as a unit (never a partial mix —
    /// rfc-core-brain.md, "Config" validation rules) whenever the persisted values fail
    /// validation. Matches the pinned mapping table exactly.
    static readonly Core.PolicyConfig DefaultPolicyConfig = new(
        awayThresholdMs: 5000,
        inputIdleRequiredMs: 10000,
        graceMs: 10000,
        noSignalReportAfterMs: 10000,
        reevaluateAfterMs: 20000,
        reevaluateCooldownMs: 600000,
        recoveryFailureThreshold: 3,
        recoveryCooldownMs: 600000,
        upgradeCooldownMs: 600000);

    /// Maps the flat, on-disk `Config` (unchanged schema) to `PolicyConfig`, per the pinned
    /// mapping table. Validates the nine policy fields as a single unit — any one out of
    /// range falls the whole set back to `DefaultPolicyConfig`, never a partially-defaulted
    /// mix (rfc-core-brain.md: "a zero-filled cooldown would make the next camera hiccup an
    /// instant restart loop"). Sensing fields (`CameraNameContains`, `DarkFrameMeanThreshold`,
    /// `SampleIntervalMs`) are untouched by this function and never affected by a policy-field
    /// failure — except that `SampleIntervalMs` is read (not written) as one input to the
    /// away-threshold floor below, the one place a sensing field and a policy field interact.
    /// Shared by file load and (once the Settings dialog wires it) the commit path, per the
    /// RFC — same function, so an invalid committed value can never reach `step` unvalidated.
    internal static Core.PolicyConfig BuildPolicyConfig(Config cfg)
    {
        long awayMs = (long)(cfg.AwayThresholdSeconds * 1000);
        long idleMs = (long)(cfg.InputIdleSeconds * 1000);
        long graceMs = (long)(cfg.GraceSeconds * 1000);
        long noSignalMs = cfg.NoSignalReportAfterMs;
        long reevaluateAfterMs = cfg.ReevaluateAfterMs;
        long reevaluateCooldownMs = cfg.ReevaluateCooldownMs;
        int recoveryThreshold = cfg.RecoveryFailureThreshold;
        long recoveryCooldownMs = cfg.RecoveryCooldownMs;
        long upgradeCooldownMs = cfg.UpgradeCooldownMs;

        // Bug fix (false-lock window): worst-case re-stabilization after one dropped detection
        // is ~one sample gap plus the presence filter's MinCoherentMs dwell. If AwayThresholdMs
        // could sit below that, a single dropped detection under fast sampling could make the
        // away clock fire before presence has had a chance to re-stabilize -- a false lock
        // while the user never left. This makes that emergent interaction impossible by
        // construction, the same way the other eight fields are validated as a unit.
        long awayFloorMs = Core.FilterConfig.Default.MinCoherentMs + 2 * cfg.SampleIntervalMs;

        bool valid =
            awayMs > 0 && idleMs > 0 && graceMs > 0 &&
            noSignalMs > 0 && reevaluateAfterMs > 0 && reevaluateCooldownMs > 0 &&
            recoveryCooldownMs > 0 && recoveryThreshold >= 1 && upgradeCooldownMs > 0 &&
            reevaluateAfterMs >= noSignalMs && awayMs >= awayFloorMs;

        if (!valid)
        {
            Log.Write("policy config values out of range — falling back to built-in defaults for all policy fields");
            return DefaultPolicyConfig;
        }

        return new Core.PolicyConfig(
            awayThresholdMs: awayMs,
            inputIdleRequiredMs: idleMs,
            graceMs: graceMs,
            noSignalReportAfterMs: noSignalMs,
            reevaluateAfterMs: reevaluateAfterMs,
            reevaluateCooldownMs: reevaluateCooldownMs,
            recoveryFailureThreshold: recoveryThreshold,
            recoveryCooldownMs: recoveryCooldownMs,
            upgradeCooldownMs: upgradeCooldownMs);
    }

    /// The pristine defaults `SanitizeSensingConfig` falls back to, one field at a time — a
    /// plain `new Config()`, not a hand-duplicated literal, so the fallback values can never
    /// drift from `Config`'s own field initializers.
    static readonly Config DefaultSensingConfig = new();

    /// Bug fix (startup crash on a corrupted/hand-edited presencelock.json): `Config.Load()`
    /// deserialized with no bounds check, and `WinFormsTimer.Interval` throws
    /// `ArgumentOutOfRangeException` for a `SampleIntervalMs` &lt; 1 — a bad on-disk value could
    /// kill the app before the tray icon even existed. Each sensing field is validated and
    /// defaulted independently — deliberately not an all-or-nothing unit like
    /// `BuildPolicyConfig`'s nine policy fields (rfc-core-brain.md: that unit exists because a
    /// zero-filled cooldown makes the next camera hiccup an instant restart loop; no such
    /// cross-field coupling exists between `SampleIntervalMs` and `DarkFrameMeanThreshold`).
    /// `CameraNameContains` and every policy field pass through untouched — this function is
    /// sensing-only, the mirror image of `BuildPolicyConfig` reading (never writing) sensing
    /// fields.
    internal static Config SanitizeSensingConfig(Config cfg)
    {
        int sampleIntervalMs = cfg.SampleIntervalMs;
        if (sampleIntervalMs < 100 || sampleIntervalMs > 10000)
        {
            Log.Write($"config: SampleIntervalMs {sampleIntervalMs} out of range [100, 10000] — " +
                      $"falling back to default {DefaultSensingConfig.SampleIntervalMs}");
            sampleIntervalMs = DefaultSensingConfig.SampleIntervalMs;
        }

        double darkFrameMeanThreshold = cfg.DarkFrameMeanThreshold;
        if (darkFrameMeanThreshold < 0 || darkFrameMeanThreshold > 255)
        {
            Log.Write($"config: DarkFrameMeanThreshold {darkFrameMeanThreshold} out of range [0, 255] — " +
                      $"falling back to default {DefaultSensingConfig.DarkFrameMeanThreshold}");
            darkFrameMeanThreshold = DefaultSensingConfig.DarkFrameMeanThreshold;
        }

        return new Config
        {
            AwayThresholdSeconds = cfg.AwayThresholdSeconds,
            SampleIntervalMs = sampleIntervalMs,
            GraceSeconds = cfg.GraceSeconds,
            InputIdleSeconds = cfg.InputIdleSeconds,
            CameraNameContains = cfg.CameraNameContains,
            DarkFrameMeanThreshold = darkFrameMeanThreshold,
            NoSignalReportAfterMs = cfg.NoSignalReportAfterMs,
            ReevaluateAfterMs = cfg.ReevaluateAfterMs,
            ReevaluateCooldownMs = cfg.ReevaluateCooldownMs,
            RecoveryFailureThreshold = cfg.RecoveryFailureThreshold,
            RecoveryCooldownMs = cfg.RecoveryCooldownMs,
            UpgradeCooldownMs = cfg.UpgradeCooldownMs,
        };
    }

    /// The real handle-invalid classifier (rfc-core-brain.md, "Notes: recovery boundary" /
    /// R2-7): HResult `0x80070006` OR a message-substring match, because the WinRT projection
    /// does not reliably preserve the HResult. Two-parameter form so `OnCaptureFailed` (which
    /// receives `MediaCaptureFailedEventArgs` — only `Code` + `Message`, no `Exception`) can
    /// use it too; the `Exception` overload serves the init path. Shell-side by design
    /// (sensing, not policy) but named and unit-tested per the RFC rather than left inline.
    internal static bool IsHandleInvalid(int hresult, string message) =>
        hresult == unchecked((int)0x80070006) ||
        (message is not null && message.Contains("handle is invalid", StringComparison.OrdinalIgnoreCase));

    internal static bool IsHandleInvalid(Exception ex) => IsHandleInvalid(ex.HResult, ex.Message);

    /// The complete, core-owned gate for "should we attempt (re)acquisition" (rfc-core-brain.md
    /// R2-4/R2-15): true exactly while `Policy.status` is `AcquiringCamera` or `Recovering` —
    /// first-init retries only, per the Status priority table, which makes this test
    /// structurally incapable of gating a post-success in-process re-init. Shared by the retry
    /// timer's `Tick` guard and both `KickAcquisitionIfNeeded` call sites, replacing the legacy
    /// shell-local `!paused && !sessionLocked` booleans with one source of truth. Pure and
    /// factored out here (rather than inlined three times as `.Tag ==` comparisons) specifically
    /// so it is independently unit-tested against every `Status` case.
    internal static bool IsAcquiringOrRecovering(Core.Status status) =>
        status.Tag == Core.Status.Tags.AcquiringCamera || status.Tag == Core.Status.Tags.Recovering;

    /// Status → tray/status-text mapping (rfc-core-brain.md, slice 8b deliverable: "Status →
    /// tray/log mapping table implemented as a single function"). `lastObservationDark` is the
    /// shell's own current-sample dark-vs-no-frame knowledge (Core's `Status.NoSignal` does not
    /// distinguish the two — see "Notes" — the shell already computed the distinction one line
    /// before constructing the `Observation`). Pure and tested independently of `WatcherContext`.
    internal static string StatusText(Core.Status status, bool lastObservationDark) => status.Tag switch
    {
        Core.Status.Tags.Watching => "Watching",
        Core.Status.Tags.NoSignal => lastObservationDark
            ? "Camera dark/blocked — not locking"
            : "No camera frames — not locking",
        Core.Status.Tags.SessionLocked => "Session locked — watching paused",
        Core.Status.Tags.Paused => "Paused — camera kept open",
        Core.Status.Tags.AcquiringCamera => "Starting…",
        Core.Status.Tags.Recovering => "Camera unavailable — retrying",
        _ => throw new UnreachableException(),
    };

    /// Shared event-construction function (rfc-core-brain.md slice 8a: "Factor each call
    /// site's event construction into a small named function... reused unchanged by 8a's
    /// `ShadowAdvance` and 8b's `Advance`"). The shell already computes `haveFrame`/`dark`/
    /// `present` one line before constructing the `Observation` today — this only names the
    /// mapping, it does not change what's computed.
    internal static Core.Observation ClassifyObservation(bool haveFrame, bool dark, bool present)
    {
        if (!haveFrame) return Core.Observation.NoFrame;
        if (dark) return Core.Observation.DarkFrame;
        return present ? Core.Observation.FaceSeen : Core.Observation.NoFace;
    }

    /// `SampleAsync`'s shared event-construction function — the `ClassifySample(...): Event`
    /// named in the RFC's slice 8a contract.
    internal static Core.Event ClassifySample(bool haveFrame, bool dark, bool present, long inputIdleMs) =>
        Core.Event.NewSample(ClassifyObservation(haveFrame, dark, present), inputIdleMs);

    /// Bug fix (frozen-frame classification): `TryAcquireLatestFrame` can re-serve a cached
    /// frame forever (a known WinRT quirk) — a frame whose `Core.FrameFreshness` verdict is
    /// stale must be classified exactly as if no frame had been acquired at all, not as a live
    /// (possibly `FaceSeen`) observation. `NoFrame` is already the correct fail-open path: it
    /// resets the away baseline and accrues the no-signal clock toward the existing
    /// `NoSignalReportAfter` → `Reevaluate` → process-restart recovery machinery, so this
    /// collapses to a single boolean AND rather than a new recovery path.
    internal static bool EffectiveHaveFrame(bool haveFrame, bool frameIsFresh) => haveFrame && frameIsFresh;

    /// Picks the largest detected face (by pixel area) and normalizes its bounding box to
    /// [0,1] by the frame's pixel dimensions, for `PresenceLock.Core`'s `PresenceFilter`
    /// (spatially-coherent presence stabilization — rfc-core-brain.handoff.md, "Burn-in
    /// incident 2026-07-28"). Takes `BitmapBounds` (a plain WinRT struct, not `DetectedFace`
    /// itself — `DetectedFace` has no public constructor and cannot be instantiated outside a
    /// real `FaceDetector` result, so this signature is the boundary that keeps the selection
    /// logic unit-testable) so the shell need only pass `faces.Select(f => f.FaceBox)`.
    /// Normalized so filter coherence is resolution/crop independent regardless of camera
    /// format or Studio Effects' auto-framing crop changing mid-session. Returns `null` for an
    /// empty list or a degenerate (zero-dimension) frame — both map to "no detection this
    /// frame" at the `PresenceFilter.step` boundary.
    internal static Core.FaceBox? LargestFaceBoxNormalized(IReadOnlyList<BitmapBounds> boxes, uint frameWidth, uint frameHeight)
    {
        if (boxes.Count == 0 || frameWidth == 0 || frameHeight == 0) return null;

        var largest = boxes[0];
        ulong largestArea = (ulong)largest.Width * largest.Height;
        for (int i = 1; i < boxes.Count; i++)
        {
            ulong area = (ulong)boxes[i].Width * boxes[i].Height;
            if (area > largestArea)
            {
                largest = boxes[i];
                largestArea = area;
            }
        }

        return new Core.FaceBox(
            x: largest.X / (double)frameWidth,
            y: largest.Y / (double)frameHeight,
            w: largest.Width / (double)frameWidth,
            h: largest.Height / (double)frameHeight);
    }

    /// Plain (id, display name, enclosure panel) projection of a candidate color camera
    /// (rfc-core-brain.md addendum 2026-08-01, slice 10: camera-arrival upgrade) — the shape
    /// `SelectPreferredCamera` ranks over, kept independent of any live WinRT device handle so
    /// the ranking itself is unit-testable. `Id` is the candidate's `MediaFrameSourceInfo.Id`
    /// (unique per color source, and the same key `InitCameraAsync` already used to look up
    /// `capture.FrameSources[...]`); `Panel` is normalized to `EnclosurePanel.Unknown` for a
    /// camera reporting no enclosure location at all (an external USB webcam), exactly as
    /// `InitCameraAsync`'s original inline `SelectionRank` treated a null `EnclosureLocation`.
    internal readonly record struct CameraCandidate(string Id, string DisplayName, EnclosurePanel Panel);

    /// The camera preference ranking (rfc-core-brain.md addendum 2026-08-01, slice 10): the
    /// exact ordering `InitCameraAsync` computed inline before this slice — a non-blank user
    /// filter wins outright (first candidate whose display name contains it, case-insensitive,
    /// preserving input order), else external-over-built-in-front (no enclosure location /
    /// `Unknown` ranks best, `Front` next, any other known panel last). Factored out as a pure,
    /// pane-agnostic function so startup selection (`InitCameraAsync`) and the arrival-triggered
    /// `DeviceWatcher` settle check share one implementation rather than re-deriving the
    /// ordering independently. Returns `null` only when `candidates` is empty — the caller's
    /// "no color camera found" is a shell-level concern, not this function's.
    internal static string? SelectPreferredCamera(IReadOnlyList<CameraCandidate> candidates, string nameFilter)
    {
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            foreach (var candidate in candidates)
                if (candidate.DisplayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    return candidate.Id;
        }

        return candidates.OrderBy(c => PanelRank(c.Panel)).First().Id;
    }

    // External USB webcams report no enclosure panel (normalized to Unknown above); prefer
    // them over the built-in camera, which sees only the lid when docked closed.
    static int PanelRank(EnclosurePanel panel) =>
        panel == EnclosurePanel.Unknown ? 0 : panel == EnclosurePanel.Front ? 1 : 2;

    static readonly Core.RestartStamps EmptyRestartStamps = new(wedgeAt: null, reevalAt: null, upgradeAt: null);

    static readonly string StampsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "restart-stamps.json");

    /// The file this JSON stamps file supersedes (rfc-core-brain.md, "Restart stamps" / R2-34):
    /// a plain-text single wall-clock timestamp, replaced by `StampsPath`'s per-`RestartReason`
    /// JSON. Deleted the first time `SaveRestartStamps` runs on an upgraded build — see there.
    static readonly string LegacyRestartStampPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "last-restart.txt");

    /// Reads the wall-clock stamps file (rfc-core-brain.md, "Restart stamps") if present, else
    /// returns empty stamps — any read/parse error also yields empty, since a parse failure
    /// must never block recovery. Feeds `Policy.start` at every startup; the counterpart writer
    /// is `SaveRestartStamps`, called only from `RestartProcess()` immediately before spawn.
    internal static Core.RestartStamps LoadRestartStamps()
    {
        try
        {
            if (File.Exists(StampsPath))
            {
                var dto = JsonSerializer.Deserialize<RestartStampsDto>(File.ReadAllText(StampsPath));
                if (dto is not null)
                    return new Core.RestartStamps(wedgeAt: dto.WedgeAt, reevalAt: dto.ReevalAt, upgradeAt: dto.UpgradeAt);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"restart stamps load failed: {ex.Message}");
        }
        return EmptyRestartStamps;
    }

    /// Writer counterpart to `LoadRestartStamps` (rfc-core-brain.md, "Restart stamps" /
    /// R1-29): persists both stamp fields verbatim (Policy.step itself only ever bumps the one
    /// matching the reason it fired, so a plain round-trip here can't poison the other reason's
    /// cooldown) plus the paused flag, in one JSON write. Called only from `RestartProcess()`,
    /// immediately before spawn — never on any other path (R2-11's pinned lifecycle: the flag
    /// is written only here and consumed-and-cleared by `ConsumePersistedPausedFlag`). A write
    /// failure must not block the restart itself, so it is caught and logged, not thrown.
    internal static void SaveRestartStamps(Core.RestartStamps stamps, bool paused)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StampsPath)!);
            var dto = new RestartStampsDto
            {
                WedgeAt = stamps.WedgeAt,
                ReevalAt = stamps.ReevalAt,
                UpgradeAt = stamps.UpgradeAt,
                Paused = paused,
            };
            File.WriteAllText(StampsPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"restart stamps save failed: {ex.Message}");
        }

        // Migration cleanup (rfc-core-brain.md, "Restart stamps" / R2-34): last-restart.txt is
        // superseded by the stamps file above; delete it here, the first time this method runs
        // on an upgraded build. File.Delete is already a silent no-op when the path is absent
        // (true for every call after the first), so no separate "have we done this yet" state
        // is needed. A cleanup failure must never block the restart this method exists to
        // support, so it is caught and logged, not thrown.
        try
        {
            File.Delete(LegacyRestartStampPath);
        }
        catch (Exception ex)
        {
            Log.Write($"legacy restart stamp cleanup failed: {ex.Message}");
        }
    }

    /// Paused-flag consume-and-clear (rfc-core-brain.md R2-11, pinned lifecycle): reads the
    /// persisted flag; if set, immediately rewrites the file with it cleared (stamps untouched)
    /// and returns true so the caller feeds `Event.Paused` through `Advance` right after
    /// `Policy.start`. Returns false — and touches nothing on disk — when absent/false/unparseable,
    /// so a normal launch or a tray Exit never inherits a stale pause, and a parse failure
    /// never blocks startup. Called exactly once, from `WatcherContext`'s constructor.
    internal static bool ConsumePersistedPausedFlag()
    {
        try
        {
            if (!File.Exists(StampsPath)) return false;
            var dto = JsonSerializer.Deserialize<RestartStampsDto>(File.ReadAllText(StampsPath));
            if (dto is null || !dto.Paused) return false;

            var cleared = new RestartStampsDto
            {
                WedgeAt = dto.WedgeAt,
                ReevalAt = dto.ReevalAt,
                UpgradeAt = dto.UpgradeAt,
                Paused = false,
            };
            File.WriteAllText(StampsPath, JsonSerializer.Serialize(cleared, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"paused-flag consume failed: {ex.Message}");
            return false;
        }
    }

    sealed class RestartStampsDto
    {
        public long? WedgeAt { get; init; }
        public long? ReevalAt { get; init; }
        // RFC addendum 2026-08-01, slice 10: absent on an old two-field stamps file, which
        // deserializes it as null — exactly how WedgeAt/ReevalAt already behaved before their
        // first restart of either reason had ever fired. Back-compat by construction.
        public long? UpgradeAt { get; init; }
        public bool Paused { get; init; }
    }
}
