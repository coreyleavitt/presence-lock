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

    /// SEC-3: before this, BuildPolicyConfig validated lower bounds only -- an absurdly large
    /// away/grace/idle threshold or cooldown passed validation and silently made locking
    /// effectively unreachable while Status kept reading plain "Watching," with no annotation
    /// (unlike the SMTC inhibitor path). Each row below sends exactly one field over its new
    /// ceiling (see BuildPolicyConfig's ceiling comment) and asserts the existing all-or-nothing
    /// fallback contract still applies -- same mechanism SEC-3 extends, not a new one.
    [Theory]
    [InlineData(14401.0, 10.0, 10.0, 10000L, 20000L, 600000L, 3, 600000L, 600000L)]          // AwayThresholdSeconds over the 4h ceiling
    [InlineData(5.0, 14401.0, 10.0, 10000L, 20000L, 600000L, 3, 600000L, 600000L)]           // InputIdleSeconds over the 4h ceiling
    [InlineData(5.0, 10.0, 14401.0, 10000L, 20000L, 600000L, 3, 600000L, 600000L)]           // GraceSeconds over the 4h ceiling
    [InlineData(5.0, 10.0, 10.0, 14400001L, 20000000L, 600000L, 3, 600000L, 600000L)]        // NoSignalReportAfterMs over the 4h ceiling
    [InlineData(5.0, 10.0, 10.0, 10000L, 14400001L, 600000L, 3, 600000L, 600000L)]           // ReevaluateAfterMs over the 4h ceiling
    [InlineData(5.0, 10.0, 10.0, 10000L, 20000L, 86400001L, 3, 600000L, 600000L)]            // ReevaluateCooldownMs over the 24h ceiling
    [InlineData(5.0, 10.0, 10.0, 10000L, 20000L, 600000L, 101, 600000L, 600000L)]            // RecoveryFailureThreshold over the ceiling of 100
    [InlineData(5.0, 10.0, 10.0, 10000L, 20000L, 600000L, 3, 86400001L, 600000L)]            // RecoveryCooldownMs over the 24h ceiling
    [InlineData(5.0, 10.0, 10.0, 10000L, 20000L, 600000L, 3, 600000L, 86400001L)]            // UpgradeCooldownMs over the 24h ceiling
    public void A_field_over_its_upper_bound_falls_back_to_the_full_default_set(
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
        Assert.Equal(expectedDefaults.RecoveryCooldownMs, policy.RecoveryCooldownMs);
        Assert.Equal(expectedDefaults.UpgradeCooldownMs, policy.UpgradeCooldownMs);
    }

    /// Boundary values sitting exactly AT each new ceiling must pass through untouched -- the
    /// ceiling is inclusive, matching SanitizeSensingConfig's existing [100, 10000]/[0, 255]
    /// inclusive-range precedent (`Boundary_values_at_the_edges_of_the_valid_range_pass_through_untouched`).
    [Fact]
    public void Boundary_values_exactly_at_each_new_ceiling_pass_through_untouched()
    {
        var away = PolicyBridge.BuildPolicyConfig(new Config { AwayThresholdSeconds = 14400.0 });
        Assert.Equal(14_400_000L, away.AwayThresholdMs);

        var idle = PolicyBridge.BuildPolicyConfig(new Config { InputIdleSeconds = 14400.0 });
        Assert.Equal(14_400_000L, idle.InputIdleRequiredMs);

        var grace = PolicyBridge.BuildPolicyConfig(new Config { GraceSeconds = 14400.0 });
        Assert.Equal(14_400_000L, grace.GraceMs);

        // NoSignalReportAfterMs's ceiling coincides with ReevaluateAfterMs's own ceiling, and
        // ReevaluateAfterMs must be >= NoSignalReportAfterMs -- both sit at the shared ceiling.
        var noSignal = PolicyBridge.BuildPolicyConfig(new Config
        {
            NoSignalReportAfterMs = 14_400_000L,
            ReevaluateAfterMs = 14_400_000L,
        });
        Assert.Equal(14_400_000L, noSignal.NoSignalReportAfterMs);
        Assert.Equal(14_400_000L, noSignal.ReevaluateAfterMs);

        var reevaluateCooldown = PolicyBridge.BuildPolicyConfig(new Config { ReevaluateCooldownMs = 86_400_000L });
        Assert.Equal(86_400_000L, reevaluateCooldown.ReevaluateCooldownMs);

        var recoveryThreshold = PolicyBridge.BuildPolicyConfig(new Config { RecoveryFailureThreshold = 100 });
        Assert.Equal(100, recoveryThreshold.RecoveryFailureThreshold);

        var recoveryCooldown = PolicyBridge.BuildPolicyConfig(new Config { RecoveryCooldownMs = 86_400_000L });
        Assert.Equal(86_400_000L, recoveryCooldown.RecoveryCooldownMs);

        var upgradeCooldown = PolicyBridge.BuildPolicyConfig(new Config { UpgradeCooldownMs = 86_400_000L });
        Assert.Equal(86_400_000L, upgradeCooldown.UpgradeCooldownMs);
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

    /// Stage-4 follow-up ("Status annotation when config makes locking effectively
    /// unreachable"): the out-param is the annotation's one source of truth, so it must agree
    /// exactly with the all-or-nothing fallback — true whenever DefaultPolicyConfig was
    /// substituted, false whenever the on-disk values were accepted.
    [Fact]
    public void Out_of_range_policy_fields_report_defaults_in_use()
    {
        PolicyBridge.BuildPolicyConfig(new Config { AwayThresholdSeconds = 0.0 }, out bool defaultsInUse);
        Assert.True(defaultsInUse);
    }

    [Fact]
    public void In_range_policy_fields_report_defaults_not_in_use()
    {
        PolicyBridge.BuildPolicyConfig(new Config(), out bool defaultsInUse);
        Assert.False(defaultsInUse);
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

/// The sampling watchdog's starvation-threshold computation (incident 2026-08-12, DES-4):
/// extracted out of the watchdog-tick lambda's inline `Math.Max(10L * cfg.SampleIntervalMs,
/// 15000)` specifically so the two magic numbers (the 10x multiplier, the 15000ms floor) get
/// the same independent, dedicated unit coverage as `SamplingWatchdogStep` itself rather than
/// living untested inside a lambda.
public class SamplingStarvationThresholdMsTests
{
    [Fact]
    public void The_15_second_floor_dominates_at_a_small_sample_interval()
    {
        // 10 * 100ms = 1000ms, well under the 15s floor.
        Assert.Equal(15000L, PolicyBridge.SamplingStarvationThresholdMs(100));
    }

    [Fact]
    public void The_10x_term_dominates_at_a_large_sample_interval()
    {
        // 10 * 5000ms = 50000ms, well over the 15s floor.
        Assert.Equal(50000L, PolicyBridge.SamplingStarvationThresholdMs(5000));
    }

    [Fact]
    public void The_crossover_boundary_is_exactly_1500ms_where_both_terms_agree()
    {
        // 10 * 1500 == 15000 == the floor -- both formulas agree exactly at this interval.
        Assert.Equal(15000L, PolicyBridge.SamplingStarvationThresholdMs(1500));

        // Just below the crossover: the 10x term (14990) is still under the floor, so the
        // floor wins.
        Assert.Equal(15000L, PolicyBridge.SamplingStarvationThresholdMs(1499));

        // Just above the crossover: the 10x term (15010) has overtaken the floor.
        Assert.Equal(15010L, PolicyBridge.SamplingStarvationThresholdMs(1501));
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

/// Hermetic base for every test class that exercises the stamps-file functions (stage-4
/// follow-up: these previously read/wrote the real per-user %LOCALAPPDATA% file — "no seam to
/// inject a path" — and flaked once in the Windows container with UnauthorizedAccessException).
/// xunit constructs a fresh instance per test method, so each test gets its own temp state dir
/// and runs parallel-safe with no cross-test file cleanup choreography; Dispose removes the dir.
public abstract class StampsStateDirFixture : IDisposable
{
    private protected readonly string stateDir = Path.Combine(
        Path.GetTempPath(), "PresenceLock.Tests", Guid.NewGuid().ToString("N"));

    private protected string StampsPath => Path.Combine(stateDir, "restart-stamps.json");

    public void Dispose()
    {
        try { Directory.Delete(stateDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

public class LoadRestartStampsTests : StampsStateDirFixture
{
    [Fact]
    public void Missing_file_yields_empty_stamps()
    {
        var stamps = PolicyBridge.LoadRestartStamps(stateDir);
        // This call must never throw and must default to empty when the file is absent.
        Assert.False(stamps.WedgeAt.HasValue);
        Assert.False(stamps.ReevalAt.HasValue);
        Assert.False(stamps.UpgradeAt.HasValue);
    }
}

/// Stamps-file writer round-trip (0001-core-brain.md, "Restart stamps" / R1-29) and the
/// paused-flag consume-and-clear file semantics (R2-11's pinned lifecycle), each against its
/// own temp state dir per StampsStateDirFixture.
public class RestartStampsPersistenceTests : StampsStateDirFixture
{
    [Fact]
    public void Saved_stamps_round_trip_through_LoadRestartStamps()
    {
        var stamps = new Core.RestartStamps(
            wedgeAt: 1_700_000_000_000, reevalAt: 1_700_000_500_000, upgradeAt: 1_700_000_800_000);

        PolicyBridge.SaveRestartStamps(stamps, paused: false, stateDir);
        var loaded = PolicyBridge.LoadRestartStamps(stateDir);

        Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
        Assert.Equal(1_700_000_500_000, loaded.ReevalAt);
        Assert.Equal(1_700_000_800_000, loaded.UpgradeAt);
    }

    [Fact]
    public void Saved_stamps_with_only_one_reason_set_round_trip_the_others_as_absent()
    {
        var stamps = new Core.RestartStamps(wedgeAt: 1_700_000_000_000, reevalAt: null, upgradeAt: null);

        PolicyBridge.SaveRestartStamps(stamps, paused: false, stateDir);
        var loaded = PolicyBridge.LoadRestartStamps(stateDir);

        Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
        Assert.False(loaded.ReevalAt.HasValue);
        Assert.False(loaded.UpgradeAt.HasValue);
    }

    /// Back-compat (0001-core-brain.md addendum 2026-08-01, slice 10): a stamps file written by
    /// a pre-slice-10 build has only WedgeAt/ReevalAt/Paused keys. The third field must load as
    /// null rather than fail, exactly like WedgeAt/ReevalAt themselves behaved before either had
    /// ever fired (R2-40's "confirming named Nullable fields remain the right shape at three").
    [Fact]
    public void An_old_two_field_stamps_file_loads_with_a_null_UpgradeAt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StampsPath)!);
        File.WriteAllText(StampsPath, """{"WedgeAt":1700000000000,"ReevalAt":null,"Paused":false}""");

        var loaded = PolicyBridge.LoadRestartStamps(stateDir);

        Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
        Assert.False(loaded.ReevalAt.HasValue);
        Assert.False(loaded.UpgradeAt.HasValue);
    }

    [Fact]
    public void Consume_returns_false_and_writes_nothing_when_no_file_exists()
    {
        Assert.False(PolicyBridge.ConsumePersistedPausedFlag(stateDir));
        Assert.False(File.Exists(StampsPath));
    }

    [Fact]
    public void Consume_returns_false_when_the_persisted_flag_is_not_set()
    {
        PolicyBridge.SaveRestartStamps(new Core.RestartStamps(wedgeAt: null, reevalAt: null, upgradeAt: null), paused: false, stateDir);
        Assert.False(PolicyBridge.ConsumePersistedPausedFlag(stateDir));
    }

    [Fact]
    public void Consume_returns_true_once_and_clears_the_flag_while_preserving_stamps()
    {
        var stamps = new Core.RestartStamps(wedgeAt: 42, reevalAt: 43, upgradeAt: 44);
        PolicyBridge.SaveRestartStamps(stamps, paused: true, stateDir);

        Assert.True(PolicyBridge.ConsumePersistedPausedFlag(stateDir));
        // Consumed-and-cleared: a second call sees the flag already cleared.
        Assert.False(PolicyBridge.ConsumePersistedPausedFlag(stateDir));

        // Stamps themselves survive the clear untouched.
        var loaded = PolicyBridge.LoadRestartStamps(stateDir);
        Assert.Equal(42L, loaded.WedgeAt);
        Assert.Equal(43L, loaded.ReevalAt);
        Assert.Equal(44L, loaded.UpgradeAt);
    }
}

/// Consequences of one before/after `Status` pair across an `Advance` call (RFC
/// 0002-environment-levels, "Advance and timers", round 3). Extracted as a pure function so
/// the sample-timer restart decision on a WTS-correction-driven unlock — which the
/// verification harness cannot see, since it models `step` semantics, not WinForms timers —
/// is independently testable here.
public class SuppressionTransitionStepTests
{
    [Fact]
    public void A_suppressed_to_unsuppressed_transition_resets_filters_restarts_sampling_and_kicks_acquisition()
    {
        var verdict = PolicyBridge.SuppressionTransitionStep(Core.Status.SessionLocked, Core.Status.Watching);

        Assert.True(verdict.ResetFilters);
        Assert.True(verdict.RestartSampleTimer);
        Assert.True(verdict.KickAcquisition);
        Assert.False(verdict.StopSampleTimer);
        Assert.NotNull(verdict.TransitionLogMessage);
        Assert.StartsWith("suppression exited:", verdict.TransitionLogMessage);
    }

    [Fact]
    public void An_unsuppressed_to_suppressed_transition_stops_sampling_only()
    {
        var verdict = PolicyBridge.SuppressionTransitionStep(Core.Status.Watching, Core.Status.Paused);

        Assert.False(verdict.ResetFilters);
        Assert.False(verdict.RestartSampleTimer);
        Assert.False(verdict.KickAcquisition);
        Assert.True(verdict.StopSampleTimer);
        Assert.NotNull(verdict.TransitionLogMessage);
        Assert.StartsWith("suppression entered:", verdict.TransitionLogMessage);
    }

    [Fact]
    public void No_transition_when_suppression_state_is_unchanged_either_way()
    {
        var stillUnsuppressed = PolicyBridge.SuppressionTransitionStep(Core.Status.Watching, Core.Status.NoSignal);
        var stillSuppressed = PolicyBridge.SuppressionTransitionStep(Core.Status.Paused, Core.Status.SessionLocked);

        foreach (var verdict in new[] { stillUnsuppressed, stillSuppressed })
        {
            Assert.False(verdict.ResetFilters);
            Assert.False(verdict.RestartSampleTimer);
            Assert.False(verdict.KickAcquisition);
            Assert.False(verdict.StopSampleTimer);
            Assert.Null(verdict.TransitionLogMessage);
        }
    }
}

/// `WTSINFOEX.SessionFlags` interpretation (RFC 0002-environment-levels, "Session mirror with
/// reconciliation", spike deliverable): documented Windows 8+ semantics confirmed live on this
/// machine (Windows 11 26200) against a demonstrably unlocked console session. Pure and
/// separated from the P/Invoke call itself so this is the one piece of the query path testable
/// without a live WTS handle.
public class InterpretSessionFlagsTests
{
    [Fact]
    public void WTS_SESSIONSTATE_LOCK_zero_reports_succeeded_and_locked()
    {
        var result = PolicyBridge.InterpretSessionFlags(0);
        Assert.True(result.Succeeded);
        Assert.True(result.Locked);
    }

    [Fact]
    public void WTS_SESSIONSTATE_UNLOCK_one_reports_succeeded_and_unlocked()
    {
        var result = PolicyBridge.InterpretSessionFlags(1);
        Assert.True(result.Succeeded);
        Assert.False(result.Locked);
    }

    [Fact]
    public void WTS_SESSIONSTATE_UNKNOWN_negative_one_fails_open_rather_than_guessing_a_lock_state()
    {
        var result = PolicyBridge.InterpretSessionFlags(-1);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(42)]
    [InlineData(int.MinValue)]
    public void Any_other_unrecognized_value_also_fails_open(int sessionFlags)
    {
        Assert.False(PolicyBridge.InterpretSessionFlags(sessionFlags).Succeeded);
    }
}

/// Round-3 boundary rule 2's companion (RFC 0002-environment-levels, "Session mirror with
/// reconciliation"): whether one `SessionSwitch` delivery restarts the reconciliation skip
/// window. Kept as its own tiny pure function, separate from `ReconciliationStep`, so this
/// specific correctness rule — an idempotent duplicate delivery must NOT restart the window —
/// has its own dedicated unit test rather than being trusted to a single `if` in
/// `OnSessionSwitch`.
public class SessionSwitchTicksAfterDeliveryTests
{
    [Fact]
    public void A_mirror_value_changing_delivery_resets_ticks_to_zero()
    {
        Assert.Equal(0, PolicyBridge.SessionSwitchTicksAfterDelivery(mirrorValueChanged: true, currentTicks: 7));
    }

    [Fact]
    public void An_idempotent_duplicate_delivery_leaves_the_ticks_counter_unchanged()
    {
        // Windows demonstrably double-fires SessionSwitch (0001-core-brain.md): a duplicate
        // delivery that did NOT change the mirror must not restart the skip window, or a stuck
        // mirror's heal could be pushed out indefinitely under unrelated session traffic.
        Assert.Equal(7, PolicyBridge.SessionSwitchTicksAfterDelivery(mirrorValueChanged: false, currentTicks: 7));
    }
}

/// The pure WTS-reconciliation decision (RFC 0002-environment-levels, "Session mirror with
/// reconciliation"), extracted per the RFC's explicit instruction, mirroring the
/// `SamplingWatchdogStep` precedent. Covers, at minimum, every case the RFC names by name:
/// fail-open on query failure, correction on exactly the second disagreeing tick, a failed
/// query interleaved between two disagreeing ticks, an idempotent duplicate `SessionSwitch`
/// not restarting the skip window, skip-window suppression, and the compounding
/// skip-plus-hysteresis worst case behind the ~three-watchdog-interval bound.
public class ReconciliationStepTests
{
    [Fact]
    public void Fail_open_on_query_failure_never_sets_locked_true_and_holds_the_counter()
    {
        // Mirror is unlocked; the OS query claims locked, but the query itself failed. A
        // failed query must NEVER set locked=true (RFC: "defaulting broken data to 'locked'
        // would silently suppress protection — the fail-closed dim-light mistake in new
        // clothes"). Round-3 boundary rule 1: the counter is HELD, not reset and not
        // incremented.
        var verdict = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: false,
            ticksSinceLastSessionSwitchMirrorChange: 10, consecutiveDisagreementCount: 1);

        Assert.False(verdict.Mirror);
        Assert.Equal(1, verdict.DisagreementCount);
        Assert.False(verdict.CorrectionApplied);
    }

    [Fact]
    public void Correction_applies_on_exactly_the_second_disagreeing_tick_not_the_first_or_third()
    {
        var first = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 5, consecutiveDisagreementCount: 0);
        Assert.False(first.CorrectionApplied);
        Assert.False(first.Mirror);
        Assert.Equal(1, first.DisagreementCount);

        var second = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 5, consecutiveDisagreementCount: first.DisagreementCount);
        Assert.True(second.CorrectionApplied);
        Assert.True(second.Mirror);
        Assert.Equal(0, second.DisagreementCount);

        // Third tick: mirror is already corrected, so the OS query now AGREES — no further
        // correction fires (proving the second tick didn't leave anything pending).
        var third = PolicyBridge.ReconciliationStep(
            mirror: second.Mirror, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 5, consecutiveDisagreementCount: second.DisagreementCount);
        Assert.False(third.CorrectionApplied);
        Assert.Equal(0, third.DisagreementCount);
    }

    [Fact]
    public void A_failed_query_interleaved_between_two_disagreeing_ticks_holds_the_counter_costing_exactly_one_extra_tick()
    {
        var tick1 = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 5, consecutiveDisagreementCount: 0);
        Assert.False(tick1.CorrectionApplied);
        Assert.Equal(1, tick1.DisagreementCount);

        // Query fails: counter HELD at 1 — neither reset to 0 nor incremented to 2. A
        // reset-on-failure reading would let an intermittent-failure pattern starve the heal
        // forever.
        var tick2 = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: false,
            ticksSinceLastSessionSwitchMirrorChange: 5, consecutiveDisagreementCount: tick1.DisagreementCount);
        Assert.False(tick2.CorrectionApplied);
        Assert.Equal(1, tick2.DisagreementCount);

        // Disagreement resumes: this is the SECOND disagreeing tick (the failed tick didn't
        // count against the streak), so correction applies now — one extra tick overall (3
        // ticks instead of 2), never a restarted count.
        var tick3 = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 5, consecutiveDisagreementCount: tick2.DisagreementCount);
        Assert.True(tick3.CorrectionApplied);
        Assert.True(tick3.Mirror);
    }

    [Fact]
    public void An_idempotent_duplicate_SessionSwitch_does_not_restart_the_skip_window()
    {
        // Composes with SessionSwitchTicksAfterDelivery (the shell's actual tracking function):
        // a genuine change resets ticks to zero; one real watchdog interval elapses; then an
        // idempotent duplicate SessionSwitch delivery must NOT reset it back to zero.
        int afterGenuineChange = PolicyBridge.SessionSwitchTicksAfterDelivery(mirrorValueChanged: true, currentTicks: 99);
        int afterOneRealInterval = afterGenuineChange + 1;
        int afterDuplicateDelivery = PolicyBridge.SessionSwitchTicksAfterDelivery(mirrorValueChanged: false, currentTicks: afterOneRealInterval);
        Assert.Equal(1, afterDuplicateDelivery); // NOT restarted back to 0 by the duplicate

        // With the window correctly not restarted, reconciliation at this tick count is no
        // longer skipped — the disagreement counter moves on this tick.
        var verdict = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: afterDuplicateDelivery, consecutiveDisagreementCount: 0);
        Assert.False(verdict.CorrectionApplied); // first disagreeing tick, hysteresis not yet met
        Assert.Equal(1, verdict.DisagreementCount); // moved -- proof it was not skipped
    }

    [Fact]
    public void Skip_window_suppresses_reconciliation_entirely_even_with_a_real_disagreement()
    {
        var verdict = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 0, consecutiveDisagreementCount: 0);

        Assert.False(verdict.Mirror);
        Assert.Equal(0, verdict.DisagreementCount); // untouched -- "skipped entirely"
        Assert.False(verdict.CorrectionApplied);
    }

    [Fact]
    public void The_compounding_skip_plus_hysteresis_worst_case_corrects_within_three_watchdog_ticks()
    {
        // RFC: "worst case one SessionSwitch-adjacency skip window plus two hysteresis ticks
        // ~= three watchdog intervals" — conditional on bounded consecutive query failures
        // (zero failures here; the interleaved-failure test above shows the +1-tick extension
        // per failure).
        var tick1 = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 0, consecutiveDisagreementCount: 0);
        Assert.False(tick1.CorrectionApplied);
        Assert.Equal(0, tick1.DisagreementCount);

        var tick2 = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 1, consecutiveDisagreementCount: tick1.DisagreementCount);
        Assert.False(tick2.CorrectionApplied);
        Assert.Equal(1, tick2.DisagreementCount);

        var tick3 = PolicyBridge.ReconciliationStep(
            mirror: false, queriedLocked: true, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 2, consecutiveDisagreementCount: tick2.DisagreementCount);
        Assert.True(tick3.CorrectionApplied); // corrected on the third watchdog tick, not before
        Assert.True(tick3.Mirror);
    }
}

