module PresenceLock.Core.Tests.Tests

open Xunit
open FsCheck
open FsCheck.Xunit
open PresenceLock.Core
open PresenceLock.Core.Tests.Generators

let private noStamps : RestartStamps =
    { WedgeAt = System.Nullable(); ReevalAt = System.Nullable(); UpgradeAt = System.Nullable() }

/// RFC 0002-environment-levels, slice 1 (MIGRATE): the mechanical-majority append point --
/// unsuppressed, uninhibited, idle=0 `StepInputs`, for call sites that never touch
/// session/pause/idle. Mirrors LevelsTests.fs's `unsuppressedInputs` convention, duplicated
/// locally per that file's own note (each module's helpers are private to it).
let private defaultInputs : StepInputs =
    { SessionLocked = false; Paused = false; LockInhibited = false; InputIdleMs = 0L }

/// General `StepContext` constructor -- every other helper below is a thin specialization of
/// this one, so the (Now, NowWall, Inputs) field mapping is written down in exactly one place.
let private ctxWith (now: MonotonicMs) (nowWall: WallClockMs) (inputs: StepInputs) : StepContext =
    { Now = now; NowWall = nowWall; Inputs = inputs }

/// Builds a `StepContext` from the old two-clock (now, nowWall) parameter pair plus
/// `defaultInputs` -- what every call site that never touched session/pause under the old
/// contract becomes under the levels one (RFC slice-1 test-migration paragraph).
let private levelsOf (now: MonotonicMs) (nowWall: WallClockMs) : StepContext =
    ctxWith now nowWall defaultInputs

/// Same as `levelsOf`, but routes `idleMs` into `StepInputs.InputIdleMs` -- what `stepLevels`
/// actually reads for the idle gate now that `InputIdleMs` has moved out of the `Sample`
/// payload (RFC "Core contract changes"). Applied uniformly at every `Event.Sample` call site
/// so the payload literal and the levels input never drift apart; the contract phase's payload
/// deletion then touches only this one helper.
let private levelsOfIdle (now: MonotonicMs) (nowWall: WallClockMs) (idleMs: int64) : StepContext =
    ctxWith now nowWall { defaultInputs with InputIdleMs = idleMs }

/// Generic idle-sync helper for the FsCheck ticks/event-driven properties, where the event (and
/// its `Sample` idle payload, if any) is data rather than a literal at the call site: extracts
/// `idleMs` from a `Sample` event so the same value reaches both the event payload (several
/// properties still assert against it directly) and `StepInputs` (what `stepLevels` reads) --
/// the "one small sample-building helper" the RFC calls for.
let private inputsForEvent (event: Event) : StepInputs =
    match event with
    | Event.Sample(_, idleMs) -> { defaultInputs with InputIdleMs = idleMs }
    | _ -> defaultInputs

let private ctxForEvent (now: MonotonicMs) (nowWall: WallClockMs) (event: Event) : StepContext =
    ctxWith now nowWall (inputsForEvent event)

/// Suppressed-via-pause inputs, for the hand-rewritten "restart preserves the pause" tests
/// (old `Event.Paused` -> levels `StepInputs.Paused = true`, per the RFC's semantics-
/// translation guide).
let private pausedInputs : StepInputs = { defaultInputs with Paused = true }

[<Fact>]
let ``baseline status immediately after start is AcquiringCamera`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    Assert.Equal(Status.AcquiringCamera, Policy.status state)

let private someConfig : PolicyConfig =
    { AwayThresholdMs = 5000L
      InputIdleRequiredMs = 10000L
      GraceMs = 10000L
      NoSignalReportAfterMs = 10000L
      ReevaluateAfterMs = 20000L
      ReevaluateCooldownMs = 600000L
      RecoveryFailureThreshold = 3
      RecoveryCooldownMs = 600000L
      UpgradeCooldownMs = 600000L }

[<Fact>]
let ``a FaceSeen sample arms the state`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let result =
        Policy.stepLevels (someConfig, state, levelsOfIdle (MonotonicMs 0L) (WallClockMs 0L) 0L, Event.Sample(Observation.FaceSeen, 0L))
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 0L)
    Assert.True(snap.Armed)

[<Fact>]
let ``look-away locks once armed, past grace, idle-satisfied, and the away threshold has elapsed`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    // Arm past grace (GraceMs = 10000) so the arming sample itself isn't in-grace.
    let armedResult =
        Policy.stepLevels (
            someConfig,
            state,
            levelsOfIdle (MonotonicMs 10001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // AwayThresholdMs = 5000, elapsed from the away-baseline the FaceSeen sample just set.
    let result =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs 15001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.Lock, result.Action)

[<Fact>]
let ``a NoFace sample does not lock while inputIdleMs is below InputIdleRequiredMs, even past grace and away threshold`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            state,
            levelsOfIdle (MonotonicMs 10001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // Away threshold and grace are both satisfied by this timestamp; only idle is not
    // (InputIdleRequiredMs = 10000).
    let result =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs 15001L) (WallClockMs 0L) 0L,
            Event.Sample(Observation.NoFace, 0L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``a NoFace sample does not lock before GraceMs elapses since the baseline event, even though armed/idle/away are satisfied`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    // Arms almost immediately after start — grace is measured from `start` (the baseline
    // event), not from the arming FaceSeen sample, so grace is still running.
    let armedResult =
        Policy.stepLevels (
            someConfig,
            state,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // AwayThresholdMs (5000) has elapsed since the away-baseline the FaceSeen sample set, and
    // idle is satisfied, but only 5001 ms have elapsed since start — GraceMs (10000) has not.
    let result =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs 5001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``InitSucceeded is a baseline event: it disarms, re-baselines grace/away, and transitions status to Watching`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    // Arm before InitSucceeded to prove InitSucceeded disarms — a stray FaceSeen before
    // acquisition truly succeeds must not carry an armed flag across the baseline.
    let armedResult =
        Policy.stepLevels (
            someConfig,
            state,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    let result =
        Policy.stepLevels (someConfig, armedResult.State, levelsOf (MonotonicMs 2L) (WallClockMs 0L), Event.InitSucceeded)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 2L)
    Assert.False(snap.Armed)
    Assert.True(snap.InGrace)
    Assert.Equal(0L, snap.AwayForMs)
    Assert.Equal(Status.Watching, Policy.status result.State)

[<Fact>]
let ``slow InitSucceeded does not consume grace measured from start's now`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    // Camera acquisition takes longer than GraceMs (10000) to succeed.
    let initResult =
        Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 15000L) (WallClockMs 0L), Event.InitSucceeded)
    // Arms immediately after success.
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 15001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // AwayThresholdMs (5000) elapses since the arming sample, but only ~5001 ms have passed
    // since InitSucceeded's baseline (15000) — grace (10000) must still be running, measured
    // from InitSucceeded, not from `start`'s stale t0 (which would already show as expired).
    let result =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs 20001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``never-seen must not lock, even when away/idle/grace are all satisfied`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    // No FaceSeen has ever occurred, so Armed stays false no matter how long the away/grace
    // clocks run or how idle the input is.
    let result =
        Policy.stepLevels (
            someConfig,
            state,
            levelsOfIdle (MonotonicMs 20001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

// --- Slice 4: signal accounting (NoFrame/DarkFrame) -------------------------------------

/// Shared body for the fail-open away-baseline-reset scenario, run against both bad-signal
/// observations (0001-core-brain.md: "NoFrame and DarkFrame observations reset the away-
/// baseline identically to FaceSeen ... but, unlike FaceSeen, do not set armed").
let private assertBadSignalResetsAwayClockWithoutArming (badObservation: Observation) =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            state,
            levelsOfIdle (MonotonicMs 10001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // Away threshold (5000) would already have elapsed by 15001 measured from the FaceSeen
    // baseline at 10001 -- but a bad-signal blip at 13000 must reset the away clock (fail-open),
    // not merely pause it.
    let blipResult =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs 13000L) (WallClockMs 0L) 20000L,
            Event.Sample(badObservation, 20000L)
        )
    // Only ~2001ms have elapsed since the blip reset -- below AwayThresholdMs (5000) -- so a
    // NoFace sample here must not lock.
    let result =
        Policy.stepLevels (
            someConfig,
            blipResult.State,
            levelsOfIdle (MonotonicMs 15001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``a NoFrame blip resets, not merely pauses, the away clock`` () =
    assertBadSignalResetsAwayClockWithoutArming Observation.NoFrame

[<Fact>]
let ``a DarkFrame blip resets, not merely pauses, the away clock`` () =
    assertBadSignalResetsAwayClockWithoutArming Observation.DarkFrame

[<Fact>]
let ``status becomes NoSignal after continuous bad signal reaches NoSignalReportAfterMs`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    // First bad sample starts the bad-signal clock; not yet reported.
    let firstBad =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1000L) (WallClockMs 0L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Status.Watching, Policy.status firstBad.State)
    // Continuous bad signal (DarkFrame following NoFrame -- both count toward the same run)
    // reaches NoSignalReportAfterMs (10000) measured from the first bad sample at 1000.
    let laterBad =
        Policy.stepLevels (
            someConfig,
            firstBad.State,
            levelsOfIdle (MonotonicMs(1000L + someConfig.NoSignalReportAfterMs)) (WallClockMs 0L) 0L,
            Event.Sample(Observation.DarkFrame, 0L)
        )
    Assert.Equal(Status.NoSignal, Policy.status laterBad.State)

