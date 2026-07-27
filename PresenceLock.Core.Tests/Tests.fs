module PresenceLock.Core.Tests.Tests

open Xunit
open FsCheck.Xunit
open PresenceLock.Core
open PresenceLock.Core.Tests.Generators

let private noStamps : RestartStamps =
    { WedgeAt = System.Nullable(); ReevalAt = System.Nullable() }

[<Fact>]
let ``baseline status immediately after start is AcquiringCamera`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    Assert.Equal(Status.AcquiringCamera, Policy.status state)

let private someConfig : PolicyConfig =
    { AwayThresholdMs = 5000L
      InputIdleRequiredMs = 10000L
      GraceMs = 10000L
      NoSignalReportAfterMs = 10000L
      ReevaluateAfterMs = 20000L
      ReevaluateCooldownMs = 600000L
      RecoveryFailureThreshold = 3
      RecoveryCooldownMs = 600000L }

[<Fact>]
let ``step is a total identity placeholder for every event kind before slice 3 wires real transitions`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    let events =
        [ Event.Sample(Observation.FaceSeen, 0L)
          Event.Sample(Observation.NoFace, 0L)
          Event.Sample(Observation.NoFrame, 0L)
          Event.Sample(Observation.DarkFrame, 0L)
          Event.InitSucceeded
          Event.InitFailed false
          Event.InitFailed true
          Event.CaptureFailed
          Event.SessionLocked
          Event.SessionUnlocked
          Event.Paused
          Event.Resumed ]
    for event in events do
        let result = Policy.step (someConfig, state, MonotonicMs 0L, WallClockMs 0L, event)
        Assert.Equal(Action.NoAction, result.Action)
        Assert.Equal(Status.AcquiringCamera, Policy.status result.State)

[<Properties(Arbitrary = [| typeof<Generators> |])>]
module PropertyTests =

    [<Property>]
    let ``baseline snapshot is unarmed, in grace, and all counters are zero`` (config: PolicyConfig) (now: MonotonicMs) =
        let state = Policy.start (now, noStamps)
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
        let state = Policy.start (now, stamps)
        let snap = Policy.snapshot (config, state, now)
        snap.LastWedgeRestartAt = stamps.WedgeAt && snap.LastReevalRestartAt = stamps.ReevalAt

    [<Property>]
    let ``grace is live, not latched: it expires exactly at GraceMs since start with no intervening events``
        (config: PolicyConfig)
        (startAt: MonotonicMs)
        =
        let state = Policy.start (startAt, noStamps)
        let (MonotonicMs startMs) = startAt
        let justBeforeExpiry = Policy.snapshot (config, state, MonotonicMs(startMs + config.GraceMs - 1L))
        let atExpiry = Policy.snapshot (config, state, MonotonicMs(startMs + config.GraceMs))
        justBeforeExpiry.InGrace && not atExpiry.InGrace
