using Core = PresenceLock.Core;
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

public class LoadRestartStampsTests
{
    [Fact]
    public void Missing_file_yields_empty_stamps()
    {
        var stamps = PolicyBridge.LoadRestartStamps();
        // Nothing writes the new stamps file yet in shadow mode (8b/8c wire persistence) —
        // this call must never throw and must default to empty when the file is absent.
        Assert.False(stamps.WedgeAt.HasValue);
        Assert.False(stamps.ReevalAt.HasValue);
    }
}