[<Fact>]
let ``continuous bad signal reaching ReevaluateAfterMs requests Action.Restart CameraReevaluation when no cooldown applies`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 1000L), Event.InitSucceeded)
    let firstBad =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1000L) (WallClockMs 1000L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    let result =
        Policy.stepLevels (
            someConfig,
            firstBad.State,
            levelsOfIdle (MonotonicMs(1000L + someConfig.ReevaluateAfterMs)) (WallClockMs 1000L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Action.Restart RestartReason.CameraReevaluation, result.Action)

[<Fact>]
let ``a cooldown-suppressed CameraReevaluation restart stays saturated and fires immediately once the cooldown clears`` () =
    // A CameraReevaluation restart happened recently (wall-clock 500) before this process
    // even started -- randomized initial RestartStamps continuity (R2-19).
    let stampsRecentReeval: RestartStamps =
        { WedgeAt = System.Nullable(); ReevalAt = System.Nullable(500L); UpgradeAt = System.Nullable() }
    let state = Policy.startLevels (MonotonicMs 0L, stampsRecentReeval, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 500L), Event.InitSucceeded)
    let firstBad =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1000L) (WallClockMs 1500L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    let thresholdCrossedAt = 1000L + someConfig.ReevaluateAfterMs
    // Threshold crossed, but only 1000ms of wall-clock have elapsed since ReevalAt (500) --
    // well under ReevaluateCooldownMs (600000) -- so the restart is suppressed.
    let suppressed =
        Policy.stepLevels (
            someConfig,
            firstBad.State,
            levelsOfIdle (MonotonicMs thresholdCrossedAt) (WallClockMs 1500L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Action.NoAction, suppressed.Action)
    // Saturated: the bad-signal run does not reset just because the attempt was suppressed --
    // it stays well past the threshold on the very next sample too (no re-accumulation needed).
    let stillSuppressed =
        Policy.stepLevels (
            someConfig,
            suppressed.State,
            levelsOfIdle (MonotonicMs(thresholdCrossedAt + 1000L)) (WallClockMs 2500L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Action.NoAction, stillSuppressed.Action)
    // Cooldown clears (600000ms wall-clock since ReevalAt = 500) -- fires immediately, not up
    // to ReevaluateAfterMs later.
    let cooldownClearedWall = 500L + someConfig.ReevaluateCooldownMs
    let fired =
        Policy.stepLevels (
            someConfig,
            stillSuppressed.State,
            levelsOfIdle (MonotonicMs(thresholdCrossedAt + 2000L)) (WallClockMs cooldownClearedWall) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Action.Restart RestartReason.CameraReevaluation, fired.Action)

[<Fact>]
let ``a baseline event resets the signal-health clock, preventing an immediate NoSignal on the next bad sample`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let badRun =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1000L) (WallClockMs 0L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    // A second InitSucceeded (standing in for the camera re-acquiring) intervenes just before
    // the original bad run would have reached NoSignalReportAfterMs (10000, i.e. at t=11000).
    let reinit =
        Policy.stepLevels (
            someConfig,
            badRun.State,
            levelsOf (MonotonicMs(1000L + someConfig.NoSignalReportAfterMs - 1L)) (WallClockMs 0L),
            Event.InitSucceeded
        )
    // Without the reset, this next bad sample (t=11000, exactly the original run's threshold
    // crossing) would already show NoSignal; the baseline event must have zeroed the clock.
    let nextBad =
        Policy.stepLevels (
            someConfig,
            reinit.State,
            levelsOfIdle (MonotonicMs(1000L + someConfig.NoSignalReportAfterMs)) (WallClockMs 0L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Status.Watching, Policy.status nextBad.State)

[<Fact>]
let ``dim-light while typing must not lock (regression: fail-closed dim-light lock incident)`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 0L,
            Event.Sample(Observation.FaceSeen, 0L)
        )
    // A long run of DarkFrame samples while the user is actively typing (idle stays 0) --
    // must never lock, regardless of how long the dark run continues.
    let mutable st = armedResult.State
    let mutable everLocked = false
    for i in 1 .. 50 do
        let t = 1L + int64 i * 1000L
        let result =
            Policy.stepLevels (someConfig, st, levelsOfIdle (MonotonicMs t) (WallClockMs 0L) 0L, Event.Sample(Observation.DarkFrame, 0L))
        st <- result.State
        if result.Action = Action.Lock then
            everLocked <- true
    Assert.False(everLocked)

[<Fact>]
let ``CaptureFailed before any InitSucceeded is a no-op (not a shell-reachable state, but step must stay total)`` () =
    // The shell only ever raises CaptureFailed for a live capture (MediaCapture.Failed), so
    // HasSucceededOnce is always true along the real call path -- this exercises the
    // otherwise-undefined pre-success case defensively rather than silently.
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let result = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.CaptureFailed)
    Assert.Equal(Action.NoAction, result.Action)
    Assert.Equal(Status.AcquiringCamera, Policy.status result.State)

// --- Slice 5: session/pause re-baselining ------------------------------------------------
//
// RFC 0002-environment-levels semantics-translation guide: old `Event.SessionLocked`/
// `SessionUnlocked`/`Paused`/`Resumed` become `Reconcile` steps carrying the corresponding
// `StepInputs.SessionLocked`/`.Paused` flip. The suppressed -> unsuppressed edge fires the
// baseline reset (same `applyBaselineReset` semantics as the old SessionUnlocked/Resumed
// arms); the unsuppressed -> suppressed edge does nothing.

[<Fact>]
let ``SessionLocked transitions status to SessionLocked and suppresses further Sample-driven locking`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    let sessionLockedInputs = { defaultInputs with SessionLocked = true }
    let lockedResult =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            ctxWith (MonotonicMs 10002L) (WallClockMs 0L) sessionLockedInputs,
            Event.Reconcile
        )
    Assert.Equal(Status.SessionLocked, Policy.status lockedResult.State)
    // A sample that would otherwise satisfy every lock gate must still not re-emit Action.Lock
    // while session-locked -- see the wrapper's while-suppressed Sample no-op rule, tested more
    // directly below.
    let result =
        Policy.stepLevels (
            someConfig,
            lockedResult.State,
            ctxWith (MonotonicMs 20003L) (WallClockMs 0L) { sessionLockedInputs with InputIdleMs = 20000L },
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``SessionUnlocked re-baselines grace/away/signal-health and disarms, resuming sampling`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    let sessionLockedInputs = { defaultInputs with SessionLocked = true }
    let lockedResult =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            ctxWith (MonotonicMs 10002L) (WallClockMs 0L) sessionLockedInputs,
            Event.Reconcile
        )
    // Old Event.SessionUnlocked -> levels Reconcile with StepInputs.SessionLocked = false: the
    // suppressed -> unsuppressed edge fires the baseline reset here, since not paused.
    let unlockedResult =
        Policy.stepLevels (someConfig, lockedResult.State, levelsOf (MonotonicMs 20003L) (WallClockMs 0L), Event.Reconcile)
    Assert.Equal(Status.Watching, Policy.status unlockedResult.State)
    let snap = Policy.snapshot (someConfig, unlockedResult.State, MonotonicMs 20003L)
    Assert.False(snap.Armed)
    Assert.True(snap.InGrace)
    Assert.Equal(0L, snap.AwayForMs)
    Assert.Equal(0L, snap.NoSignalForMs)
    // Grace after unlock: a NoFace sample immediately after (before GraceMs elapses since the
    // unlock baseline) must not lock, even though the pre-lock session had long satisfied every
    // gate.
    let result =
        Policy.stepLevels (
            someConfig,
            unlockedResult.State,
            levelsOfIdle (MonotonicMs(20003L + someConfig.GraceMs - 1L)) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``a Sample event is a complete no-op while SessionLocked: no arming, no re-baseline, no Lock`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let sessionLockedInputs = { defaultInputs with SessionLocked = true }
    let lockedResult =
        Policy.stepLevels (someConfig, initResult.State, ctxWith (MonotonicMs 1L) (WallClockMs 0L) sessionLockedInputs, Event.Reconcile)
    let beforeSnap = Policy.snapshot (someConfig, lockedResult.State, MonotonicMs 50000L)
    // A FaceSeen while locked must not arm -- if it did, an immediate unlock would incorrectly
    // start out armed.
    let sampledResult =
        Policy.stepLevels (
            someConfig,
            lockedResult.State,
            ctxWith (MonotonicMs 50000L) (WallClockMs 0L) { sessionLockedInputs with InputIdleMs = 20000L },
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    let afterSnap = Policy.snapshot (someConfig, sampledResult.State, MonotonicMs 50000L)
    Assert.Equal(Action.NoAction, sampledResult.Action)
    Assert.Equal(Status.SessionLocked, Policy.status sampledResult.State)
    Assert.Equal(beforeSnap.Armed, afterSnap.Armed)
    Assert.Equal(beforeSnap.AwayForMs, afterSnap.AwayForMs)
    Assert.Equal(beforeSnap.NoSignalForMs, afterSnap.NoSignalForMs)

[<Fact>]
let ``Paused transitions status to Paused, stops sampling, but does not disarm or touch the away baseline`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    let beforeSnap = Policy.snapshot (someConfig, armedResult.State, MonotonicMs 2L)
    // Old Event.Paused -> levels Reconcile with StepInputs.Paused = true.
    let pausedResult =
        Policy.stepLevels (someConfig, armedResult.State, ctxWith (MonotonicMs 2L) (WallClockMs 0L) pausedInputs, Event.Reconcile)
    let afterSnap = Policy.snapshot (someConfig, pausedResult.State, MonotonicMs 2L)
    Assert.Equal(Status.Paused, Policy.status pausedResult.State)
    // Paused is not a baseline event -- Armed/AwayBaselineAt must survive it unchanged.
    Assert.Equal(beforeSnap.Armed, afterSnap.Armed)
    Assert.Equal(beforeSnap.AwayForMs, afterSnap.AwayForMs)
    // A Sample while Paused must not lock even though away/grace/idle are all already satisfied.
    let result =
        Policy.stepLevels (
            someConfig,
            pausedResult.State,
            ctxWith (MonotonicMs 20003L) (WallClockMs 0L) { pausedInputs with InputIdleMs = 20000L },
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``Resumed re-baselines grace/away/signal-health and disarms, resuming sampling`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    let pausedResult =
        Policy.stepLevels (someConfig, armedResult.State, ctxWith (MonotonicMs 2L) (WallClockMs 0L) pausedInputs, Event.Reconcile)
    // Old Event.Resumed -> levels Reconcile with StepInputs.Paused = false: the suppressed ->
    // unsuppressed edge fires the baseline reset.
    let resumedResult =
        Policy.stepLevels (someConfig, pausedResult.State, levelsOf (MonotonicMs 10003L) (WallClockMs 0L), Event.Reconcile)
    Assert.Equal(Status.Watching, Policy.status resumedResult.State)
    let snap = Policy.snapshot (someConfig, resumedResult.State, MonotonicMs 10003L)
    Assert.False(snap.Armed)
    Assert.True(snap.InGrace)
    Assert.Equal(0L, snap.AwayForMs)
    Assert.Equal(0L, snap.NoSignalForMs)
    let result =
        Policy.stepLevels (
            someConfig,
            resumedResult.State,
            levelsOfIdle (MonotonicMs(10003L + someConfig.GraceMs - 1L)) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

// --- Slice 6: recovery policy (InitFailed / CaptureFailed) -------------------------------

[<Fact>]
let ``InitFailed transitions status to Recovering on the very first pre-success failure, of either classification, without yet requesting a restart`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let result = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitFailed false)
    Assert.Equal(Status.Recovering, Policy.status result.State)
    Assert.Equal(Action.NoAction, result.Action)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 0L)
    Assert.Equal(1, snap.InitFailStreak)

// (The old "pause takes precedence over an unlock-driven resume: SessionUnlocked while Paused
// is a no-op" test (0001-core-brain.md's pinned truth-table row, incident 2026-08-17's root
// cause) is DELETED here, not migrated -- see docs/rfc/0002-environment-levels.migration.md.
// It pinned the wrong behavior on purpose; LevelsTests.fs's incident-fixed-point tracer test
// proves the opposite, correct behavior under the levels contract: unlock-while-paused
// followed by resume reaches Watching, not a permanently suppressed state.)

// (The three old "session-event idempotency" tests -- SessionLocked/Paused/Resumed delivered
// twice while already in that state is a no-op -- are DELETED here, not migrated. They are
// fully superseded by LevelsTests.fs's "identical consecutive StepInputs across a Reconcile
// trigger no edge and no baseline reset" test, which pins the same claim precisely as the RFC
// scopes it for the levels contract. See the migration table.)

[<Fact>]
let ``config changed mid-grace applies immediately, without discarding accumulated baseline state`` () =
    let tighterConfig = { someConfig with GraceMs = 2000L }
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // Under the original config (GraceMs = 10000), t=3000 is still mid-grace and must not lock.
    let stillOriginal =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs 3000L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, stillOriginal.Action)
    // The very next sample, under a live-reloaded tighter config (GraceMs = 2000), sees grace
    // already elapsed (from the same FaceSeen baseline at t=1) and away/idle both satisfied --
    // it must lock on this sample, without needing to discard/replay any accumulated state.
    let underTighterConfig =
        Policy.stepLevels (
            tighterConfig,
            stillOriginal.State,
            levelsOfIdle (MonotonicMs 6001L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.Lock, underTighterConfig.Action)

[<Fact>]
let ``an 8-hour tick gap followed by NoFace (idle satisfied) locks on the very next sample`` () =
    let eightHoursMs = 8L * 60L * 60L * 1000L
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // The machine sleeps for 8 hours; TickCount64 (and so `now`) jumps by the same amount on
    // wake, gap-obliviously -- away/grace/idle are all trivially satisfied by an 8-hour gap.
    let result =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs(1L + eightHoursMs)) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.Lock, result.Action)

[<Fact>]
let ``the same 8-hour tick gap followed by NoFrame does not lock`` () =
    let eightHoursMs = 8L * 60L * 60L * 1000L
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let armedResult =
        Policy.stepLevels (
            someConfig,
            initResult.State,
            levelsOfIdle (MonotonicMs 1L) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // NoFrame never arms and resets the away-baseline (fail-open) -- an 8-hour gap ending in a
    // NoFrame observation must never lock.
    let result =
        Policy.stepLevels (
            someConfig,
            armedResult.State,
            levelsOfIdle (MonotonicMs(1L + eightHoursMs)) (WallClockMs 0L) 20000L,
            Event.Sample(Observation.NoFrame, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``InitFailed requests Action.Restart CameraWedged once RecoveryFailureThreshold consecutive handle-invalid failures are reached and no cooldown applies`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    // RecoveryFailureThreshold = 3 in someConfig; the first two handle-invalid failures stay
    // below threshold.
    let first = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitFailed true)
    Assert.Equal(Action.NoAction, first.Action)
    let second = Policy.stepLevels (someConfig, first.State, levelsOf (MonotonicMs 1L) (WallClockMs 1L), Event.InitFailed true)
    Assert.Equal(Action.NoAction, second.Action)
    // Third consecutive handle-invalid failure crosses the threshold; no stamped WedgeAt exists
    // yet, so cooldownElapsed is vacuously true.
    let third = Policy.stepLevels (someConfig, second.State, levelsOf (MonotonicMs 2L) (WallClockMs 2L), Event.InitFailed true)
    Assert.Equal(Action.Restart RestartReason.CameraWedged, third.Action)
    let snap = Policy.snapshot (someConfig, third.State, MonotonicMs 2L)
    Assert.Equal(System.Nullable(2L), snap.LastWedgeRestartAt)

[<Fact>]
let ``a cooldown-suppressed InitFailed wedge restart stays saturated and fires immediately once the cooldown clears`` () =
    // A CameraWedged restart happened recently (wall-clock 500) before this process even
    // started -- randomized initial RestartStamps continuity (R2-19).
    let stampsRecentWedge: RestartStamps =
        { WedgeAt = System.Nullable(500L); ReevalAt = System.Nullable(); UpgradeAt = System.Nullable() }
    let state = Policy.startLevels (MonotonicMs 0L, stampsRecentWedge, defaultInputs)
    let first = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 600L), Event.InitFailed true)
    let second = Policy.stepLevels (someConfig, first.State, levelsOf (MonotonicMs 1L) (WallClockMs 700L), Event.InitFailed true)
    // Threshold (3) crossed here, but only 300ms of wall-clock have elapsed since WedgeAt (500)
    // -- well under RecoveryCooldownMs (600000) -- so the restart is suppressed.
    let suppressed = Policy.stepLevels (someConfig, second.State, levelsOf (MonotonicMs 2L) (WallClockMs 800L), Event.InitFailed true)
    Assert.Equal(Action.NoAction, suppressed.Action)
    // Saturated: the streak keeps growing but stays suppressed on the very next failure too.
    let stillSuppressed =
        Policy.stepLevels (someConfig, suppressed.State, levelsOf (MonotonicMs 3L) (WallClockMs 900L), Event.InitFailed true)
    Assert.Equal(Action.NoAction, stillSuppressed.Action)
    // Cooldown clears (600000ms wall-clock since WedgeAt = 500) -- fires immediately.
    let cooldownClearedWall = 500L + someConfig.RecoveryCooldownMs
    let fired =
        Policy.stepLevels (
            someConfig,
            stillSuppressed.State,
            levelsOf (MonotonicMs 4L) (WallClockMs cooldownClearedWall),
            Event.InitFailed true
        )
    Assert.Equal(Action.Restart RestartReason.CameraWedged, fired.Action)

[<Fact>]
let ``a long run of handleInvalid:false InitFailed events never requests a restart, even once RecoveryFailureThreshold and RecoveryCooldownMs are both long since satisfied (unplugged/absent camera)`` () =
    // Regression for the unplug/replug analysis (0001-core-brain.md, "Notes: Camera unplug/
    // replug, analyzed"): the pre-success streak gate requires handle-invalid failures, so a
    // camera that is simply absent (never handle-invalid) must retry forever without ever
    // requesting a process restart -- restarting cannot summon a camera that isn't there.
    let mutable state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let mutable everRestarted = false
    for i in 1 .. 50 do
        let t = int64 i * 5000L
        let result = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs t) (WallClockMs t), Event.InitFailed false)
        state <- result.State
        if result.Action <> Action.NoAction then
            everRestarted <- true
    Assert.False(everRestarted)
    Assert.Equal(Status.Recovering, Policy.status state)

