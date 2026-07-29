using System.Diagnostics;
using System.Text.Json;
using Windows.Graphics.Imaging;
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
    /// Built-in defaults for all eight `PolicyConfig` fields, applied as a unit (never a
    /// partial mix — rfc-core-brain.md, "Config" validation rules) whenever the persisted
    /// values fail validation. Matches the pinned mapping table exactly.
    static readonly Core.PolicyConfig DefaultPolicyConfig = new(
        awayThresholdMs: 5000,
        inputIdleRequiredMs: 10000,
        graceMs: 10000,
        noSignalReportAfterMs: 10000,
        reevaluateAfterMs: 20000,
        reevaluateCooldownMs: 600000,
        recoveryFailureThreshold: 3,
        recoveryCooldownMs: 600000);

    /// Maps the flat, on-disk `Config` (unchanged schema) to `PolicyConfig`, per the pinned
    /// mapping table. Validates the eight policy fields as a single unit — any one out of
    /// range falls the whole set back to `DefaultPolicyConfig`, never a partially-defaulted
    /// mix (rfc-core-brain.md: "a zero-filled cooldown would make the next camera hiccup an
    /// instant restart loop"). Sensing fields (`CameraNameContains`, `DarkFrameMeanThreshold`,
    /// `SampleIntervalMs`) are untouched by this function and never affected by a policy-field
    /// failure. Shared by file load and (once the Settings dialog wires it) the commit path,
    /// per the RFC — same function, so an invalid committed value can never reach `step`
    /// unvalidated.
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

        bool valid =
            awayMs > 0 && idleMs > 0 && graceMs > 0 &&
            noSignalMs > 0 && reevaluateAfterMs > 0 && reevaluateCooldownMs > 0 &&
            recoveryCooldownMs > 0 && recoveryThreshold >= 1 &&
            reevaluateAfterMs >= noSignalMs;

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
            recoveryCooldownMs: recoveryCooldownMs);
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

    static readonly Core.RestartStamps EmptyRestartStamps = new(wedgeAt: null, reevalAt: null);

    static readonly string StampsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "restart-stamps.json");

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
                    return new Core.RestartStamps(wedgeAt: dto.WedgeAt, reevalAt: dto.ReevalAt);
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
            var dto = new RestartStampsDto { WedgeAt = stamps.WedgeAt, ReevalAt = stamps.ReevalAt, Paused = paused };
            File.WriteAllText(StampsPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"restart stamps save failed: {ex.Message}");
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

            var cleared = new RestartStampsDto { WedgeAt = dto.WedgeAt, ReevalAt = dto.ReevalAt, Paused = false };
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
        public bool Paused { get; init; }
    }
}
