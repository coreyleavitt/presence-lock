using Core = PresenceLock.Core;
using Windows.Graphics.Imaging;
using EnclosurePanel = Windows.Devices.Enumeration.Panel;
using Xunit;

namespace PresenceLock.Tests;

/// Dedicated shell-side xunit tests for PolicyBridge (0001-core-brain.md, slice 8a /
/// "Config: file schema, mapping, validation" / R2-7). These are pure functions extracted
/// from Program.cs specifically so they stop being "expected behavior existed only as code"
/// (the RFC's Motivation) — each gets independent coverage here rather than only exercised
/// indirectly through the shell.
public class IsHandleInvalidTests
{
    [Fact]
    public void HResult_match_is_handle_invalid()
    {
        Assert.True(PolicyBridge.IsHandleInvalid(unchecked((int)0x80070006), "some other message"));
    }

    [Fact]
    public void Message_substring_match_is_handle_invalid_case_insensitive()
    {
        Assert.True(PolicyBridge.IsHandleInvalid(0, "The HANDLE Is Invalid, sorry"));
    }

    [Fact]
    public void Neither_hresult_nor_message_match_is_not_handle_invalid()
    {
        Assert.False(PolicyBridge.IsHandleInvalid(unchecked((int)0x80004005), "access denied"));
    }

    [Fact]
    public void Args_form_serves_two_parameter_MediaCaptureFailedEventArgs_shape()
    {
        // R2-7: OnCaptureFailed only has Code (int) + Message (string), no Exception — the
        // two-parameter form must work standalone, not merely via the Exception overload.
        Assert.True(PolicyBridge.IsHandleInvalid(hresult: 0, message: "handle is invalid"));
        Assert.False(PolicyBridge.IsHandleInvalid(hresult: 0, message: "timeout"));
    }

    [Fact]
    public void Exception_overload_delegates_to_the_two_parameter_form()
    {
        var ex = new InvalidOperationException("wrapped: the handle is invalid here");
        Assert.True(PolicyBridge.IsHandleInvalid(ex));
    }
}

/// RFC 0002-environment-levels, "Construction discipline": `StepInputs` has three adjacent
/// same-typed bools, so a positional C# constructor call with two of them transposed still
/// compiles silently. The RFC's mitigation is a one-helper, always-named-arguments rule at
/// every shell call site (`PolicyBridge`/`Program.cs` construct `StepInputs` in exactly one
/// place) — this test is the pinned safety net for that rule: it constructs `StepInputs` via
/// named arguments with each bool flipped independently off a known baseline and asserts
/// EVERY field of the result (not just the one flipped), so a future reordering of the F#
/// record's declared fields — which would only matter to a positional call — can never
/// silently transpose which field a given named argument lands on without failing here first.
public class StepInputsConstructionTests
{
    [Fact]
    public void Flipping_SessionLocked_leaves_every_other_field_at_baseline()
    {
        var inputs = new Core.StepInputs(
            sessionLocked: true, paused: false, lockInhibited: false, inputIdleMs: 0L);

        Assert.True(inputs.SessionLocked);
        Assert.False(inputs.Paused);
        Assert.False(inputs.LockInhibited);
        Assert.Equal(0L, inputs.InputIdleMs);
    }

    [Fact]
    public void Flipping_Paused_leaves_every_other_field_at_baseline()
    {
        var inputs = new Core.StepInputs(
            sessionLocked: false, paused: true, lockInhibited: false, inputIdleMs: 0L);

        Assert.False(inputs.SessionLocked);
        Assert.True(inputs.Paused);
        Assert.False(inputs.LockInhibited);
        Assert.Equal(0L, inputs.InputIdleMs);
    }

    [Fact]
    public void Flipping_LockInhibited_leaves_every_other_field_at_baseline()
    {
        var inputs = new Core.StepInputs(
            sessionLocked: false, paused: false, lockInhibited: true, inputIdleMs: 0L);

        Assert.False(inputs.SessionLocked);
        Assert.False(inputs.Paused);
        Assert.True(inputs.LockInhibited);
        Assert.Equal(0L, inputs.InputIdleMs);
    }

    [Fact]
    public void Setting_InputIdleMs_leaves_every_other_field_at_baseline()
    {
        var inputs = new Core.StepInputs(
            sessionLocked: false, paused: false, lockInhibited: false, inputIdleMs: 4242L);

        Assert.False(inputs.SessionLocked);
        Assert.False(inputs.Paused);
        Assert.False(inputs.LockInhibited);
        Assert.Equal(4242L, inputs.InputIdleMs);
    }
}

public class ClassifyObservationTests
{
    [Fact]
    public void No_frame_classifies_as_NoFrame_regardless_of_other_flags()
    {
        Assert.Equal(Core.Observation.NoFrame, PolicyBridge.ClassifyObservation(haveFrame: false, dark: true, present: true));
        Assert.Equal(Core.Observation.NoFrame, PolicyBridge.ClassifyObservation(haveFrame: false, dark: false, present: false));
    }