/// DES-2: `SessionLockMirror` owns the three fields that used to be loose `WatcherContext` state
/// (`sessionLocked`/`ticksSinceLastSessionSwitchMirrorChange`/`sessionLockDisagreementCount`)
/// behind `OnSessionSwitch`/`Reconcile`, delegating the actual decision to the existing
/// `SessionSwitchTicksAfterDelivery`/`ReconciliationStep` unchanged -- this type is field
/// ownership, not new policy. These tests pin that the delegation and field bookkeeping are
/// wired correctly (in particular, that the skip-window reset/hold behavior these two methods
/// share via the same private counter survives the extraction) rather than re-testing the pure
/// functions themselves, which are already covered by `ReconciliationStepTests`/
/// `SessionSwitchTicksAfterDeliveryTests` above.
public class SessionLockMirrorTests
{
    [Fact]
    public void Constructed_with_the_startup_value_exposes_it_as_Locked()
    {
        Assert.True(new SessionLockMirror(initialLocked: true).Locked);
        Assert.False(new SessionLockMirror(initialLocked: false).Locked);
    }

    [Fact]
    public void OnSessionSwitch_sets_Locked_to_the_delivered_value_in_either_direction()
    {
        var mirror = new SessionLockMirror(initialLocked: false);

        mirror.OnSessionSwitch(locked: true);
        Assert.True(mirror.Locked);

        mirror.OnSessionSwitch(locked: false);
        Assert.False(mirror.Locked);
    }

