namespace PresenceLock.Core

/// The decision core: pure functions from `(PolicyConfig, State, time, Event)` to a new
/// `State` plus a single `Action`. No clocks, no I/O — every `now`/`nowWall` is a caller-
/// supplied parameter (rfc-core-brain.md, "Time semantics").
module Policy =

    /// Called exactly once per process, in the shell's `WatcherContext` constructor, before
    /// the first camera-acquisition attempt. All later re-baselining goes through events into
    /// the same `State` — never a second `start` call. Takes no `PolicyConfig`: `start` only
    /// stamps the initial baseline and imports the persisted stamps; every threshold is
    /// compared live at `step`/`snapshot` time, so a config parameter here would be dead — and
    /// ambiguous against `step`'s.
    let start (now: MonotonicMs, stamps: RestartStamps) : State =
        { HasSucceededOnce = false
          InitFailStreak = 0
          Armed = false
          IsPaused = false
          IsSessionLocked = false
          GraceBaselineAt = now
          AwayBaselineAt = now
          BadSignalSince = None
          Stamps = stamps
          // Fresh state, pre-first-event: not paused, not locked, no failure yet, no success
          // yet — priority row 4 (Status priority table, rfc-core-brain.md "Notes").
          CachedStatus = Status.AcquiringCamera }

    /// Total, priority-ordered `State -> Status` projection (rfc-core-brain.md, the Status
    /// priority table in "Notes"). `PolicyConfig`/`now` are needed only for row 5 (`NoSignal`):
    /// whether the current continuous bad-signal run has crossed `NoSignalReportAfterMs` as of
    /// this step's `now`. Rows 1-4 are pure `State` predicates. Recomputed on every `step` and
    /// cached in `State.CachedStatus` — see the module-level doc on `status`.
    let private computeStatus (config: PolicyConfig, state: State, now: MonotonicMs) : Status =
        if state.IsPaused then
            Status.Paused
        elif state.IsSessionLocked then
            Status.SessionLocked
        elif not state.HasSucceededOnce then
            if state.InitFailStreak >= 1 then Status.Recovering else Status.AcquiringCamera
        else
            let (MonotonicMs nowMs) = now
            let badSignalForMs =
                match state.BadSignalSince with
                | None -> 0L
                | Some (MonotonicMs sinceMs) -> nowMs - sinceMs
            if badSignalForMs >= config.NoSignalReportAfterMs then
                Status.NoSignal
            else
                Status.Watching

    /// Recomputes and caches `Status` against the state as of this `step`'s `now` — every
    /// branch below ends with this, since `Status`'s `NoSignal` row (row 5) depends on `now`
    /// even when no other field changed (slice 4).
    let private withRecomputedStatus (config: PolicyConfig, now: MonotonicMs) (state: State) : State =
        { state with CachedStatus = computeStatus (config, state, now) }

    /// Wall-clock cooldown check shared by both restart reasons (slice 4 introduces the first
    /// use, for `ReevaluateCooldownMs`; slice 6's recovery streak reuses this same idiom for
    /// `RecoveryCooldownMs` per the RFC's slice-6 refactor note — R2-30). An absent stamp
    /// (never restarted for this reason) always clears the cooldown.
    let private cooldownElapsed (nowWallMs: int64, cooldownMs: int64, lastRestartAt: System.Nullable<int64>) : bool =
        if lastRestartAt.HasValue then
            nowWallMs - lastRestartAt.Value >= cooldownMs
        else
            true

    /// `step` for `Sample`/`InitSucceeded` — the slice-3 lock decision, plus slice 4's signal
    /// accounting for `NoFrame`/`DarkFrame` — and the still-unimplemented placeholder cases for
    /// event kinds owned by later slices (session/pause: slice 5; recovery: slice 6).
    let step
        (config: PolicyConfig, state: State, now: MonotonicMs, nowWall: WallClockMs, event: Event)
        : StepResult =
        match event with
        | Event.Sample(Observation.FaceSeen, _inputIdleMs) ->
            let updated =
                { state with
                    Armed = true
                    AwayBaselineAt = now
                    BadSignalSince = None }
                |> withRecomputedStatus (config, now)
            { State = updated; Action = Action.NoAction }
        | Event.Sample(Observation.NoFace, inputIdleMs) ->
            // A healthy sample (camera working, just no face) resets the signal-health clock
            // identically to `FaceSeen` — only `NoFrame`/`DarkFrame` are "bad signal" — but,
            // unlike `FaceSeen`, does not touch `Armed`/`AwayBaselineAt`: that's precisely what
            // lets the away clock accumulate toward `AwayThresholdMs`.
            let updated = { state with BadSignalSince = None } |> withRecomputedStatus (config, now)
            let (MonotonicMs nowMs) = now
            let (MonotonicMs graceBaselineMs) = updated.GraceBaselineAt
            let (MonotonicMs awayBaselineMs) = updated.AwayBaselineAt
            let outOfGrace = nowMs - graceBaselineMs >= config.GraceMs
            let awaySatisfied = nowMs - awayBaselineMs >= config.AwayThresholdMs
            let idleSatisfied = inputIdleMs >= config.InputIdleRequiredMs
            let action =
                if updated.Armed && outOfGrace && awaySatisfied && idleSatisfied then
                    Action.Lock
                else
                    Action.NoAction
            { State = updated; Action = action }
        | Event.Sample((Observation.NoFrame | Observation.DarkFrame), _inputIdleMs) ->
            // Fail-open: a `NoFrame`/`DarkFrame` observation resets the away-baseline
            // identically to `FaceSeen` (both represent "not a valid continuous away
            // observation") but, unlike `FaceSeen`, never arms.
            //
            // Bad-signal-streak start: latched on the first bad sample of a run and carried
            // forward unchanged by every subsequent bad sample in the same run — this is what
            // gives the pinned "saturated streak" semantics (R2-27) for free: once
            // `ReevaluateAfterMs` is crossed and an attempt is cooldown-suppressed, nothing here
            // ever resets `BadSignalSince`, so the very next sample after the cooldown clears
            // is already past threshold and fires immediately, rather than re-accumulating up
            // to `ReevaluateAfterMs` again.
            let badSignalSince =
                match state.BadSignalSince with
                | Some since -> since
                | None -> now
            let (MonotonicMs nowMs) = now
            let (MonotonicMs sinceMs) = badSignalSince
            let badSignalForMs = nowMs - sinceMs
            let (WallClockMs nowWallMs) = nowWall
            let reevaluateDue =
                badSignalForMs >= config.ReevaluateAfterMs
                && cooldownElapsed (nowWallMs, config.ReevaluateCooldownMs, state.Stamps.ReevalAt)
            let stamps =
                if reevaluateDue then
                    { state.Stamps with ReevalAt = System.Nullable(nowWallMs) }
                else
                    state.Stamps
            let updated =
                { state with
                    AwayBaselineAt = now
                    BadSignalSince = Some badSignalSince
                    Stamps = stamps }
                |> withRecomputedStatus (config, now)
            let action =
                if reevaluateDue then
                    Action.Restart RestartReason.CameraReevaluation
                else
                    Action.NoAction
            { State = updated; Action = action }
        | Event.InitSucceeded ->
            let updated =
                { state with
                    Armed = false
                    GraceBaselineAt = now
                    AwayBaselineAt = now
                    BadSignalSince = None
                    HasSucceededOnce = true
                    InitFailStreak = 0 }
                |> withRecomputedStatus (config, now)
            { State = updated; Action = Action.NoAction }
        | Event.InitFailed _
        | Event.CaptureFailed
        | Event.SessionLocked
        | Event.SessionUnlocked
        | Event.Paused
        | Event.Resumed ->
            { State = state; Action = Action.NoAction }

    /// Computed during `step` and cached in `State` — reflects the world as of the most
    /// recent event, at most one sample interval stale under normal sampling; the shell reads
    /// it only inside `Advance`, immediately after a `step`.
    let status (state: State) : Status =
        state.CachedStatus

    /// Takes `PolicyConfig` because `InGrace`/`AwayForMs`/`NoSignalForMs` are derived live
    /// against thresholds at projection time, consistent with the live-config rule — grace is
    /// never latched.
    let snapshot (config: PolicyConfig, state: State, now: MonotonicMs) : Snapshot =
        let (MonotonicMs nowMs) = now
        let (MonotonicMs graceBaselineMs) = state.GraceBaselineAt
        let (MonotonicMs awayBaselineMs) = state.AwayBaselineAt
        let noSignalForMs =
            match state.BadSignalSince with
            | None -> 0L
            | Some (MonotonicMs sinceMs) -> nowMs - sinceMs
        { Armed = state.Armed
          InGrace = nowMs - graceBaselineMs < config.GraceMs
          AwayForMs = nowMs - awayBaselineMs
          NoSignalForMs = noSignalForMs
          InitFailStreak = state.InitFailStreak
          LastWedgeRestartAt = state.Stamps.WedgeAt
          LastReevalRestartAt = state.Stamps.ReevalAt }