    [Fact]
    public void Dark_frame_classifies_as_DarkFrame_when_frame_present_but_dark()
    {
        Assert.Equal(Core.Observation.DarkFrame, PolicyBridge.ClassifyObservation(haveFrame: true, dark: true, present: false));
    }

    [Fact]
    public void Bright_frame_with_face_classifies_as_FaceSeen()
    {
        Assert.Equal(Core.Observation.FaceSeen, PolicyBridge.ClassifyObservation(haveFrame: true, dark: false, present: true));
    }

    [Fact]
    public void Bright_frame_without_face_classifies_as_NoFace()
    {
        Assert.Equal(Core.Observation.NoFace, PolicyBridge.ClassifyObservation(haveFrame: true, dark: false, present: false));
    }
}

public class BuildPolicyConfigTests
{
    [Fact]
    public void Valid_config_maps_seconds_to_milliseconds_per_the_pinned_table()
    {
        var cfg = new Config
        {
            AwayThresholdSeconds = 5.0,
            InputIdleSeconds = 10.0,
            GraceSeconds = 10.0,
            NoSignalReportAfterMs = 10000,
            ReevaluateAfterMs = 20000,
            ReevaluateCooldownMs = 600000,
            RecoveryFailureThreshold = 3,
            RecoveryCooldownMs = 600000,
            UpgradeCooldownMs = 600000,
        };

        var policy = PolicyBridge.BuildPolicyConfig(cfg);

        Assert.Equal(5000, policy.AwayThresholdMs);
        Assert.Equal(10000, policy.InputIdleRequiredMs);
        Assert.Equal(10000, policy.GraceMs);
        Assert.Equal(10000, policy.NoSignalReportAfterMs);
        Assert.Equal(20000, policy.ReevaluateAfterMs);
        Assert.Equal(600000, policy.ReevaluateCooldownMs);
        Assert.Equal(3, policy.RecoveryFailureThreshold);
        Assert.Equal(600000, policy.RecoveryCooldownMs);
        Assert.Equal(600000, policy.UpgradeCooldownMs);
    }

    [Fact]
    public void Default_config_matches_the_pinned_defaults()
    {
        var policy = PolicyBridge.BuildPolicyConfig(new Config());

        Assert.Equal(5000, policy.AwayThresholdMs);
        Assert.Equal(10000, policy.InputIdleRequiredMs);
        Assert.Equal(10000, policy.GraceMs);
        Assert.Equal(10000, policy.NoSignalReportAfterMs);
        Assert.Equal(20000, policy.ReevaluateAfterMs);
        Assert.Equal(600000, policy.ReevaluateCooldownMs);
        Assert.Equal(3, policy.RecoveryFailureThreshold);
        Assert.Equal(600000, policy.RecoveryCooldownMs);
        Assert.Equal(600000, policy.UpgradeCooldownMs);
    }

    [Theory]
    [InlineData(0.0, 10.0, 10.0, 10000L, 20000L, 600000L, 3, 600000L, 600000L)]   // AwayThresholdSeconds <= 0
    [InlineData(5.0, 10.0, 10.0, 0L, 20000L, 600000L, 3, 600000L, 600000L)]       // NoSignalReportAfterMs <= 0
    [InlineData(5.0, 10.0, 10.0, 10000L, 20000L, 600000L, 0, 600000L, 600000L)]   // RecoveryFailureThreshold < 1
    [InlineData(5.0, 10.0, 10.0, 20000L, 10000L, 600000L, 3, 600000L, 600000L)]   // ReevaluateAfterMs < NoSignalReportAfterMs
    [InlineData(5.0, 10.0, 10.0, 10000L, 20000L, 600000L, 3, 600000L, 0L)]        // UpgradeCooldownMs <= 0
    public void Any_single_invalid_field_falls_back_to_the_full_default_set_never_a_partial_mix(
        double awaySeconds, double idleSeconds, double graceSeconds,
        long noSignalMs, long reevaluateAfterMs, long reevaluateCooldownMs,
        int recoveryThreshold, long recoveryCooldownMs, long upgradeCooldownMs)
    {
        var cfg = new Config
        {
            AwayThresholdSeconds = awaySeconds,
            InputIdleSeconds = idleSeconds,
            GraceSeconds = graceSeconds,
            NoSignalReportAfterMs = noSignalMs,
            ReevaluateAfterMs = reevaluateAfterMs,
            ReevaluateCooldownMs = reevaluateCooldownMs,
            RecoveryFailureThreshold = recoveryThreshold,
            RecoveryCooldownMs = recoveryCooldownMs,
            UpgradeCooldownMs = upgradeCooldownMs,
        };

        var policy = PolicyBridge.BuildPolicyConfig(cfg);
        var expectedDefaults = PolicyBridge.BuildPolicyConfig(new Config());

        Assert.Equal(expectedDefaults.AwayThresholdMs, policy.AwayThresholdMs);
        Assert.Equal(expectedDefaults.InputIdleRequiredMs, policy.InputIdleRequiredMs);
        Assert.Equal(expectedDefaults.GraceMs, policy.GraceMs);
        Assert.Equal(expectedDefaults.NoSignalReportAfterMs, policy.NoSignalReportAfterMs);
        Assert.Equal(expectedDefaults.ReevaluateAfterMs, policy.ReevaluateAfterMs);
        Assert.Equal(expectedDefaults.ReevaluateCooldownMs, policy.ReevaluateCooldownMs);
        Assert.Equal(expectedDefaults.RecoveryFailureThreshold, policy.RecoveryFailureThreshold);
        Assert.Equal(expectedDefaults.RecoveryCooldownMs, policy.RecoveryCooldownMs);
        Assert.Equal(expectedDefaults.UpgradeCooldownMs, policy.UpgradeCooldownMs);
    }