    [Fact]
    public void A_genuine_change_from_OnSessionSwitch_resets_the_skip_window_so_correction_needs_a_third_tick()
    {
        // Mirrors ReconciliationStepTests' compounding skip-plus-hysteresis case, but through
        // SessionLockMirror's own OnSessionSwitch/Reconcile surface: a genuine mirror-changing
        // SessionSwitch must reset the skip window to zero, so the very next Reconcile call is
        // skipped entirely. Proof: correction needs a THIRD disagreeing Reconcile call here, not
        // a second -- a stuck/un-reset skip window would correct on the second call instead.
        var mirror = new SessionLockMirror(initialLocked: false);
        mirror.OnSessionSwitch(locked: true); // genuine change: Locked -> true, skip window -> 0

        var tick1 = mirror.Reconcile(querySucceeded: true, queriedLocked: false); // skipped (window == 0)
        Assert.False(tick1.CorrectionApplied);
        var tick2 = mirror.Reconcile(querySucceeded: true, queriedLocked: false); // first real disagreement
        Assert.False(tick2.CorrectionApplied);
        var tick3 = mirror.Reconcile(querySucceeded: true, queriedLocked: false); // second -> corrects
        Assert.True(tick3.CorrectionApplied);
        Assert.False(mirror.Locked);
    }

