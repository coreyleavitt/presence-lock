using Core = PresenceLock.Core;
using Windows.Graphics.Imaging;
using Xunit;

namespace PresenceLock.Tests;

/// Dedicated shell-side xunit tests for PolicyBridge (rfc-core-brain.md, slice 8a /
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
    }

    [Theory]
    [InlineData(0.0, 10.0, 10.0, 10000L, 20000L, 600000L, 3, 600000L)]   // AwayThresholdSeconds <= 0
    [InlineData(5.0, 10.0, 10.0, 0L, 20000L, 600000L, 3, 600000L)]       // NoSignalReportAfterMs <= 0
    [InlineData(5.0, 10.0, 10.0, 10000L, 20000L, 600000L, 0, 600000L)]   // RecoveryFailureThreshold < 1
    [InlineData(5.0, 10.0, 10.0, 20000L, 10000L, 600000L, 3, 600000L)]   // ReevaluateAfterMs < NoSignalReportAfterMs
    public void Any_single_invalid_field_falls_back_to_the_full_default_set_never_a_partial_mix(
        double awaySeconds, double idleSeconds, double graceSeconds,
        long noSignalMs, long reevaluateAfterMs, long reevaluateCooldownMs,
        int recoveryThreshold, long recoveryCooldownMs)
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
    }

    [Fact]
    public void Sensing_only_fields_are_not_part_of_PolicyConfig_and_do_not_affect_the_fallback()
    {
        // CameraNameContains / DarkFrameMeanThreshold / SampleIntervalMs stay shell-only
        // (rfc-core-brain.md) — a fat-fingered policy field must not touch them, and this
        // mapping function must never read them.
        var cfg = new Config { CameraNameContains = "Logitech", DarkFrameMeanThreshold = 42.0, SampleIntervalMs = 250 };
        var policy = PolicyBridge.BuildPolicyConfig(cfg);
        var defaults = PolicyBridge.BuildPolicyConfig(new Config());

        Assert.Equal(defaults.AwayThresholdMs, policy.AwayThresholdMs);
        Assert.Equal(defaults.RecoveryCooldownMs, policy.RecoveryCooldownMs);
    }
}

public class ClassifySampleTests
{
    [Fact]
    public void Constructs_a_Sample_event_carrying_the_classified_observation_and_idle_time()
    {
        var evt = PolicyBridge.ClassifySample(haveFrame: true, dark: false, present: true, inputIdleMs: 1234);
        var sample = Assert.IsType<Core.Event.Sample>(evt);
        Assert.Equal(Core.Observation.FaceSeen, sample.Item1);
        Assert.Equal(1234, sample.inputIdleMs);
    }
}

/// Shell-side helper feeding PresenceLock.Core's PresenceFilter (rfc-core-brain.handoff.md,
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

/// Shell-side status→text mapping (rfc-core-brain.md, slice 8b deliverable). Pure and tested
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

/// The complete, core-owned retry/kick gate (rfc-core-brain.md R2-4/R2-15): true exactly for
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

public class LoadRestartStampsTests
{
    [Fact]
    public void Missing_file_yields_empty_stamps()
    {
        var stamps = PolicyBridge.LoadRestartStamps();
        // This call must never throw and must default to empty when the file is absent.
        Assert.False(stamps.WedgeAt.HasValue);
        Assert.False(stamps.ReevalAt.HasValue);
    }
}

/// Stamps-file writer round-trip (rfc-core-brain.md, "Restart stamps" / R1-29) and the
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
            var stamps = new Core.RestartStamps(wedgeAt: 1_700_000_000_000, reevalAt: 1_700_000_500_000);

            PolicyBridge.SaveRestartStamps(stamps, paused: false);
            var loaded = PolicyBridge.LoadRestartStamps();

            Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
            Assert.Equal(1_700_000_500_000, loaded.ReevalAt);
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }

    [Fact]
    public void Saved_stamps_with_only_one_reason_set_round_trip_the_other_as_absent()
    {
        if (File.Exists(StampsPath)) File.Delete(StampsPath);
        try
        {
            var stamps = new Core.RestartStamps(wedgeAt: 1_700_000_000_000, reevalAt: null);

            PolicyBridge.SaveRestartStamps(stamps, paused: false);
            var loaded = PolicyBridge.LoadRestartStamps();

            Assert.Equal(1_700_000_000_000, loaded.WedgeAt);
            Assert.False(loaded.ReevalAt.HasValue);
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
            PolicyBridge.SaveRestartStamps(new Core.RestartStamps(wedgeAt: null, reevalAt: null), paused: false);
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
            var stamps = new Core.RestartStamps(wedgeAt: 42, reevalAt: 43);
            PolicyBridge.SaveRestartStamps(stamps, paused: true);

            Assert.True(PolicyBridge.ConsumePersistedPausedFlag());
            // Consumed-and-cleared: a second call sees the flag already cleared.
            Assert.False(PolicyBridge.ConsumePersistedPausedFlag());

            // Stamps themselves survive the clear untouched.
            var loaded = PolicyBridge.LoadRestartStamps();
            Assert.Equal(42L, loaded.WedgeAt);
            Assert.Equal(43L, loaded.ReevalAt);
        }
        finally
        {
            if (File.Exists(StampsPath)) File.Delete(StampsPath);
        }
    }
}
