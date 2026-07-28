using System.Text.Json;
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

    static readonly Core.RestartStamps EmptyRestartStamps = new(wedgeAt: null, reevalAt: null);

    static readonly string StampsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "restart-stamps.json");

    /// Reads the RFC's new wall-clock stamps file (rfc-core-brain.md, "Restart stamps") if
    /// present, else returns empty stamps — any read/parse error also yields empty, since a
    /// parse failure must never block recovery. Nothing writes this file yet: that's 8b/8c,
    /// once `Action.Restart` is actually executed and its cooldown persisted. Read-only here,
    /// so the shadow core's own `Policy.start` has a real (currently always-empty) input
    /// rather than a hand-rolled literal, and the same reader is what 8b's real `Advance`
    /// reuses unchanged.
    internal static Core.RestartStamps LoadRestartStamps()
    {
        try
        {
            if (File.Exists(StampsPath))
            {
                var dto = JsonSerializer.Deserialize<RestartStampsDto>(File.ReadAllText(StampsPath));
                if (dto is not null)
                    return new Core.RestartStamps(wedgeAt: ToNullable(dto.WedgeAt), reevalAt: ToNullable(dto.ReevalAt));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"restart stamps load failed: {ex.Message}");
        }
        return EmptyRestartStamps;
    }

    static Nullable<long> ToNullable(long? v) => v.HasValue ? new Nullable<long>(v.Value) : new Nullable<long>();

    sealed class RestartStampsDto
    {
        public long? WedgeAt { get; init; }
        public long? ReevalAt { get; init; }
    }
}