    [Fact]
    public void An_idempotent_duplicate_SessionSwitch_does_not_restart_the_skip_window()
    {
        var mirror = new SessionLockMirror(initialLocked: true);
        // Advance the skip window well past the constructor default via two agreeing (no-op)
        // Reconcile calls, so the window is unambiguously open before the duplicate delivery.
        mirror.Reconcile(querySucceeded: true, queriedLocked: true);
        mirror.Reconcile(querySucceeded: true, queriedLocked: true);

        mirror.OnSessionSwitch(locked: true); // duplicate delivery: no change, window must NOT reset

        // If the window had been (wrongly) reset to zero, correction would need a third
        // disagreeing tick (as in the genuine-change test above). Correcting on the SECOND
        // disagreeing tick instead proves the duplicate left the window running.
        var tick1 = mirror.Reconcile(querySucceeded: true, queriedLocked: false);
        Assert.False(tick1.CorrectionApplied);
        var tick2 = mirror.Reconcile(querySucceeded: true, queriedLocked: false);
        Assert.True(tick2.CorrectionApplied);
    }

    [Fact]
    public void Reconcile_applies_a_correction_on_the_second_consecutive_disagreeing_tick_and_reports_old_and_new_values()
    {
        var mirror = new SessionLockMirror(initialLocked: true);
        mirror.Reconcile(querySucceeded: true, queriedLocked: true); // advance the skip window, no disagreement

        var first = mirror.Reconcile(querySucceeded: true, queriedLocked: false);
        Assert.False(first.CorrectionApplied);
        Assert.True(mirror.Locked); // uncorrected yet -- hysteresis not met

        var second = mirror.Reconcile(querySucceeded: true, queriedLocked: false);
        Assert.True(second.CorrectionApplied);
        Assert.True(second.OldLocked);
        Assert.False(second.NewLocked);
        Assert.False(mirror.Locked);
    }