    /// Bug fix (false-lock window): worst-case re-stabilization after one dropped detection is
    /// ~one sample gap plus the filter's MinCoherentMs dwell; if AwayThresholdMs can be lower
    /// than that, a single dropped detection under fast sampling can make the away clock fire
    /// before presence has had a chance to re-stabilize -- a false lock while the user never
    /// left. The floor is exactly at the boundary here: AwayThresholdSeconds=2.0s * 1000 = 2000ms
    /// == FilterConfig.Default.MinCoherentMs (1000ms) + 2 * the default SampleIntervalMs (500ms).
    [Fact]
    public void Away_at_the_MinCoherentMs_plus_two_sample_intervals_floor_passes()
    {
        var cfg = new Config { AwayThresholdSeconds = 2.0, SampleIntervalMs = 500 };

        var policy = PolicyBridge.BuildPolicyConfig(cfg);

        Assert.Equal(2000, policy.AwayThresholdMs);
    }

    [Fact]
    public void Away_below_the_MinCoherentMs_plus_two_sample_intervals_floor_falls_back_to_defaults()
    {
        var cfg = new Config { AwayThresholdSeconds = 1.5, SampleIntervalMs = 500 }; // 1500ms < 2000ms floor

        var policy = PolicyBridge.BuildPolicyConfig(cfg);
        var defaults = PolicyBridge.BuildPolicyConfig(new Config());

        Assert.Equal(defaults.AwayThresholdMs, policy.AwayThresholdMs);
    }

    /// The floor scales with SampleIntervalMs, not a fixed constant: at a faster sample rate a
    /// smaller away threshold is still safe.
    [Fact]
    public void Away_floor_scales_with_a_custom_SampleIntervalMs()
    {
        var cfg = new Config { AwayThresholdSeconds = 1.5, SampleIntervalMs = 250 }; // 1500ms == 1000 + 2*250

        var policy = PolicyBridge.BuildPolicyConfig(cfg);

        Assert.Equal(1500, policy.AwayThresholdMs);
    }

    [Fact]
    public void Sensing_only_fields_are_not_part_of_PolicyConfig_and_do_not_affect_the_fallback()
    {
        // CameraNameContains / DarkFrameMeanThreshold / SampleIntervalMs stay shell-only
        // (0001-core-brain.md) — a fat-fingered policy field must not touch them, and this
        // mapping function must never read them.
        var cfg = new Config { CameraNameContains = "Logitech", DarkFrameMeanThreshold = 42.0, SampleIntervalMs = 250 };
        var policy = PolicyBridge.BuildPolicyConfig(cfg);
        var defaults = PolicyBridge.BuildPolicyConfig(new Config());

        Assert.Equal(defaults.AwayThresholdMs, policy.AwayThresholdMs);
        Assert.Equal(defaults.RecoveryCooldownMs, policy.RecoveryCooldownMs);
    }
}

/// Frozen-frame classification (bug fix): `TryAcquireLatestFrame` can re-serve a cached frame
/// forever (a known WinRT quirk) -- a stale frame must be treated exactly as if no frame had
/// been acquired at all, so the existing fail-open NoFrame path (reset the away baseline,
/// accrue the no-signal clock, eventually restart) recovers the pipe with zero new mechanism.
public class EffectiveHaveFrameTests
{
    [Fact]
    public void A_fresh_acquired_frame_counts_as_a_frame()
    {
        Assert.True(PolicyBridge.EffectiveHaveFrame(haveFrame: true, frameIsFresh: true));
    }

    [Fact]
    public void No_frame_was_acquired_regardless_of_freshness()
    {
        Assert.False(PolicyBridge.EffectiveHaveFrame(haveFrame: false, frameIsFresh: true));
        Assert.False(PolicyBridge.EffectiveHaveFrame(haveFrame: false, frameIsFresh: false));
    }

    [Fact]
    public void A_stale_frame_is_treated_as_no_frame_even_though_one_was_acquired()
    {
        Assert.False(PolicyBridge.EffectiveHaveFrame(haveFrame: true, frameIsFresh: false));
    }
}