[<Fact>]
let ``CaptureFailed after InitSucceeded requests Action.Restart CameraWedged unconditionally on its first occurrence, subject only to cooldown`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    // RecoveryFailureThreshold (3) is never approached -- a single CaptureFailed is enough.
    let result = Policy.stepLevels (someConfig, initResult.State, levelsOf (MonotonicMs 1L) (WallClockMs 1L), Event.CaptureFailed)
    Assert.Equal(Action.Restart RestartReason.CameraWedged, result.Action)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 1L)
    Assert.Equal(System.Nullable(1L), snap.LastWedgeRestartAt)

[<Fact>]
let ``CaptureFailed while Paused still requests Action.Restart, and the restart preserves the pause`` () =
    // Deliberate asymmetry (0001-core-brain.md, R2-10): sample-driven decisions are pause-immune,
    // failure-driven restarts are not -- the camera is kept alive while paused, so its death is
    // a real fact requiring recovery, and R1-30's persisted pause flag exists precisely so such
    // a restart preserves the pause. Old Event.Paused -> levels StepInputs.Paused = true.
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let pausedResult =
        Policy.stepLevels (someConfig, initResult.State, ctxWith (MonotonicMs 1L) (WallClockMs 0L) pausedInputs, Event.Reconcile)
    let result =
        Policy.stepLevels (someConfig, pausedResult.State, ctxWith (MonotonicMs 2L) (WallClockMs 1L) pausedInputs, Event.CaptureFailed)
    Assert.Equal(Action.Restart RestartReason.CameraWedged, result.Action)
    // Status priority (Paused outranks everything) holds even though a restart was requested --
    // the restart branch never touches the Paused input.
    Assert.Equal(Status.Paused, Policy.status result.State)

[<Fact>]
let ``a cooldown-suppressed CaptureFailed returns Action.NoAction (the shell falls back to the NoFrame-to-reevaluation backstop)`` () =
    let stampsRecentWedge: RestartStamps =
        { WedgeAt = System.Nullable(500L); ReevalAt = System.Nullable(); UpgradeAt = System.Nullable() }
    let state = Policy.startLevels (MonotonicMs 0L, stampsRecentWedge, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 500L), Event.InitSucceeded)
    let result = Policy.stepLevels (someConfig, initResult.State, levelsOf (MonotonicMs 1L) (WallClockMs 600L), Event.CaptureFailed)
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``scenario: a cooldown-suppressed CaptureFailed recovers via the NoFrame-to-reevaluation backstop, with no new mechanism`` () =
    // 0001-core-brain.md, "Notes: recovery boundary" -- the cooldown-suppressed CaptureFailed
    // backstop (R2-2): core-side, the suppressed path returns NoAction; the shell tears down the
    // dead capture but keeps the sample timer running, so SampleAsync with a null reader emits
    // Sample(NoFrame, ...) on every subsequent tick. This scenario reproduces exactly that using
    // only mechanisms slice 4 already implements -- no new recovery path.
    let stampsRecentWedge: RestartStamps =
        { WedgeAt = System.Nullable(500L); ReevalAt = System.Nullable(); UpgradeAt = System.Nullable() }
    let state = Policy.startLevels (MonotonicMs 0L, stampsRecentWedge, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 500L), Event.InitSucceeded)
    let suppressed =
        Policy.stepLevels (someConfig, initResult.State, levelsOf (MonotonicMs 1L) (WallClockMs 600L), Event.CaptureFailed)
    Assert.Equal(Action.NoAction, suppressed.Action)
    // The shell keeps sampling with a null reader -- the very next sample already observes
    // NoFrame. Once continuous bad signal reaches ReevaluateAfterMs (20000) and
    // ReevaluateCooldownMs (600000, measured against ReevalAt = null => vacuously clear) permits,
    // step requests the reevaluation restart -- recovering the dead camera without ever routing
    // back through CaptureFailed/InitFailed.
    let firstBad =
        Policy.stepLevels (
            someConfig,
            suppressed.State,
            levelsOfIdle (MonotonicMs 1000L) (WallClockMs 1600L) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Status.Watching, Policy.status firstBad.State)
    let laterBad =
        Policy.stepLevels (
            someConfig,
            firstBad.State,
            levelsOfIdle (MonotonicMs(1000L + someConfig.ReevaluateAfterMs)) (WallClockMs(1600L + someConfig.ReevaluateAfterMs)) 0L,
            Event.Sample(Observation.NoFrame, 0L)
        )
    Assert.Equal(Action.Restart RestartReason.CameraReevaluation, laterBad.Action)

// --- Slice 10: camera-arrival upgrade (RFC addendum 2026-08-01) -------------------------

[<Fact>]
let ``BetterCameraAvailable before any InitSucceeded is a no-op`` () =
    // Pre-first-success (0001-core-brain.md addendum): the acquisition retry loop already
    // re-runs camera selection on every attempt, so a restart here would be pure waste.
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let result = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.BetterCameraAvailable)
    Assert.Equal(Action.NoAction, result.Action)
    Assert.Equal(Status.AcquiringCamera, Policy.status result.State)

[<Fact>]
let ``BetterCameraAvailable after InitSucceeded requests Action.Restart CameraUpgrade unconditionally on first occurrence, subject only to cooldown`` () =
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    // No prior CameraUpgrade restart -- cooldownElapsed is vacuously true against a null stamp.
    let result = Policy.stepLevels (someConfig, initResult.State, levelsOf (MonotonicMs 1L) (WallClockMs 1L), Event.BetterCameraAvailable)
    Assert.Equal(Action.Restart RestartReason.CameraUpgrade, result.Action)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 1L)
    Assert.Equal(System.Nullable(1L), snap.LastUpgradeRestartAt)

[<Fact>]
let ``a cooldown-suppressed BetterCameraAvailable returns Action.NoAction and leaves UpgradeAt unchanged`` () =
    // A CameraUpgrade restart happened recently (wall-clock 500) before this process even
    // started -- randomized initial RestartStamps continuity (R2-19 pattern).
    let stampsRecentUpgrade: RestartStamps =
        { WedgeAt = System.Nullable(); ReevalAt = System.Nullable(); UpgradeAt = System.Nullable(500L) }
    let state = Policy.startLevels (MonotonicMs 0L, stampsRecentUpgrade, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 500L), Event.InitSucceeded)
    // Only 100ms of wall-clock have elapsed since UpgradeAt (500) -- well under
    // UpgradeCooldownMs (600000) -- so the restart is suppressed.
    let result = Policy.stepLevels (someConfig, initResult.State, levelsOf (MonotonicMs 1L) (WallClockMs 600L), Event.BetterCameraAvailable)
    Assert.Equal(Action.NoAction, result.Action)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 1L)
    Assert.Equal(System.Nullable(500L), snap.LastUpgradeRestartAt)

[<Fact>]
let ``BetterCameraAvailable while Paused still requests Action.Restart CameraUpgrade, and the restart preserves the pause`` () =
    // Deliberate parity with CaptureFailed's R2-10 asymmetry: an elective upgrade restart
    // while paused/locked is harmless, and pause survives every self-restart by design. Old
    // Event.Paused -> levels StepInputs.Paused = true.
    let state = Policy.startLevels (MonotonicMs 0L, noStamps, defaultInputs)
    let initResult = Policy.stepLevels (someConfig, state, levelsOf (MonotonicMs 0L) (WallClockMs 0L), Event.InitSucceeded)
    let pausedResult =
        Policy.stepLevels (someConfig, initResult.State, ctxWith (MonotonicMs 1L) (WallClockMs 0L) pausedInputs, Event.Reconcile)
    let result =
        Policy.stepLevels (someConfig, pausedResult.State, ctxWith (MonotonicMs 2L) (WallClockMs 1L) pausedInputs, Event.BetterCameraAvailable)
    Assert.Equal(Action.Restart RestartReason.CameraUpgrade, result.Action)
    Assert.Equal(Status.Paused, Policy.status result.State)

[<Properties(Arbitrary = [| typeof<Generators> |])>]
module PropertyTests =

    [<Property>]
    let ``baseline snapshot is unarmed, in grace, and all counters are zero`` (config: PolicyConfig) (now: MonotonicMs) =
        let state = Policy.startLevels (now, noStamps, defaultInputs)
        let snap = Policy.snapshot (config, state, now)
        not snap.Armed
        && snap.InGrace
        && snap.AwayForMs = 0L
        && snap.NoSignalForMs = 0L
        && snap.InitFailStreak = 0

    [<Property>]
    let ``baseline snapshot surfaces exactly the restart stamps passed to start``
        (config: PolicyConfig)
        (now: MonotonicMs)
        (stamps: RestartStamps)
        =
        let state = Policy.startLevels (now, stamps, defaultInputs)
        let snap = Policy.snapshot (config, state, now)
        snap.LastWedgeRestartAt = stamps.WedgeAt && snap.LastReevalRestartAt = stamps.ReevalAt

    [<Property>]
    let ``grace is live, not latched: it expires exactly at GraceMs since start with no intervening events``
        (config: PolicyConfig)
        (startAt: MonotonicMs)
        =
        let state = Policy.startLevels (startAt, noStamps, defaultInputs)
        let (MonotonicMs startMs) = startAt
        let justBeforeExpiry = Policy.snapshot (config, state, MonotonicMs(startMs + config.GraceMs - 1L))
        let atExpiry = Policy.snapshot (config, state, MonotonicMs(startMs + config.GraceMs))
        justBeforeExpiry.InGrace && not atExpiry.InGrace

    // --- Properties 1-3, 7 (0001-core-brain.md, slice 3) ------------------------------------
    //
    // Restricted to the event subset slice 3 actually implements — `Sample`/`InitSucceeded` —
    // per "table tests restricted to what's expressible without SessionUnlocked/Resumed/
    // recovery events" (slice 3's scope note). `Sample(NoFrame|DarkFrame, _)` is included
    // (via `observationGen`) since `step` already handles those as no-ops in this slice; the
    // independent models below treat them as no-ops too, matching the real (deliberately
    // partial) implementation.

    let private sampleOrInitEventGen : Gen<Event> =
        Gen.oneof
            [ gen {
                let! obs = observationGen
                let! idleMs = Gen.choose (0, 3_600_000) |> Gen.map int64
                return Event.Sample(obs, idleMs)
              }
              Gen.constant Event.InitSucceeded ]

    /// One "tick": `deltaMs` of monotonic time elapses, then `event` is stepped.
    let private ticksGen : Gen<(int64 * Event) list> =
        Gen.listOf (
            gen {
                let! deltaMs = Gen.choose (0, 20_000) |> Gen.map int64
                let! event = sampleOrInitEventGen
                return (deltaMs, event)
            }
        )

    /// Per-tick result, paired with the two independent test-side models the properties check
    /// against — never against `Snapshot.Armed`/derived `State` fields, which would make the
    /// implementation its own oracle (0001-core-brain.md finding R2-28). Both models reflect
    /// only the baseline events slice 3 implements (`start`, `InitSucceeded`); slice 5 extends
    /// them with `SessionUnlocked` (unless paused)/`Resumed` -- levels-path counterpart: the
    /// suppression edge rule extends them with the suppressed -> unsuppressed transition.
    type private TickResult =
        { Now: int64
          Event: Event
          Action: Action
          /// Independent armed model: true iff a `FaceSeen` occurred since the last baseline
          /// event, as of just before this tick's event (matching what `step` itself would
          /// have used to gate this same event's `Lock` decision).
          ModelArmed: bool
          /// `now` of the most recent baseline event, as of just before this tick's event.
          LastBaselineNow: int64 }

    let private runTicks (config: PolicyConfig) (stamps: RestartStamps) (startMs: int64) (ticks: (int64 * Event) list) =
        let folder (state, nowMs, modelArmed, lastBaselineNow, acc) (deltaMs, event) =
            let nowMs' = nowMs + deltaMs
            let result = Policy.stepLevels (config, state, ctxForEvent (MonotonicMs nowMs') (WallClockMs 0L) event, event)
            let record =
                { Now = nowMs'
                  Event = event
                  Action = result.Action
                  ModelArmed = modelArmed
                  LastBaselineNow = lastBaselineNow }
            let modelArmed' =
                match event with
                | Event.InitSucceeded -> false
                | Event.Sample(Observation.FaceSeen, _) -> true
                | _ -> modelArmed
            let lastBaselineNow' =
                match event with
                | Event.InitSucceeded -> nowMs'
                | _ -> lastBaselineNow
            (result.State, nowMs', modelArmed', lastBaselineNow', record :: acc)
        let initialState = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
        let _, _, _, _, results = List.fold folder (initialState, startMs, false, startMs, []) ticks
        List.rev results

    [<Property>]
    let ``property 1: no Action.Lock unless an independent armed model says a FaceSeen occurred since the last baseline event``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        Prop.forAll (Arb.fromGen ticksGen) (fun ticks ->
            runTicks config stamps startMs ticks
            |> List.forall (fun r -> r.Action <> Action.Lock || r.ModelArmed))

    [<Property>]
    let ``property 2: no Action.Lock while inputIdleMs is below InputIdleRequiredMs``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        Prop.forAll (Arb.fromGen ticksGen) (fun ticks ->
            runTicks config stamps startMs ticks
            |> List.forall (fun r ->
                match r.Event with
                | Event.Sample(_, idleMs) -> r.Action <> Action.Lock || idleMs >= config.InputIdleRequiredMs
                | _ -> r.Action <> Action.Lock))

    [<Property>]
    let ``property 3: no Action.Lock before GraceMs elapses after the most recent baseline event``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        Prop.forAll (Arb.fromGen ticksGen) (fun ticks ->
            runTicks config stamps startMs ticks
            |> List.forall (fun r -> r.Action <> Action.Lock || r.Now - r.LastBaselineNow >= config.GraceMs))

    [<Property>]
    let ``property 7: a continuous run of NoFace shorter than AwayThresholdMs never locks, regardless of armed/grace/idle state``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        let runGen =
            gen {
                let! useInitSucceeded = Arb.generate<bool>
                let! gapToInit = Gen.choose (0, 30_000) |> Gen.map int64
                let! armFirst = Arb.generate<bool>
                let! gapToArm = Gen.choose (0, 30_000) |> Gen.map int64
                let! rawDeltas = Gen.listOf (Gen.choose (0, 2_000) |> Gen.map int64)
                let! idles = Gen.listOf (Gen.choose (0, 3_600_000) |> Gen.map int64)
                return (useInitSucceeded, gapToInit, armFirst, gapToArm, rawDeltas, idles)
            }
        Prop.forAll
            (Arb.fromGen runGen)
            (fun (useInitSucceeded, gapToInit, armFirst, gapToArm, rawDeltas, idles) ->
                let idlesArr = (if List.isEmpty idles then [ 0L ] else idles) |> List.toArray
                let mutable state = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
                let mutable now = startMs
                if useInitSucceeded then
                    now <- now + gapToInit
                    state <- (Policy.stepLevels (config, state, levelsOf (MonotonicMs now) (WallClockMs 0L), Event.InitSucceeded)).State
                if armFirst then
                    now <- now + gapToArm
                    state <-
                        (Policy.stepLevels (
                            config,
                            state,
                            levelsOfIdle (MonotonicMs now) (WallClockMs 0L) 3_600_000L,
                            Event.Sample(Observation.FaceSeen, 3_600_000L)
                        ))
                            .State
                // The away-baseline is now fixed at `now` (this event, or the initial baseline
                // if neither branch above ran) since only NoFace samples follow — construct the
                // continuous run so its cumulative elapsed time from here never reaches
                // AwayThresholdMs.
                let runStartNow = now
                let mutable cumulative = 0L
                let mutable idx = 0
                let mutable neverLocked = true
                for delta in rawDeltas do
                    let candidate = cumulative + delta
                    if candidate < config.AwayThresholdMs then
                        cumulative <- candidate
                        let sampleNow = runStartNow + cumulative
                        let idleMs = idlesArr.[idx % idlesArr.Length]
                        idx <- idx + 1
                        let result =
                            Policy.stepLevels (
                                config,
                                state,
                                levelsOfIdle (MonotonicMs sampleNow) (WallClockMs 0L) idleMs,
                                Event.Sample(Observation.NoFace, idleMs)
                            )
                        state <- result.State
                        if result.Action = Action.Lock then
                            neverLocked <- false
                neverLocked)

    // --- Properties 4, 6 (0001-core-brain.md, slice 4) ---------------------------------------

    let private badSignalObservationGen : Gen<Observation> =
        Gen.elements [ Observation.NoFrame; Observation.DarkFrame ]

    let private badSignalTicksGen : Gen<(int64 * Event) list> =
        Gen.listOf (
            gen {
                let! deltaMs = Gen.choose (0, 20_000) |> Gen.map int64
                let! obs = badSignalObservationGen
                let! idleMs = Gen.choose (0, 3_600_000) |> Gen.map int64
                return (deltaMs, Event.Sample(obs, idleMs))
            }
        )

    [<Property>]
    let ``property 4: NoFrame/DarkFrame sequences alone never produce Action.Lock``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        Prop.forAll (Arb.fromGen badSignalTicksGen) (fun ticks ->
            let initialState = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
            let folder (state, nowMs, neverLocked) (deltaMs, event) =
                let nowMs' = nowMs + deltaMs
                let result = Policy.stepLevels (config, state, ctxForEvent (MonotonicMs nowMs') (WallClockMs 0L) event, event)
                (result.State, nowMs', neverLocked && result.Action <> Action.Lock)
            let _, _, neverLocked = List.fold folder (initialState, startMs, true) ticks
            neverLocked)

    /// Any event kind may appear — the property is about restart spacing, not about which
    /// events drive `BadSignalSince` — reusing the full generic `eventGen` from Generators.
    let private wallTicksGen : Gen<(int64 * Event) list> =
        Gen.listOf (
            gen {
                let! deltaMs = Gen.choose (0, 20_000) |> Gen.map int64
                let! event = eventGen
                return (deltaMs, event)
            }
        )

    [<Property>]
    let ``property 6: at most one Action.Restart CameraReevaluation per ReevaluateCooldownMs window``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        (startWallAt: WallClockMs)
        =
        let (MonotonicMs startMs) = startAt
        let (WallClockMs startWallMs) = startWallAt
        Prop.forAll (Arb.fromGen wallTicksGen) (fun ticks ->
            let initialState = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
            let folder (state, nowMs, nowWallMs, fireTimesDesc) (deltaMs, event) =
                let nowMs' = nowMs + deltaMs
                let nowWallMs' = nowWallMs + deltaMs
                let result =
                    Policy.stepLevels (config, state, ctxForEvent (MonotonicMs nowMs') (WallClockMs nowWallMs') event, event)
                let fireTimesDesc' =
                    match result.Action with
                    | Action.Restart RestartReason.CameraReevaluation -> nowWallMs' :: fireTimesDesc
                    | _ -> fireTimesDesc
                (result.State, nowMs', nowWallMs', fireTimesDesc')
            let _, _, _, fireTimesDesc =
                List.fold folder (initialState, startMs, startWallMs, []) ticks
            // Prepend the randomized initial stamp (if any restart of this reason ever happened
            // before this process started) so the property also covers cross-restart cooldown
            // continuity (R2-19), not merely spacing among fires observed within this run.
            let fireTimes =
                match stamps.ReevalAt with
                | v when v.HasValue -> v.Value :: List.rev fireTimesDesc
                | _ -> List.rev fireTimesDesc
            fireTimes
            |> List.pairwise
            |> List.forall (fun (a, b) -> b - a >= config.ReevaluateCooldownMs))

    // --- Property 8 (0001-core-brain.md, slice 5) — rewritten for RFC 0002-environment-levels
    // (slice 1 test-migration paragraph: `applyPauseLockModel`, the FsCheck pause-lock model
    // driving this property, is a hand-rewrite hotspot, not a mechanical append). The old model
    // hand-rolled a hand-derived (isPaused, isSessionLocked) fold over `Event.SessionLocked`/
    // `SessionUnlocked`/`Paused`/`Resumed`, including the precedence interaction the incident's
    // root cause lived in. Under levels there IS no precedence interaction to model (RFC "There
    // is no pause-precedence interaction to specify"): `suppressed inputs = inputs.SessionLocked
    // || inputs.Paused` is a plain, order-independent OR, so the rewrite tracks `StepInputs`
    // directly rather than re-deriving two flags from an event stream. Level-changing ticks fire
    // `Reconcile` synchronously at the transition, mirroring the shell's `Advance(Reconcile)`
    // contract (RFC "Verification harness", model-fidelity rule) -- the same discipline slice 2's
    // environment model formalizes and slice 2's property 13 will eventually supersede this test
    // with (RFC slice-1 note: "property 8 superseded by property 13").

    /// Level-transition alphabet for this property: either a discrete level flip (fires
    /// `Reconcile` at the transition) or a `Sample` carrying the CURRENT levels, idle re-rolled
    /// fresh on every tick (matching `GetLastInputInfo` being queried fresh on every `Advance`).
    type private LevelsTick =
        | SetSessionLocked of bool
        | SetPaused of bool
        | SetLockInhibited of bool
        | SampleTick of Observation * idleMs: int64

    let private levelsTickGen : Gen<LevelsTick> =
        Gen.oneof
            [ Arb.generate<bool> |> Gen.map SetSessionLocked
              Arb.generate<bool> |> Gen.map SetPaused
              Arb.generate<bool> |> Gen.map SetLockInhibited
              gen {
                  let! obs = observationGen
                  let! idleMs = Gen.choose (0, 3_600_000) |> Gen.map int64
                  return SampleTick(obs, idleMs)
              } ]

    let private levelsTicksGen : Gen<(int64 * LevelsTick) list> =
        Gen.listOf (
            gen {
                let! deltaMs = Gen.choose (0, 20_000) |> Gen.map int64
                let! tick = levelsTickGen
                return (deltaMs, tick)
            }
        )

    [<Property>]
    let ``property 8: Sample events while suppressed (paused or session-locked) never change the armed/away/signal-health baseline and never emit Action.Lock``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        Prop.forAll (Arb.fromGen levelsTicksGen) (fun ticks ->
            let initialState = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
            let folder (state, nowMs, inputs: StepInputs, ok) (deltaMs, tick) =
                let nowMs' = nowMs + deltaMs
                match tick with
                | SetSessionLocked v ->
                    let inputs' = { inputs with SessionLocked = v }
                    let result =
                        Policy.stepLevels (config, state, ctxWith (MonotonicMs nowMs') (WallClockMs 0L) inputs', Event.Reconcile)
                    (result.State, nowMs', inputs', ok)
                | SetPaused v ->
                    let inputs' = { inputs with Paused = v }
                    let result =
                        Policy.stepLevels (config, state, ctxWith (MonotonicMs nowMs') (WallClockMs 0L) inputs', Event.Reconcile)
                    (result.State, nowMs', inputs', ok)
                | SetLockInhibited v ->
                    let inputs' = { inputs with LockInhibited = v }
                    let result =
                        Policy.stepLevels (config, state, ctxWith (MonotonicMs nowMs') (WallClockMs 0L) inputs', Event.Reconcile)
                    (result.State, nowMs', inputs', ok)
                | SampleTick(obs, idleMs) ->
                    let inputs' = { inputs with InputIdleMs = idleMs }
                    let beforeSnap = Policy.snapshot (config, state, MonotonicMs nowMs')
                    let ctx = ctxWith (MonotonicMs nowMs') (WallClockMs 0L) inputs'
                    let result = Policy.stepLevels (config, state, ctx, Event.Sample(obs, idleMs))
                    let afterSnap = Policy.snapshot (config, result.State, MonotonicMs nowMs')
                    let suppressed = inputs'.SessionLocked || inputs'.Paused
                    let ok' =
                        ok
                        && (not suppressed
                            || (result.Action = Action.NoAction
                                && beforeSnap.Armed = afterSnap.Armed
                                && beforeSnap.AwayForMs = afterSnap.AwayForMs
                                && beforeSnap.NoSignalForMs = afterSnap.NoSignalForMs))
                    (result.State, nowMs', inputs', ok')
            let _, _, _, ok = List.fold folder (initialState, startMs, defaultInputs, true) ticks
            ok)

    // --- Properties 5, 9 (0001-core-brain.md, slice 6) ---------------------------------------

    [<Property>]
    let ``property 5: at most one Action.Restart CameraWedged per RecoveryCooldownMs window``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        (startWallAt: WallClockMs)
        =
        let (MonotonicMs startMs) = startAt
        let (WallClockMs startWallMs) = startWallAt
        Prop.forAll (Arb.fromGen wallTicksGen) (fun ticks ->
            let initialState = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
            let folder (state, nowMs, nowWallMs, fireTimesDesc) (deltaMs, event) =
                let nowMs' = nowMs + deltaMs
                let nowWallMs' = nowWallMs + deltaMs
                let result =
                    Policy.stepLevels (config, state, ctxForEvent (MonotonicMs nowMs') (WallClockMs nowWallMs') event, event)
                let fireTimesDesc' =
                    match result.Action with
                    | Action.Restart RestartReason.CameraWedged -> nowWallMs' :: fireTimesDesc
                    | _ -> fireTimesDesc
                (result.State, nowMs', nowWallMs', fireTimesDesc')
            let _, _, _, fireTimesDesc =
                List.fold folder (initialState, startMs, startWallMs, []) ticks
            // Prepend the randomized initial stamp (if any CameraWedged restart ever happened
            // before this process started) so the property also covers cross-restart cooldown
            // continuity (R2-19) -- restarts can be requested by either InitFailed (streak-gated)
            // or CaptureFailed (unconditional), and the cooldown must hold across both sources.
            let fireTimes =
                match stamps.WedgeAt with
                | v when v.HasValue -> v.Value :: List.rev fireTimesDesc
                | _ -> List.rev fireTimesDesc
            fireTimes
            |> List.pairwise
            |> List.forall (fun (a, b) -> b - a >= config.RecoveryCooldownMs))

    [<Property>]
    let ``property 9: a Lock-producing Sample re-emits Action.Lock when immediately repeated with no intervening suppression change (no internal latch)``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        Prop.forAll (Arb.fromGen wallTicksGen) (fun ticks ->
            let initialState = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
            let folder (state, nowMs, ok) (deltaMs, event) =
                let nowMs' = nowMs + deltaMs
                let ctx = ctxForEvent (MonotonicMs nowMs') (WallClockMs 0L) event
                let result = Policy.stepLevels (config, state, ctx, event)
                let ok' =
                    ok
                    && (if result.Action = Action.Lock then
                            // Re-derivation, not a latch: step does not remember having emitted
                            // Lock -- it only learns a lock actually took hold via a subsequent
                            // suppression (StepInputs.SessionLocked = true via Reconcile), sourced
                            // from the real OS notification. Since no event intervenes here, the
                            // identical Sample must re-emit Action.Lock.
                            let repeat = Policy.stepLevels (config, result.State, ctx, event)
                            repeat.Action = Action.Lock
                        else
                            true)
                (result.State, nowMs', ok')
            let _, _, ok = List.fold folder (initialState, startMs, true) ticks
            ok)

    // --- Property 12 (0001-core-brain.md addendum 2026-08-01, slice 10) ---------------------

    [<Property>]
    let ``property 12: at most one Action.Restart CameraUpgrade per UpgradeCooldownMs window``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        (startWallAt: WallClockMs)
        =
        let (MonotonicMs startMs) = startAt
        let (WallClockMs startWallMs) = startWallAt
        Prop.forAll (Arb.fromGen wallTicksGen) (fun ticks ->
            let initialState = Policy.startLevels (MonotonicMs startMs, stamps, defaultInputs)
            let folder (state, nowMs, nowWallMs, fireTimesDesc) (deltaMs, event) =
                let nowMs' = nowMs + deltaMs
                let nowWallMs' = nowWallMs + deltaMs
                let result =
                    Policy.stepLevels (config, state, ctxForEvent (MonotonicMs nowMs') (WallClockMs nowWallMs') event, event)
                let fireTimesDesc' =
                    match result.Action with
                    | Action.Restart RestartReason.CameraUpgrade -> nowWallMs' :: fireTimesDesc
                    | _ -> fireTimesDesc
                (result.State, nowMs', nowWallMs', fireTimesDesc')
            let _, _, _, fireTimesDesc =
                List.fold folder (initialState, startMs, startWallMs, []) ticks
            // Prepend the randomized initial stamp (R2-19 pattern), covering cross-restart
            // cooldown continuity for the third restart reason exactly as properties 5/6 do
            // for the first two.
            let fireTimes =
                match stamps.UpgradeAt with
                | v when v.HasValue -> v.Value :: List.rev fireTimesDesc
                | _ -> List.rev fireTimesDesc
            fireTimes
            |> List.pairwise
            |> List.forall (fun (a, b) -> b - a >= config.UpgradeCooldownMs))