    [Fact]
    public void A_failed_query_never_corrects_the_mirror_even_when_the_streak_would_otherwise_be_ready()
    {
        var mirror = new SessionLockMirror(initialLocked: false);
        mirror.Reconcile(querySucceeded: true, queriedLocked: false); // advance the skip window, agrees
        mirror.Reconcile(querySucceeded: true, queriedLocked: true);  // first disagreeing tick

        var result = mirror.Reconcile(querySucceeded: false, queriedLocked: true); // query fails on what would be the second

        Assert.False(result.CorrectionApplied);
        Assert.False(mirror.Locked);
    }

    /// SHELL-1: `OnSessionSwitch`'s return value is what `WatcherContext.OnSessionSwitch` gates
    /// its log line and `Advance(Core.Event.Reconcile)` call on, so a duplicate/idempotent
    /// SessionSwitch delivery (Windows demonstrably double-fires these) does no redundant work --
    /// matching the other two mirror-changing sites (WTS reconciliation gates on
    /// `CorrectionApplied`; inhibitor aggregation gates on `Refresh()`'s own `changed`).
    [Fact]
    public void OnSessionSwitch_returns_true_for_a_genuine_lock_to_unlock_or_unlock_to_lock_transition()
    {
        var mirror = new SessionLockMirror(initialLocked: false);

        Assert.True(mirror.OnSessionSwitch(locked: true));
        Assert.True(mirror.Locked);

        Assert.True(mirror.OnSessionSwitch(locked: false));
        Assert.False(mirror.Locked);
    }

    [Fact]
    public void OnSessionSwitch_returns_false_for_a_duplicate_delivery_that_does_not_change_Locked()
    {
        var mirror = new SessionLockMirror(initialLocked: true);

        // A duplicate SessionLock delivery: the mirror is already locked, so this is a genuine
        // no-op -- the caller must not call Advance for it.
        bool changed = mirror.OnSessionSwitch(locked: true);

        Assert.False(changed);
        Assert.True(mirror.Locked); // unchanged, and correctly still locked
    }

    [Fact]
    public void OnSessionSwitch_returning_false_still_leaves_Locked_at_the_delivered_value()
    {
        // Even on the no-op path, Locked must reflect the delivered value exactly (it already
        // did, which is precisely why changed is false) -- this pins that the no-op short
        // circuit lives entirely in the caller's Advance-gating decision, never in whether
        // Locked itself gets set.
        var mirror = new SessionLockMirror(initialLocked: false);

        bool changed = mirror.OnSessionSwitch(locked: false);

        Assert.False(changed);
        Assert.False(mirror.Locked);
    }
}

/// SEC-4: a persisted `Paused:true` restart-stamps flag is deliberately inherited across a
/// self-restart (RFC 0002-environment-levels, "Pause ownership") -- but the stamps file is
/// same-user readable/writable, so a forged or corrupted flag would otherwise start the app
/// paused with no cue beyond the tray text. This pins the pure trigger: exactly one non-null
/// WARNING message when startup inherited a pause, and no message (and therefore no log line)
/// otherwise -- mirroring SuppressionTransitionVerdict.TransitionLogMessage's established
/// null-means-no-log contract in this same file.
public class StartupPauseInheritedWarningTests
{
    [Fact]
    public void Inherited_pause_produces_a_single_loud_warning_message()
    {
        var message = PolicyBridge.StartupPauseInheritedWarning(startupPaused: true);

        Assert.NotNull(message);
        Assert.StartsWith("WARNING:", message);
        Assert.Contains("PAUSED", message);
    }

    [Fact]
    public void No_inherited_pause_produces_no_warning_message()
    {
        Assert.Null(PolicyBridge.StartupPauseInheritedWarning(startupPaused: false));
    }
}

/// The startup input-assembly ordering pin (RFC 0002-environment-levels, "Construction
/// discipline" pinned test / "Session mirror with reconciliation" startup bullet): the
/// constructor itself can't be driven under xunit (WinForms/WinRT construction), so
/// `WatcherContext.AssembleStartupInputs` is the extracted seam — a pure orchestrator over
/// injected delegates whose call order this test records and asserts directly, pinning that
/// pause consume-and-clear runs before the WTS query, which runs before any `StepInputs` is
/// assembled. A misordered constructor would fail SILENTLY as "unsuppressed" — exactly the
/// class of defect this pin exists to catch.
public class AssembleStartupInputsTests
{
    [Fact]
    public void Consume_then_query_then_read_idle_run_in_that_exact_order()
    {
        var callOrder = new List<string>();

        var result = WatcherContext.AssembleStartupInputs(
            consumePersistedPausedFlag: () => { callOrder.Add("consume"); return true; },
            queryWtsSessionLocked: () =>
            {
                callOrder.Add("query");
                return new PolicyBridge.WtsLockQueryResult(Succeeded: true, Locked: true);
            },
            readInputIdleMs: () => { callOrder.Add("idle"); return 4242L; });

        Assert.Equal(new[] { "consume", "query", "idle" }, callOrder);
        Assert.True(result.Paused);
        Assert.True(result.SessionLocked);
        Assert.True(result.Inputs.Paused);
        Assert.True(result.Inputs.SessionLocked);
        Assert.False(result.Inputs.LockInhibited);
        Assert.Equal(4242L, result.Inputs.InputIdleMs);
    }

    [Fact]
    public void A_failed_WTS_query_fails_open_to_an_unlocked_mirror_even_if_the_payload_claims_locked()
    {
        // RFC: "a failed query never sets locked=true" — pinned at the startup path too, not
        // only the watchdog-tick path.
        var result = WatcherContext.AssembleStartupInputs(
            consumePersistedPausedFlag: () => false,
            queryWtsSessionLocked: () => new PolicyBridge.WtsLockQueryResult(Succeeded: false, Locked: true),
            readInputIdleMs: () => 0L);

        Assert.False(result.SessionLocked);
        Assert.False(result.Inputs.SessionLocked);
    }