/// Bug fix (startup crash on a corrupted/hand-edited presencelock.json): `Config.Load()`
/// deserializes with no bounds check, and `WinFormsTimer.Interval` throws
/// `ArgumentOutOfRangeException` for a `SampleIntervalMs` &lt; 1 — a bad on-disk value killed
/// the app at startup before the tray icon even existed. Sensing fields are deliberately
/// independent (mirrors BuildPolicyConfig's comment that sensing fields stay untouched by the
/// policy-field all-or-nothing unit) — each is validated and defaulted on its own, never as a
/// unit with the others, and never touching any policy field.
public class SanitizeSensingConfigTests
{
    [Fact]
    public void In_range_sensing_fields_pass_through_untouched()
    {
        var cfg = new Config { SampleIntervalMs = 750, DarkFrameMeanThreshold = 12.5 };

        var sanitized = PolicyBridge.SanitizeSensingConfig(cfg);

        Assert.Equal(750, sanitized.SampleIntervalMs);
        Assert.Equal(12.5, sanitized.DarkFrameMeanThreshold);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(10001)]
    [InlineData(0)]
    public void Out_of_range_SampleIntervalMs_falls_back_to_the_default_independently_of_DarkFrameMeanThreshold(int badValue)
    {
        var cfg = new Config { SampleIntervalMs = badValue, DarkFrameMeanThreshold = 12.5 };

        var sanitized = PolicyBridge.SanitizeSensingConfig(cfg);

        Assert.Equal(new Config().SampleIntervalMs, sanitized.SampleIntervalMs);
        Assert.Equal(12.5, sanitized.DarkFrameMeanThreshold);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(255.1)]
    public void Out_of_range_DarkFrameMeanThreshold_falls_back_to_the_default_independently_of_SampleIntervalMs(double badValue)
    {
        var cfg = new Config { DarkFrameMeanThreshold = badValue, SampleIntervalMs = 750 };

        var sanitized = PolicyBridge.SanitizeSensingConfig(cfg);

        Assert.Equal(new Config().DarkFrameMeanThreshold, sanitized.DarkFrameMeanThreshold);
        Assert.Equal(750, sanitized.SampleIntervalMs);
    }

    [Fact]
    public void Sanitizing_a_sensing_field_never_alters_any_policy_field()
    {
        var cfg = new Config
        {
            SampleIntervalMs = 0,
            DarkFrameMeanThreshold = 999,
            AwayThresholdSeconds = 42.0,
            RecoveryFailureThreshold = 7,
        };

        var sanitized = PolicyBridge.SanitizeSensingConfig(cfg);

        Assert.Equal(42.0, sanitized.AwayThresholdSeconds);
        Assert.Equal(7, sanitized.RecoveryFailureThreshold);
    }

    [Fact]
    public void Boundary_values_at_the_edges_of_the_valid_range_pass_through_untouched()
    {
        var low = PolicyBridge.SanitizeSensingConfig(new Config { SampleIntervalMs = 100, DarkFrameMeanThreshold = 0.0 });
        Assert.Equal(100, low.SampleIntervalMs);
        Assert.Equal(0.0, low.DarkFrameMeanThreshold);

        var high = PolicyBridge.SanitizeSensingConfig(new Config { SampleIntervalMs = 10000, DarkFrameMeanThreshold = 255.0 });
        Assert.Equal(10000, high.SampleIntervalMs);
        Assert.Equal(255.0, high.DarkFrameMeanThreshold);
    }
}

/// Shell-side helper feeding PresenceLock.Core's PresenceFilter (0001-core-brain.handoff.md,
/// "Burn-in incident 2026-07-28"). Takes plain `BitmapBounds` rather than `DetectedFace` itself
/// so it stays unit-testable: `DetectedFace` has no public constructor and can only be produced
/// by a real `FaceDetector` result.
public class LargestFaceBoxNormalizedTests
{
    [Fact]
    public void No_boxes_yields_null()
    {
        Assert.Null(PolicyBridge.LargestFaceBoxNormalized(Array.Empty<BitmapBounds>(), frameWidth: 640, frameHeight: 480));
    }

    [Theory]
    [InlineData(0u, 480u)]
    [InlineData(640u, 0u)]
    public void Degenerate_frame_dimensions_yield_null(uint width, uint height)
    {
        var boxes = new[] { new BitmapBounds { X = 0, Y = 0, Width = 100, Height = 100 } };
        Assert.Null(PolicyBridge.LargestFaceBoxNormalized(boxes, width, height));
    }

    [Fact]
    public void Single_box_is_normalized_by_frame_dimensions()
    {
        var boxes = new[] { new BitmapBounds { X = 64, Y = 48, Width = 128, Height = 96 } };

        var box = PolicyBridge.LargestFaceBoxNormalized(boxes, frameWidth: 640, frameHeight: 480);

        Assert.NotNull(box);
        Assert.Equal(0.1, box!.Value.X, 10);
        Assert.Equal(0.1, box.Value.Y, 10);
        Assert.Equal(0.2, box.Value.W, 10);
        Assert.Equal(0.2, box.Value.H, 10);
    }

    [Fact]
    public void Multiple_boxes_selects_the_largest_by_pixel_area()
    {
        var small = new BitmapBounds { X = 0, Y = 0, Width = 50, Height = 50 };
        var large = new BitmapBounds { X = 200, Y = 100, Width = 200, Height = 150 };
        var boxes = new[] { small, large };

        var box = PolicyBridge.LargestFaceBoxNormalized(boxes, frameWidth: 640, frameHeight: 480);

        Assert.NotNull(box);
        Assert.Equal(large.X / 640.0, box!.Value.X, 10);
        Assert.Equal(large.Y / 480.0, box.Value.Y, 10);
        Assert.Equal(large.Width / 640.0, box.Value.W, 10);
        Assert.Equal(large.Height / 480.0, box.Value.H, 10);
    }
}

