module PresenceLock.Core.Tests.Tests

open Xunit
open FsCheck
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
let ``a FaceSeen sample arms the state`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    let result =
        Policy.step (someConfig, state, MonotonicMs 0L, WallClockMs 0L, Event.Sample(Observation.FaceSeen, 0L))
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 0L)
    Assert.True(snap.Armed)

[<Fact>]
let ``look-away locks once armed, past grace, idle-satisfied, and the away threshold has elapsed`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    // Arm past grace (GraceMs = 10000) so the arming sample itself isn't in-grace.
    let armedResult =
        Policy.step (someConfig, state, MonotonicMs 10001L, WallClockMs 0L, Event.Sample(Observation.FaceSeen, 20000L))
    // AwayThresholdMs = 5000, elapsed from the away-baseline the FaceSeen sample just set.
    let result =
        Policy.step (
            someConfig,
            armedResult.State,
            MonotonicMs 15001L,
            WallClockMs 0L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.Lock, result.Action)

[<Fact>]
let ``a NoFace sample does not lock while inputIdleMs is below InputIdleRequiredMs, even past grace and away threshold`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    let armedResult =
        Policy.step (someConfig, state, MonotonicMs 10001L, WallClockMs 0L, Event.Sample(Observation.FaceSeen, 20000L))
    // Away threshold and grace are both satisfied by this timestamp; only idle is not
    // (InputIdleRequiredMs = 10000).
    let result =
        Policy.step (
            someConfig,
            armedResult.State,
            MonotonicMs 15001L,
            WallClockMs 0L,
            Event.Sample(Observation.NoFace, 0L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``a NoFace sample does not lock before GraceMs elapses since the baseline event, even though armed/idle/away are satisfied`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    // Arms almost immediately after start — grace is measured from `start` (the baseline
    // event), not from the arming FaceSeen sample, so grace is still running.
    let armedResult =
        Policy.step (someConfig, state, MonotonicMs 1L, WallClockMs 0L, Event.Sample(Observation.FaceSeen, 20000L))
    // AwayThresholdMs (5000) has elapsed since the away-baseline the FaceSeen sample set, and
    // idle is satisfied, but only 5001 ms have elapsed since start — GraceMs (10000) has not.
    let result =
        Policy.step (
            someConfig,
            armedResult.State,
            MonotonicMs 5001L,
            WallClockMs 0L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``InitSucceeded is a baseline event: it disarms, re-baselines grace/away, and transitions status to Watching`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    // Arm before InitSucceeded to prove InitSucceeded disarms — a stray FaceSeen before
    // acquisition truly succeeds must not carry an armed flag across the baseline.
    let armedResult =
        Policy.step (someConfig, state, MonotonicMs 1L, WallClockMs 0L, Event.Sample(Observation.FaceSeen, 20000L))
    let result =
        Policy.step (someConfig, armedResult.State, MonotonicMs 2L, WallClockMs 0L, Event.InitSucceeded)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs 2L)
    Assert.False(snap.Armed)
    Assert.True(snap.InGrace)
    Assert.Equal(0L, snap.AwayForMs)
    Assert.Equal(Status.Watching, Policy.status result.State)

[<Fact>]
let ``slow InitSucceeded does not consume grace measured from start's now`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    // Camera acquisition takes longer than GraceMs (10000) to succeed.
    let initResult =
        Policy.step (someConfig, state, MonotonicMs 15000L, WallClockMs 0L, Event.InitSucceeded)
    // Arms immediately after success.
    let armedResult =
        Policy.step (
            someConfig,
            initResult.State,
            MonotonicMs 15001L,
            WallClockMs 0L,
            Event.Sample(Observation.FaceSeen, 20000L)
        )
    // AwayThresholdMs (5000) elapses since the arming sample, but only ~5001 ms have passed
    // since InitSucceeded's baseline (15000) — grace (10000) must still be running, measured
    // from InitSucceeded, not from `start`'s stale t0 (which would already show as expired).
    let result =
        Policy.step (
            someConfig,
            armedResult.State,
            MonotonicMs 20001L,
            WallClockMs 0L,
            Event.Sample(Observation.NoFace, 20000L)
        )
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``never-seen must not lock, even when away/idle/grace are all satisfied`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps)
    // No FaceSeen has ever occurred, so Armed stays false no matter how long the away/grace
    // clocks run or how idle the input is.
    let result =
        Policy.step (someConfig, state, MonotonicMs 20001L, WallClockMs 0L, Event.Sample(Observation.NoFace, 20000L))
    Assert.Equal(Action.NoAction, result.Action)

[<Fact>]
let ``step is still an identity placeholder for event kinds slice 3 does not own (session/pause/recovery)`` () =
    // Sample/InitSucceeded gain real behavior in slice 3 (tested below); the remaining event
    // kinds stay untouched placeholders until slices 5 (session/pause) and 6 (recovery).
    let state = Policy.start (MonotonicMs 0L, noStamps)
    let events =
        [ Event.InitFailed false
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

    // --- Properties 1-3, 7 (rfc-core-brain.md, slice 3) ------------------------------------
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
    /// implementation its own oracle (rfc-core-brain.md finding R2-28). Both models reflect
    /// only the baseline events slice 3 implements (`start`, `InitSucceeded`); slice 5 extends
    /// them with `SessionUnlocked` (unless paused)/`Resumed`.
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
            let result = Policy.step (config, state, MonotonicMs nowMs', WallClockMs 0L, event)
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
        let initialState = Policy.start (MonotonicMs startMs, stamps)
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
                let mutable state = Policy.start (MonotonicMs startMs, stamps)
                let mutable now = startMs
                if useInitSucceeded then
                    now <- now + gapToInit
                    state <- (Policy.step (config, state, MonotonicMs now, WallClockMs 0L, Event.InitSucceeded)).State
                if armFirst then
                    now <- now + gapToArm
                    state <-
                        (Policy.step (
                            config,
                            state,
                            MonotonicMs now,
                            WallClockMs 0L,
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
                            Policy.step (
                                config,
                                state,
                                MonotonicMs sampleNow,
                                WallClockMs 0L,
                                Event.Sample(Observation.NoFace, idleMs)
                            )
                        state <- result.State
                        if result.Action = Action.Lock then
                            neverLocked <- false
                neverLocked)