    [Fact]
    public void A_successful_query_reporting_unlocked_yields_an_unsuppressed_startup_mirror()
    {
        var result = WatcherContext.AssembleStartupInputs(
            consumePersistedPausedFlag: () => false,
            queryWtsSessionLocked: () => new PolicyBridge.WtsLockQueryResult(Succeeded: true, Locked: false),
            readInputIdleMs: () => 0L);

        Assert.False(result.Paused);
        Assert.False(result.SessionLocked);
    }
}

/// RFC 0002-environment-levels, slice 4: "a shell test asserts sampling resumes after a
/// WTS-correction-driven unlock ... the harness models `step`, not WinForms timers — this leg
/// only a shell test can pin." The real `WatcherContext` can't be driven under xunit, so this
/// composes the real pieces the correction path actually runs through —
/// `PolicyBridge.ReconciliationStep`, the real `Core.Policy.step`, and
/// `PolicyBridge.SuppressionTransitionStep` — proving the sample-timer RESTART DECISION is
/// reached, not merely that some `Advance`-shaped function was invoked. Only the literal
/// WinForms `sampleTimer.Start()` call itself stays outside this seam, per the RFC's own
/// guidance ("the WinForms Timer.Start call itself may stay one thin layer outside the seam").
public class WtsCorrectionResumesSamplingTests
{
    [Fact]
    public void A_WTS_correction_driven_unlock_reaches_the_sample_timer_restart_decision()
    {
        var config = PolicyBridge.BuildPolicyConfig(new Config());

        // A restart-while-locked process: the startup mirror is locked.
        var startInputs = new Core.StepInputs(sessionLocked: true, paused: false, lockInhibited: false, inputIdleMs: 0L);
        var state = Core.Policy.start(
            Core.MonotonicMs.NewMonotonicMs(0),
            new Core.RestartStamps(wedgeAt: null, reevalAt: null, upgradeAt: null),
            startInputs);
        var previousStatus = Core.Policy.status(state);
        Assert.Equal(Core.Status.SessionLocked, previousStatus);

        // Two consecutive disagreeing watchdog ticks (WTS reports unlocked): correction applies
        // on exactly the second, per ReconciliationStep's hysteresis.
        var tick1 = PolicyBridge.ReconciliationStep(
            mirror: true, queriedLocked: false, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 10, consecutiveDisagreementCount: 0);
        Assert.False(tick1.CorrectionApplied);

        var tick2 = PolicyBridge.ReconciliationStep(
            mirror: true, queriedLocked: false, querySucceeded: true,
            ticksSinceLastSessionSwitchMirrorChange: 10, consecutiveDisagreementCount: tick1.DisagreementCount);
        Assert.True(tick2.CorrectionApplied);
        Assert.False(tick2.Mirror);

        // Corrections drive behavior, not just the bool (RFC): mirror update then
        // Advance(Reconcile) — here, the real Core.Policy.step call with the corrected mirror.
        var correctedInputs = new Core.StepInputs(sessionLocked: tick2.Mirror, paused: false, lockInhibited: false, inputIdleMs: 0L);
        var ctx = new Core.StepContext(
            now: Core.MonotonicMs.NewMonotonicMs(1000),
            nowWall: Core.WallClockMs.NewWallClockMs(1000),
            inputs: correctedInputs);
        var result = Core.Policy.step(config, state, ctx, Core.Event.Reconcile);
        var newStatus = Core.Policy.status(result.State);

        Assert.NotEqual(Core.Status.SessionLocked, newStatus);

        // The Advance-internal suppression-transition rule (which owns the sample-timer restart
        // decision by construction) must decide to restart sampling on this exact
        // (previousStatus, newStatus) pair — not merely "Advance was called."
        var transition = PolicyBridge.SuppressionTransitionStep(previousStatus, newStatus);
        Assert.True(transition.RestartSampleTimer);
        Assert.True(transition.ResetFilters);
        Assert.True(transition.KickAcquisition);
        Assert.False(transition.StopSampleTimer);
    }
}

/// RFC 0002-environment-levels, "Slices" item 4: "a shell test asserts every Status case
/// renders without throwing" — pinning that no future Status arm needs a new StatusText
/// guard. Enumerates the DU's cases via reflection rather than a hand-written list of today's
/// six, specifically so a future seventh `Status` case is picked up automatically and would
/// fail this test through `StatusText`'s `UnreachableException` default arm — exactly the
/// crash the RFC calls out ("a seventh case would crash the watchdog on its first inhibited
/// tick").
public class StatusTextExhaustivenessTests
{
    [Fact]
    public void Every_Status_case_renders_via_StatusText_without_throwing()
    {
        var allStatusValues = typeof(Core.Status)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(Core.Status))
            .Select(p => (Core.Status)p.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(allStatusValues);
        foreach (var status in allStatusValues)
        {
            Assert.False(string.IsNullOrEmpty(PolicyBridge.StatusText(status, lastObservationDark: false)));
            Assert.False(string.IsNullOrEmpty(PolicyBridge.StatusText(status, lastObservationDark: true)));
        }
    }
}

/// Migration cleanup (0001-core-brain.md, "Restart stamps" / R2-34): the superseded
/// last-restart.txt is deleted the first time SaveRestartStamps runs on an upgraded build.
/// Hermetic per StampsStateDirFixture, like every other stamps-file test.
public class LegacyRestartStampCleanupTests : StampsStateDirFixture
{
    string LegacyPath => Path.Combine(stateDir, "last-restart.txt");

    [Fact]
    public void SaveRestartStamps_deletes_a_pre_existing_legacy_stamp_file()
    {
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(LegacyPath, DateTime.UtcNow.ToString("o"));
        Assert.True(File.Exists(LegacyPath));

        PolicyBridge.SaveRestartStamps(new Core.RestartStamps(wedgeAt: null, reevalAt: null, upgradeAt: null), paused: false, stateDir);

        Assert.False(File.Exists(LegacyPath));
    }

    [Fact]
    public void SaveRestartStamps_is_silent_when_no_legacy_stamp_file_exists()
    {
        // Absence is the steady state after the first upgraded run — must never throw and
        // must not conjure the legacy file back into existence.
        PolicyBridge.SaveRestartStamps(new Core.RestartStamps(wedgeAt: null, reevalAt: null, upgradeAt: null), paused: false, stateDir);

        Assert.False(File.Exists(LegacyPath));
    }
}

/// RFC 0002-environment-levels, "Media provider (first inhibitor)": file-only kill switch, no
/// Settings UI. `SanitizeSensingConfig` rebuilds `Config` field-by-field (never a plain clone),
/// so an omitted field silently resets to the type's default on every load — this pins that
/// `MediaInhibitorEnabled` is copied through explicitly, in both directions, exactly like every
/// other sensing field above.
public class MediaInhibitorEnabledSanitizeTests
{
    [Fact]
    public void MediaInhibitorEnabled_true_passes_through_untouched()
    {
        var sanitized = PolicyBridge.SanitizeSensingConfig(new Config { MediaInhibitorEnabled = true });
        Assert.True(sanitized.MediaInhibitorEnabled);
    }