/// Shell-side status→text mapping (0001-core-brain.md, slice 8b deliverable). Pure and tested
/// independently of WatcherContext per every Status case.
public class StatusTextTests
{
    [Fact]
    public void NoSignal_dark_uses_dark_blocked_text()
    {
        Assert.Equal("Camera dark/blocked — not locking", PolicyBridge.StatusText(Core.Status.NoSignal, lastObservationDark: true));
    }

    [Fact]
    public void NoSignal_no_frame_uses_no_frames_text()
    {
        Assert.Equal("No camera frames — not locking", PolicyBridge.StatusText(Core.Status.NoSignal, lastObservationDark: false));
    }

    [Fact]
    public void Watching_maps_to_Watching()
    {
        Assert.Equal("Watching", PolicyBridge.StatusText(Core.Status.Watching, lastObservationDark: false));
    }

    [Fact]
    public void SessionLocked_maps_to_the_expected_text()
    {
        Assert.Equal("Session locked — watching paused", PolicyBridge.StatusText(Core.Status.SessionLocked, lastObservationDark: false));
    }

    [Fact]
    public void Paused_maps_to_the_expected_text()
    {
        Assert.Equal("Paused — camera kept open", PolicyBridge.StatusText(Core.Status.Paused, lastObservationDark: false));
    }

    [Fact]
    public void AcquiringCamera_maps_to_Starting()
    {
        Assert.Equal("Starting…", PolicyBridge.StatusText(Core.Status.AcquiringCamera, lastObservationDark: false));
    }

    [Fact]
    public void Recovering_maps_to_retrying_text()
    {
        Assert.Equal("Camera unavailable — retrying", PolicyBridge.StatusText(Core.Status.Recovering, lastObservationDark: false));
    }
}

/// The complete, core-owned retry/kick gate (0001-core-brain.md R2-4/R2-15): true exactly for
/// AcquiringCamera/Recovering, the pre-first-success-only statuses.
public class IsAcquiringOrRecoveringTests
{
    [Fact]
    public void AcquiringCamera_and_Recovering_are_true()
    {
        Assert.True(PolicyBridge.IsAcquiringOrRecovering(Core.Status.AcquiringCamera));
        Assert.True(PolicyBridge.IsAcquiringOrRecovering(Core.Status.Recovering));
    }

    [Fact]
    public void Every_other_status_is_false()
    {
        Assert.False(PolicyBridge.IsAcquiringOrRecovering(Core.Status.Watching));
        Assert.False(PolicyBridge.IsAcquiringOrRecovering(Core.Status.NoSignal));
        Assert.False(PolicyBridge.IsAcquiringOrRecovering(Core.Status.SessionLocked));
        Assert.False(PolicyBridge.IsAcquiringOrRecovering(Core.Status.Paused));
    }
}

/// Suppression predicate (RFC 0002-environment-levels, "Status"): the two priority rows
/// sampling stops for, used by Advance's suppression-transition rule to detect the before/
/// after status pair's edge.
public class IsSuppressedStatusTests
{
    [Fact]
    public void Paused_and_SessionLocked_are_suppressed()
    {
        Assert.True(PolicyBridge.IsSuppressedStatus(Core.Status.Paused));
        Assert.True(PolicyBridge.IsSuppressedStatus(Core.Status.SessionLocked));
    }

    [Fact]
    public void Every_other_status_is_not_suppressed()
    {
        Assert.False(PolicyBridge.IsSuppressedStatus(Core.Status.Watching));
        Assert.False(PolicyBridge.IsSuppressedStatus(Core.Status.NoSignal));
        Assert.False(PolicyBridge.IsSuppressedStatus(Core.Status.AcquiringCamera));
        Assert.False(PolicyBridge.IsSuppressedStatus(Core.Status.Recovering));
    }
}

/// Sampling watchdog gate (incident 2026-08-12): true exactly for the statuses where sample
/// events should be flowing (Watching/NoSignal). False for Paused/SessionLocked (sampling is
/// deliberately stopped) and AcquiringCamera/Recovering (pre-first-success acquisition is the
/// retry timer's job, not the sample timer's).
public class ExpectsSamplingTests
{
    [Fact]
    public void Watching_and_NoSignal_expect_sampling()
    {
        Assert.True(PolicyBridge.ExpectsSampling(Core.Status.Watching));
        Assert.True(PolicyBridge.ExpectsSampling(Core.Status.NoSignal));
    }

    [Fact]
    public void Paused_and_SessionLocked_do_not_expect_sampling()
    {
        Assert.False(PolicyBridge.ExpectsSampling(Core.Status.Paused));
        Assert.False(PolicyBridge.ExpectsSampling(Core.Status.SessionLocked));
    }