    [Fact]
    public void MediaInhibitorEnabled_false_passes_through_untouched_not_silently_reset_to_the_default()
    {
        var sanitized = PolicyBridge.SanitizeSensingConfig(new Config { MediaInhibitorEnabled = false });
        Assert.False(sanitized.MediaInhibitorEnabled);
    }
}

/// Pure aggregation/change-detection (RFC 0002-environment-levels, "Inhibitor registry", round
/// 3): extracted out of `LockInhibitorRegistry.Refresh` specifically so these tests can target
/// `Compute` directly with plain `(string Name, bool Active)` tuples — no `LockInhibitor`/
/// `Func&lt;bool&gt;` fakes needed.
public class LockInhibitorAggregationTests
{
    [Fact]
    public void No_providers_is_inactive_with_no_names_and_no_transition_from_an_inactive_baseline()
    {
        var (agg, changed) = LockInhibitorAggregation.Compute([], previousActive: false);

        Assert.False(agg.Active);
        Assert.Empty(agg.ActiveNames);
        Assert.False(changed);
    }

    [Fact]
    public void One_active_provider_among_inactive_ones_is_active_with_only_its_name()
    {
        var (agg, changed) = LockInhibitorAggregation.Compute(
            [("quiet-hours", false), ("media-playing", true)], previousActive: false);

        Assert.True(agg.Active);
        Assert.Equal(["media-playing"], agg.ActiveNames);
        Assert.True(changed);
    }

    [Fact]
    public void Multiple_active_providers_lists_every_active_name_in_provider_order()
    {
        var (agg, changed) = LockInhibitorAggregation.Compute(
            [("media-playing", true), ("quiet-hours", false), ("presentation-mode", true)],
            previousActive: true);

        Assert.True(agg.Active);
        Assert.Equal(["media-playing", "presentation-mode"], agg.ActiveNames);
        Assert.False(changed); // was already active, still active -> no transition
    }

    [Fact]
    public void Transition_from_inactive_to_active_reports_changed()
    {
        var (agg, changed) = LockInhibitorAggregation.Compute([("media-playing", true)], previousActive: false);

        Assert.True(agg.Active);
        Assert.True(changed);
    }

    [Fact]
    public void Transition_from_active_to_inactive_reports_changed()
    {
        var (agg, changed) = LockInhibitorAggregation.Compute([("media-playing", false)], previousActive: true);

        Assert.False(agg.Active);
        Assert.True(changed);
    }

    [Fact]
    public void No_transition_when_the_aggregate_active_state_is_unchanged_either_way()
    {
        var stillInactive = LockInhibitorAggregation.Compute([("media-playing", false)], previousActive: false);
        var stillActive = LockInhibitorAggregation.Compute([("media-playing", true)], previousActive: true);

        Assert.False(stillInactive.Changed);
        Assert.False(stillActive.Changed);
    }
}

/// `LockInhibitor.Refresh`'s fail-open exception contract (RFC 0002-environment-levels,
/// "Inhibitor registry"): a throwing query is caught, reported inactive, and never propagates —
/// "assume active forever" would be the silent-cannot-lock trap in new clothes, and an escaped
/// exception would take down the entire watchdog tick handler on the UI thread.
public class LockInhibitorFailOpenTests
{
    [Fact]
    public void A_throwing_query_reports_inactive_and_does_not_propagate()
    {
        var inhibitor = new LockInhibitor("flaky", () => throw new InvalidOperationException("zombie session"));

        var ex = Record.Exception(inhibitor.Refresh);

        Assert.Null(ex);
        Assert.False(inhibitor.Active);
    }

    [Fact]
    public void A_succeeding_query_reports_the_queried_value_in_either_direction()
    {
        var active = new LockInhibitor("media-playing", () => true);
        active.Refresh();
        Assert.True(active.Active);

        var inactive = new LockInhibitor("media-playing", () => false);
        inactive.Refresh();
        Assert.False(inactive.Active);
    }

    [Fact]
    public void A_query_that_recovers_after_a_prior_throw_reports_active_again()
    {
        // Fail-open must not latch: a transient throw must not permanently pin Active=false.
        bool shouldThrow = true;
        var inhibitor = new LockInhibitor("flaky", () =>
        {
            if (shouldThrow) throw new InvalidOperationException("zombie session");
            return true;
        });

        inhibitor.Refresh();
        Assert.False(inhibitor.Active);

        shouldThrow = false;
        inhibitor.Refresh();
        Assert.True(inhibitor.Active);
    }
}

/// `LockInhibitorRegistry.Refresh` (RFC 0002-environment-levels, "Inhibitor registry"): fans out
/// `LockInhibitor.Refresh` over every provider, then delegates to the already-covered
/// `LockInhibitorAggregation.Compute` for the aggregate/changed verdict. These tests exercise the
/// fan-out and the cached-snapshot field writes the pure `Compute` tests above cannot reach.
public class LockInhibitorRegistryTests
{
    [Fact]
    public void Refresh_fans_out_to_every_provider_and_reports_changed_on_the_activating_tick()
    {
        var registry = new LockInhibitorRegistry([
            new LockInhibitor("media-playing", () => true),
            new LockInhibitor("quiet-hours", () => false),
        ]);

        bool changed = registry.Refresh();

        Assert.True(changed);
        Assert.True(registry.Active);
        Assert.Equal(["media-playing"], registry.ActiveNames);
    }

    [Fact]
    public void A_subsequent_refresh_with_no_state_change_reports_not_changed()
    {
        var registry = new LockInhibitorRegistry([new LockInhibitor("media-playing", () => true)]);

        Assert.True(registry.Refresh());
        Assert.False(registry.Refresh());
    }

    [Fact]
    public void A_throwing_provider_fails_open_without_hiding_other_active_providers()
    {
        var registry = new LockInhibitorRegistry([
            new LockInhibitor("flaky", () => throw new InvalidOperationException("zombie session")),
            new LockInhibitor("media-playing", () => true),
        ]);

        registry.Refresh();

        Assert.True(registry.Active);
        Assert.Equal(["media-playing"], registry.ActiveNames);
    }

    [Fact]
    public void Clearing_the_only_active_provider_reports_changed_and_empties_ActiveNames()
    {
        bool playing = true;
        var registry = new LockInhibitorRegistry([new LockInhibitor("media-playing", () => playing)]);
        Assert.True(registry.Refresh());

        playing = false;
        bool changed = registry.Refresh();

        Assert.True(changed);
        Assert.False(registry.Active);
        Assert.Empty(registry.ActiveNames);
    }
}

/// Shell-side inhibitor annotation (RFC 0002-environment-levels, "Status"): appended whenever the
/// registry snapshot is active, regardless of which `Status` row is showing — both halves (the
/// bool and the names) come from the registry, never from `Policy.lockInhibited`.
public class AppendInhibitionAnnotationTests
{
    [Fact]
    public void Inactive_registry_leaves_the_status_text_unchanged()
    {
        Assert.Equal("Watching", PolicyBridge.AppendInhibitionAnnotation("Watching", inhibited: false, activeNames: []));
    }

    [Fact]
    public void Active_registry_with_one_name_matches_the_RFCs_pinned_example_exactly()
    {
        Assert.Equal(
            "Watching · lock inhibited (media-playing)",
            PolicyBridge.AppendInhibitionAnnotation("Watching", inhibited: true, activeNames: ["media-playing"]));
    }

    [Fact]
    public void Active_registry_with_multiple_names_joins_them_with_a_comma_and_space()
    {
        Assert.Equal(
            "Watching · lock inhibited (media-playing, presentation-mode)",
            PolicyBridge.AppendInhibitionAnnotation(
                "Watching", inhibited: true, activeNames: ["media-playing", "presentation-mode"]));
    }

    [Fact]
    public void The_annotation_composes_with_every_Status_row()
    {
        // Extends StatusTextExhaustivenessTests' reach (RFC: the annotation shows "regardless of
        // which row is showing" — NoSignal and inhibition must be visible simultaneously, never
        // one hiding the other).
        var allStatusValues = typeof(Core.Status)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(Core.Status))
            .Select(p => (Core.Status)p.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(allStatusValues);
        foreach (var status in allStatusValues)
        {
            string baseText = PolicyBridge.StatusText(status, lastObservationDark: false);
            string annotated = PolicyBridge.AppendInhibitionAnnotation(baseText, inhibited: true, activeNames: ["media-playing"]);

            Assert.StartsWith(baseText, annotated);
            Assert.EndsWith(" · lock inhibited (media-playing)", annotated);

            // Inactive must never annotate, for every row alike.
            Assert.Equal(baseText, PolicyBridge.AppendInhibitionAnnotation(baseText, inhibited: false, activeNames: []));
        }
    }
}

/// Stage-4 follow-up ("Status annotation when config makes locking effectively unreachable"):
/// same orthogonal-annotation design as AppendInhibitionAnnotationTests — the cue composes
/// with every Status row and with the inhibition annotation, never hides behind either.
public class AppendConfigFallbackAnnotationTests
{
    [Fact]
    public void An_accepted_config_leaves_the_status_text_unchanged()
    {
        Assert.Equal("Watching", PolicyBridge.AppendConfigFallbackAnnotation("Watching", policyDefaultsInUse: false));
    }

    [Fact]
    public void A_rejected_config_appends_the_defaults_in_use_cue()
    {
        Assert.Equal(
            "Watching · config out of range — defaults in use",
            PolicyBridge.AppendConfigFallbackAnnotation("Watching", policyDefaultsInUse: true));
    }

    [Fact]
    public void The_annotation_composes_after_the_inhibition_annotation_matching_Renders_order()
    {
        // Render's pinned composition order: status row, then inhibition (tick-fresh state),
        // then config fallback (load-time state). Both cues must be visible simultaneously.
        string text = PolicyBridge.AppendConfigFallbackAnnotation(
            PolicyBridge.AppendInhibitionAnnotation("Watching", inhibited: true, activeNames: ["media-playing"]),
            policyDefaultsInUse: true);

        Assert.Equal("Watching · lock inhibited (media-playing) · config out of range — defaults in use", text);
    }

    [Fact]
    public void The_annotation_composes_with_every_Status_row()
    {
        var allStatusValues = typeof(Core.Status)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(Core.Status))
            .Select(p => (Core.Status)p.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(allStatusValues);
        foreach (var status in allStatusValues)
        {
            string baseText = PolicyBridge.StatusText(status, lastObservationDark: false);
            string annotated = PolicyBridge.AppendConfigFallbackAnnotation(baseText, policyDefaultsInUse: true);

            Assert.StartsWith(baseText, annotated);
            Assert.EndsWith(" · config out of range — defaults in use", annotated);
            Assert.Equal(baseText, PolicyBridge.AppendConfigFallbackAnnotation(baseText, policyDefaultsInUse: false));
        }
    }
}

/// RFC 0002-environment-levels, "Slices" item 5: "BuildStepInputs sourcing (registry active →
/// StepInputs.LockInhibited true)". `WatcherContext` itself can't be constructed under xunit
/// (WinForms), but `BuildStepInputs` — the one `StepInputs`-construction site every call site
/// routes through — and `LockInhibitorRegistry` are both directly testable; composing them here
/// proves the same wiring the instance `BuildStepInputs()` overload performs (a one-line forward
/// of `inhibitorRegistry.Active` via a named argument) without needing a live shell instance.
public class BuildStepInputsInhibitorSourcingTests
{
    [Fact]
    public void An_active_registry_snapshot_sources_StepInputs_LockInhibited_true()
    {
        var registry = new LockInhibitorRegistry([new LockInhibitor("media-playing", () => true)]);
        registry.Refresh();
        Assert.True(registry.Active);

        var inputs = WatcherContext.BuildStepInputs(
            sessionLocked: false, paused: false, lockInhibited: registry.Active, inputIdleMs: 0L);

        Assert.True(inputs.LockInhibited);
    }

    [Fact]
    public void An_inactive_registry_snapshot_sources_StepInputs_LockInhibited_false()
    {
        var registry = new LockInhibitorRegistry([new LockInhibitor("media-playing", () => false)]);
        registry.Refresh();
        Assert.False(registry.Active);

        var inputs = WatcherContext.BuildStepInputs(
            sessionLocked: false, paused: false, lockInhibited: registry.Active, inputIdleMs: 0L);

        Assert.False(inputs.LockInhibited);
    }
}

/// RFC 0002-environment-levels, "Advance and timers": "within one watchdog tick, WTS
/// reconciliation runs first, then inhibitor Refresh()/aggregation ..., and only then does
/// SamplingWatchdogStep read Policy.status." `WatcherContext`'s real Tick handler can't be driven
/// under xunit (WinForms Timer, live P/Invoke, live WinRT) — `WatchdogTickStep` is the pure
/// orchestrator seam this pins instead, mirroring `AssembleStartupInputsTests`' precedent.
public class WatchdogTickStepTests
{
    [Fact]
    public void Reconcile_then_refresh_inhibitors_then_sampling_watchdog_run_in_that_exact_order()
    {
        var callOrder = new List<string>();

        WatcherContext.WatchdogTickStep(
            reconcile: () => callOrder.Add("reconcile"),
            refreshInhibitors: () => callOrder.Add("refresh"),
            runSamplingWatchdogStep: () => callOrder.Add("watchdog"));

        Assert.Equal(new[] { "reconcile", "refresh", "watchdog" }, callOrder);
    }

    [Fact]
    public void Each_step_runs_exactly_once_even_if_earlier_steps_mutate_shared_state()
    {
        int calls = 0;
        WatcherContext.WatchdogTickStep(
            reconcile: () => calls++,
            refreshInhibitors: () => calls++,
            runSamplingWatchdogStep: () => calls++);

        Assert.Equal(3, calls);
    }
}