    [Fact]
    public void AcquiringCamera_and_Recovering_do_not_expect_sampling()
    {
        Assert.False(PolicyBridge.ExpectsSampling(Core.Status.AcquiringCamera));
        Assert.False(PolicyBridge.ExpectsSampling(Core.Status.Recovering));
    }
}

/// The sampling watchdog's pure two-strike step function (incident 2026-08-12): belt-and-
/// suspenders for the whole starvation class (a resume path that fails to restart sampleTimer,
/// or a SampleAsync pass hung forever leaving the `sampling` reentrancy guard stuck true). A
/// cheap self-heal (restart the sample timer) on the first starved check after entering a
/// sampling status, escalating to a `CaptureFailed`-shaped restart request only if starvation
/// persists past a second consecutive check — so a transient stall self-heals silently and only
/// a truly wedged pipeline reaches the core's existing cooldown-gated restart machinery.
public class SamplingWatchdogStepTests
{
    const long StarvationMs = 15000;

    [Fact]
    public void Not_expecting_sampling_resets_strikes_and_refreshes_the_stamp_to_now()
    {
        // Paused/locked/acquiring time never counts as starvation, and the first check after
        // re-entering a sampling status must measure from ~now, not from a stamp stale from
        // before the non-sampling interval (this is what absorbs a sleep/wake gap while paused).
        var verdict = PolicyBridge.SamplingWatchdogStep(
            expectsSampling: false, nowMs: 100_000, lastSamplePassMs: 1_000, strikes: 2, starvationMs: StarvationMs);

        Assert.Equal(0, verdict.Strikes);
        Assert.Equal(100_000, verdict.StampMs);
        Assert.False(verdict.StartSampleTimer);
        Assert.False(verdict.TreatAsCaptureFailed);
    }

    [Fact]
    public void Expecting_sampling_and_not_yet_starved_takes_no_action_and_leaves_the_stamp_unchanged()
    {
        var verdict = PolicyBridge.SamplingWatchdogStep(
            expectsSampling: true, nowMs: 10_000, lastSamplePassMs: 1_000, strikes: 0, starvationMs: StarvationMs);

        Assert.Equal(0, verdict.Strikes);
        Assert.Equal(1_000, verdict.StampMs);
        Assert.False(verdict.StartSampleTimer);
        Assert.False(verdict.TreatAsCaptureFailed);
    }

    [Fact]
    public void First_starved_check_self_heals_by_starting_the_sample_timer()
    {
        var verdict = PolicyBridge.SamplingWatchdogStep(
            expectsSampling: true, nowMs: 20_000, lastSamplePassMs: 1_000, strikes: 0, starvationMs: StarvationMs);

        Assert.Equal(1, verdict.Strikes);
        Assert.Equal(20_000, verdict.StampMs);
        Assert.True(verdict.StartSampleTimer);
        Assert.False(verdict.TreatAsCaptureFailed);
    }

    [Fact]
    public void Second_consecutive_starved_check_escalates_to_capture_failed_and_resets_strikes()
    {
        var verdict = PolicyBridge.SamplingWatchdogStep(
            expectsSampling: true, nowMs: 40_000, lastSamplePassMs: 20_000, strikes: 1, starvationMs: StarvationMs);

        Assert.Equal(0, verdict.Strikes);
        Assert.Equal(40_000, verdict.StampMs);
        Assert.False(verdict.StartSampleTimer);
        Assert.True(verdict.TreatAsCaptureFailed);
    }

    [Fact]
    public void A_healthy_pass_observed_between_strikes_resets_the_strike_count()
    {
        // strikes=1 carried in, but this check is not starved -- a completed sample pass landed
        // between the first strike and this check, so escalation must not fire next time either.
        var verdict = PolicyBridge.SamplingWatchdogStep(
            expectsSampling: true, nowMs: 21_000, lastSamplePassMs: 20_000, strikes: 1, starvationMs: StarvationMs);

        Assert.Equal(0, verdict.Strikes);
        Assert.False(verdict.TreatAsCaptureFailed);
    }

    [Fact]
    public void Repeated_starvation_alternates_self_heal_then_escalate_across_consecutive_checks()
    {
        var first = PolicyBridge.SamplingWatchdogStep(
            expectsSampling: true, nowMs: 20_000, lastSamplePassMs: 1_000, strikes: 0, starvationMs: StarvationMs);
        Assert.True(first.StartSampleTimer);
        Assert.Equal(1, first.Strikes);

        var second = PolicyBridge.SamplingWatchdogStep(
            expectsSampling: true, nowMs: 40_000, lastSamplePassMs: first.StampMs, strikes: first.Strikes, starvationMs: StarvationMs);
        Assert.True(second.TreatAsCaptureFailed);
        Assert.Equal(0, second.Strikes);
    }
}

/// The camera preference ranking (0001-core-brain.md addendum 2026-08-01, slice 10:
/// camera-arrival upgrade), factored out of InitCameraAsync's original inline selection so
/// startup selection and the arrival-triggered device watcher share one implementation. These
/// tests are the proof that the extraction is behavior-preserving: every case here is exactly
/// what InitCameraAsync computed inline before this slice.
public class SelectPreferredCameraTests
{
    [Fact]
    public void No_candidates_yields_null()
    {
        Assert.Null(PolicyBridge.SelectPreferredCamera(Array.Empty<PolicyBridge.CameraCandidate>(), nameFilter: ""));
    }

    [Fact]
    public void A_non_blank_user_filter_match_wins_outright_over_panel_ranking()
    {
        // The filter match is a built-in-front camera and would lose to the external camera on
        // panel rank alone -- an explicit user filter must still win.
        var candidates = new[]
        {
            new PolicyBridge.CameraCandidate("ext-1", "Logitech BRIO", EnclosurePanel.Unknown),
            new PolicyBridge.CameraCandidate("front-1", "Surface Camera Front", EnclosurePanel.Front),
        };

        var chosen = PolicyBridge.SelectPreferredCamera(candidates, nameFilter: "Surface");

        Assert.Equal("front-1", chosen);
    }

    [Fact]
    public void Filter_match_is_case_insensitive()
    {
        var candidates = new[] { new PolicyBridge.CameraCandidate("id-1", "LifeCam HD-3000", EnclosurePanel.Unknown) };

        Assert.Equal("id-1", PolicyBridge.SelectPreferredCamera(candidates, nameFilter: "lifecam"));
    }

    [Fact]
    public void Filter_matches_the_first_candidate_in_input_order()
    {
        var candidates = new[]
        {
            new PolicyBridge.CameraCandidate("first", "USB Camera", EnclosurePanel.Unknown),
            new PolicyBridge.CameraCandidate("second", "USB Camera", EnclosurePanel.Unknown),
        };

        Assert.Equal("first", PolicyBridge.SelectPreferredCamera(candidates, nameFilter: "USB"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_or_null_filter_falls_straight_through_to_panel_ranking(string? nameFilter)
    {
        var candidates = new[]
        {
            new PolicyBridge.CameraCandidate("front-1", "Surface Camera Front", EnclosurePanel.Front),
            new PolicyBridge.CameraCandidate("ext-1", "External USB Camera", EnclosurePanel.Unknown),
        };

        Assert.Equal("ext-1", PolicyBridge.SelectPreferredCamera(candidates, nameFilter!));
    }

    [Fact]
    public void A_filter_that_matches_nothing_falls_through_to_panel_ranking()
    {
        var candidates = new[]
        {
            new PolicyBridge.CameraCandidate("front-1", "Surface Camera Front", EnclosurePanel.Front),
            new PolicyBridge.CameraCandidate("ext-1", "External USB Camera", EnclosurePanel.Unknown),
        };

        Assert.Equal("ext-1", PolicyBridge.SelectPreferredCamera(candidates, nameFilter: "Nonexistent"));
    }

    [Fact]
    public void No_enclosure_location_external_over_built_in_front()
    {
        // The exact ordering 0001-core-brain.md's original SelectionRank implemented: no
        // enclosure location at all (external USB webcams) ranks above Front.
        var candidates = new[]
        {
            new PolicyBridge.CameraCandidate("front-1", "Built-in Front", EnclosurePanel.Front),
            new PolicyBridge.CameraCandidate("ext-1", "External Webcam", EnclosurePanel.Unknown),
        };

        Assert.Equal("ext-1", PolicyBridge.SelectPreferredCamera(candidates, nameFilter: ""));
    }

    [Fact]
    public void Front_ranks_above_any_other_known_panel_location()
    {
        var candidates = new[]
        {
            new PolicyBridge.CameraCandidate("back-1", "Built-in Back", EnclosurePanel.Back),
            new PolicyBridge.CameraCandidate("front-1", "Built-in Front", EnclosurePanel.Front),
        };

        Assert.Equal("front-1", PolicyBridge.SelectPreferredCamera(candidates, nameFilter: ""));
    }

    [Fact]
    public void Ties_in_panel_rank_prefer_the_first_candidate_in_input_order()
    {
        var candidates = new[]
        {
            new PolicyBridge.CameraCandidate("ext-1", "External Webcam A", EnclosurePanel.Unknown),
            new PolicyBridge.CameraCandidate("ext-2", "External Webcam B", EnclosurePanel.Unknown),
        };

        Assert.Equal("ext-1", PolicyBridge.SelectPreferredCamera(candidates, nameFilter: ""));
    }
}

public class LoadRestartStampsTests
{
    [Fact]
    public void Missing_file_yields_empty_stamps()
    {
        var stamps = PolicyBridge.LoadRestartStamps();
        // This call must never throw and must default to empty when the file is absent.
        Assert.False(stamps.WedgeAt.HasValue);
        Assert.False(stamps.ReevalAt.HasValue);
        Assert.False(stamps.UpgradeAt.HasValue);
    }
}

/// Stamps-file writer round-trip (0001-core-brain.md, "Restart stamps" / R1-29) and the
/// paused-flag consume-and-clear file semantics (R2-11's pinned lifecycle). These tests read
/// and write the real per-user stamps file (there is no seam to inject a path), matching
/// LoadRestartStampsTests' existing precedent — each test restores the file to "absent" in a
/// finally block so it does not leak state into other tests in this collection.
public class RestartStampsPersistenceTests
{
    static readonly string StampsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "restart-stamps.json");

    [Fact]
    public void Saved_stamps_round_trip_through_LoadRestartStamps()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        try
        {
            var stamps = new Core.RestartStamps(
                wedgeAt: 1_700_000_000_000, reevalAt: 1_700_000_500_000, upgradeAt: 1_700_000_800_000);

            PolicyBridge.SaveRestartStamps(stamps, paused: false);
            var loaded = PolicyBridge.LoadRestartStamps();

            Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
            Assert.Equal(1_700_000_500_000, loaded.ReevalAt);
            Assert.Equal(1_700_000_800_000, loaded.UpgradeAt);
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }

    [Fact]
    public void Saved_stamps_with_only_one_reason_set_round_trip_the_others_as_absent()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        try
        {
            var stamps = new Core.RestartStamps(wedgeAt: 1_700_000_000_000, reevalAt: null, upgradeAt: null);

            PolicyBridge.SaveRestartStamps(stamps, paused: false);
            var loaded = PolicyBridge.LoadRestartStamps();

            Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
            Assert.False(loaded.ReevalAt.HasValue);
            Assert.False(loaded.UpgradeAt.HasValue);
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }

    /// Back-compat (0001-core-brain.md addendum 2026-08-01, slice 10): a stamps file written by
    /// a pre-slice-10 build has only WedgeAt/ReevalAt/Paused keys. The third field must load as
    /// null rather than fail, exactly like WedgeAt/ReevalAt themselves behaved before either had
    /// ever fired (R2-40's "confirming named Nullable fields remain the right shape at three").
    [Fact]
    public void An_old_two_field_stamps_file_loads_with_a_null_UpgradeAt()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StampsPath)!);
            File.WriteAllText(StampsPath, """{"WedgeAt":1700000000000,"ReevalAt":null,"Paused":false}""");

            var loaded = PolicyBridge.LoadRestartStamps();

            Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
            Assert.False(loaded.ReevalAt.HasValue);
            Assert.False(loaded.UpgradeAt.HasValue);
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }

    [Fact]
    public void Consume_returns_false_and_writes_nothing_when_no_file_exists()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        Assert.False(PolicyBridge.ConsumePersistedPausedFlag());
        Assert.False(File.Exists(StampsPath));
    }

    [Fact]
    public void Consume_returns_false_when_the_persisted_flag_is_not_set()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        try
        {
            PolicyBridge.SaveRestartStamps(new Core.RestartStamps(wedgeAt: null, reevalAt: null, upgradeAt: null), paused: false);
            Assert.False(PolicyBridge.ConsumePersistedPausedFlag());
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }

    [Fact]
    public void Consume_returns_true_once_and_clears_the_flag_while_preserving_stamps()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        try
        {
            var stamps = new Core.RestartStamps(wedgeAt: 42, reevalAt: 43, upgradeAt: 44);
            PolicyBridge.SaveRestartStamps(stamps, paused: true);

            Assert.True(PolicyBridge.ConsumePersistedPausedFlag());
            // Consumed-and-cleared: a second call sees the flag already cleared.
            Assert.False(PolicyBridge.ConsumePersistedPausedFlag());

            // Stamps themselves survive the clear untouched.
            var loaded = PolicyBridge.LoadRestartStamps();
            Assert.Equal(42L, loaded.WedgeAt);
            Assert.Equal(43L, loaded.ReevalAt);
            Assert.Equal(44L, loaded.UpgradeAt);
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }
}

/// Migration cleanup (0001-core-brain.md, "Restart stamps" / R2-34): the superseded
/// last-restart.txt is deleted the first time SaveRestartStamps runs on an upgraded build.
/// Same real-filesystem precedent as RestartStampsPersistenceTests — no seam to inject a path.
public class LegacyRestartStampCleanupTests
{
    static readonly string StampsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "restart-stamps.json");

    static readonly string LegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock", "last-restart.txt");

    [Fact]
    public void SaveRestartStamps_deletes_a_pre_existing_legacy_stamp_file()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        File.WriteAllText(LegacyPath, DateTime.UtcNow.ToString("o"));
        try
        {
            Assert.True(File.Exists(LegacyPath));

            PolicyBridge.SaveRestartStamps(new Core.RestartStamps(wedgeAt: null, reevalAt: null, upgradeAt: null), paused: false);

            Assert.False(File.Exists(LegacyPath));
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
            if (File.Exists(LegacyPath)) File.Delete(LegacyPath);
        }
    }

    [Fact]
    public void SaveRestartStamps_is_silent_when_no_legacy_stamp_file_exists()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        if (File.Exists(LegacyPath)) File.Delete(LegacyPath);
        try
        {
            // Absence is the steady state after the first upgraded run — must never throw and
            // must not conjure the legacy file back into existence.
            PolicyBridge.SaveRestartStamps(new Core.RestartStamps(wedgeAt: null, reevalAt: null, upgradeAt: null), paused: false);

            Assert.False(File.Exists(LegacyPath));
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }
}
