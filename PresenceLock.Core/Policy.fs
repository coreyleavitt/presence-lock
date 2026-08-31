namespace PresenceLock.Core

/// The decision core: pure functions from `(PolicyConfig, State, time, Event)` to a new
/// `State` plus a single `Action`. No clocks, no I/O — every `now`/`nowWall` is a caller-
/// supplied parameter (0001-core-brain.md, "Time semantics").
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
          // Old path never reads LastInputs — an all-false/0 instance, never inference from
          // a struct default (RFC 0002-environment-levels' Construction discipline is what
          // makes StepInputs a reference type in the first place; this literal instance is
          // unrelated to that hazard since nothing here reads it).
          LastInputs =
            { SessionLocked = false
              Paused = false
              LockInhibited = false
              InputIdleMs = 0L }
          GraceBaselineAt = now
          AwayBaselineAt = now
          BadSignalSince = None
          Stamps = stamps
          // Fresh state, pre-first-event: not paused, not locked, no failure yet, no success
          // yet — priority row 4 (Status priority table, 0001-core-brain.md "Notes").
          CachedStatus = Status.AcquiringCamera }

    /// Levels-path counterpart of `start` (RFC 0002-environment-levels): takes the initial
    /// `StepInputs` the shell sampled before acquisition begins (WTS query + pause consume-
    /// and-clear complete first — restart pause-inheritance becomes ordinary input passing).
    /// `LastInputs` is initialized to `inputs` VERBATIM — pinned, not left to inference: the
    /// suppression-edge rule's first-call correctness depends on it, since any other initial
    /// value (a default reads "unsuppressed") would make `suppressed LastInputs` false on a
    /// restart-while-locked process and silently skip the baseline reset on the first real
    /// unlock it observes. The initial `CachedStatus` is likewise computed from `inputs`
    /// directly (rows 1-2 of the priority table) rather than via `computeLevelsStatus`: pre-
    /// first-event, `InitFailStreak` is always 0 and `HasSucceededOnce` is always false, so
    /// rows 3/5/6 can never apply and no `PolicyConfig` is needed — matching `start`'s
    /// no-`PolicyConfig` signature. A restart-while-paused process therefore reports `Paused`
    /// from its very first render, never a one-frame `AcquiringCamera` flash.
    let startLevels (now: MonotonicMs, stamps: RestartStamps, inputs: StepInputs) : State =
        let initialStatus =
            if inputs.Paused then Status.Paused
            elif inputs.SessionLocked then Status.SessionLocked
            else Status.AcquiringCamera
        { HasSucceededOnce = false
          InitFailStreak = 0
          Armed = false
          IsPaused = false
          IsSessionLocked = false
          LastInputs = inputs
          GraceBaselineAt = now
          AwayBaselineAt = now
          BadSignalSince = None
          Stamps = stamps
          CachedStatus = initialStatus }

    /// Total, priority-ordered `State -> Status` projection (0001-core-brain.md, the Status
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

    /// Levels-path counterpart of `computeStatus` (RFC 0002-environment-levels, "Status"):
    /// the same six-case priority table, but rows 1-2 read `state.LastInputs` instead of the
    /// deleted folded `IsPaused`/`IsSessionLocked` flags. Rows 3-6 are unchanged. Called only
    /// from `stepLevels`'s tail (after `LastInputs` has already been overwritten with the
    /// current call's inputs) and from `startLevels` (pre-first-event, where rows 3/5/6 can
    /// never apply since `InitFailStreak` is always 0 and `HasSucceededOnce` is always false).
    let private computeLevelsStatus (config: PolicyConfig, state: State, now: MonotonicMs) : Status =
        let inputs = state.LastInputs
        if inputs.Paused then
            Status.Paused
        elif inputs.SessionLocked then
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

    let private withRecomputedLevelsStatus (config: PolicyConfig, now: MonotonicMs) (state: State) : State =
        { state with CachedStatus = computeLevelsStatus (config, state, now) }

    /// `suppressed inputs = inputs.SessionLocked || inputs.Paused` (RFC 0002-environment-
    /// levels, "Suppression and the single edge rule") — the entire former truth table
    /// collapses into this one predicate plus the edge rule in `stepLevels`.
    let private suppressed (inputs: StepInputs) : bool = inputs.SessionLocked || inputs.Paused

    /// Wall-clock cooldown check shared by both restart reasons (slice 4 introduces the first
    /// use, for `ReevaluateCooldownMs`; slice 6's recovery streak reuses this same idiom for
    /// `RecoveryCooldownMs` per the RFC's slice-6 refactor note — R2-30). An absent stamp
    /// (never restarted for this reason) always clears the cooldown.
    let private cooldownElapsed (nowWallMs: int64, cooldownMs: int64, lastRestartAt: System.Nullable<int64>) : bool =
        if lastRestartAt.HasValue then
            nowWallMs - lastRestartAt.Value >= cooldownMs
        else
            true

    /// Shared wedge-restart decision (0001-core-brain.md slice-6 refactor note, R2-30): given
    /// whether the caller already wants to request a `CameraWedged` restart (`InitFailed` gates
    /// this on streak+classification before calling in; `CaptureFailed` always wants it once
    /// `HasSucceededOnce`), applies the `RecoveryCooldownMs` gate against `Stamps.WedgeAt` via
    /// `cooldownElapsed`, and, only if the restart actually fires, stamps `WedgeAt` with this
    /// step's `nowWall` -- deduplicating the "check cooldown, stamp on fire" idiom the two
    /// recovery events would otherwise re-derive independently.
    let private requestWedgeRestart
        (nowWallMs: int64, cooldownMs: int64, stamps: RestartStamps, wantsRestart: bool)
        : RestartStamps * bool =
        let restartDue = wantsRestart && cooldownElapsed (nowWallMs, cooldownMs, stamps.WedgeAt)
        let stamps' =
            if restartDue then
                { stamps with WedgeAt = System.Nullable(nowWallMs) }
            else
                stamps
        (stamps', restartDue)

    /// A baseline-event reset shared by `InitSucceeded`, `SessionUnlocked` (unless paused), and
    /// `Resumed` (unless already resumed): disarm, re-baseline grace/away from `now`, and reset
    /// the signal-health clock (0001-core-brain.md, R2-20) — parity with `ResumeSampling()`'s
    /// `noSignalStreak = 0`. Deliberately leaves `IsPaused`/`IsSessionLocked` untouched; each
    /// caller sets exactly the flag(s) its own event owns.
    let private applyBaselineReset (now: MonotonicMs) (state: State) : State =
        { state with
            Armed = false
            GraceBaselineAt = now
            AwayBaselineAt = now
            BadSignalSince = None }

    /// Per-event match for the levels path (RFC 0002-environment-levels, "Suppression and the
    /// single edge rule"). Fully suppression-blind: never reads `LastInputs`/suppression and
    /// never writes `LastInputs`/`CachedStatus` — both are wrapper-owned by `stepLevels`'s
    /// single tail, which is thereby the only writer of `LastInputs` and the only caller of
    /// status computation. This is deliberately NOT a drop-in port of `step`'s per-event
    /// match: the `when state.IsPaused || state.IsSessionLocked` guard on the `Sample` arms
    /// is deleted, not ported (the wrapper's while-suppressed no-op subsumes it), idle is
    /// read from `ctx.Inputs.InputIdleMs` rather than the `Sample` payload's `inputIdleMs`
    /// (the payload is ignored here, kept only so the `Event` shape stays shared with the old
    /// path this slice), and the four legacy session/pause events become decision-state
    /// no-ops — transitional arms, deleted outright once the shell migration removes the
    /// events themselves from `Event`. Failure/restart events (`InitFailed`, `CaptureFailed`,
    /// `BetterCameraAvailable`) are ported unchanged: they were already pause-immune
    /// (0001-core-brain.md R2-10), i.e. already suppression-blind, so they need no adaptation.
    let private dispatch (config: PolicyConfig, state: State, ctx: StepContext, event: Event) : StepResult =
        let now = ctx.Now
        let nowWall = ctx.NowWall
        match event with
        | Event.Sample(Observation.FaceSeen, _inputIdleMs) ->
            let updated =
                { state with
                    Armed = true
                    AwayBaselineAt = now
                    BadSignalSince = None }
            { State = updated; Action = Action.NoAction }
        | Event.Sample(Observation.NoFace, _inputIdleMs) ->
            let updated = { state with BadSignalSince = None }
            let (MonotonicMs nowMs) = now
            let (MonotonicMs graceBaselineMs) = updated.GraceBaselineAt
            let (MonotonicMs awayBaselineMs) = updated.AwayBaselineAt
            let outOfGrace = nowMs - graceBaselineMs >= config.GraceMs
            let awaySatisfied = nowMs - awayBaselineMs >= config.AwayThresholdMs
            let idleSatisfied = ctx.Inputs.InputIdleMs >= config.InputIdleRequiredMs
            let action =
                if updated.Armed && outOfGrace && awaySatisfied && idleSatisfied then
                    Action.Lock
                else
                    Action.NoAction
            { State = updated; Action = action }
        | Event.Sample((Observation.NoFrame | Observation.DarkFrame), _inputIdleMs) ->
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
            { State = updated; Action = Action.NoAction }
        | Event.InitFailed handleInvalid ->
            let streak = state.InitFailStreak + 1
            let (WallClockMs nowWallMs) = nowWall
            let wantsRestart = handleInvalid && streak >= config.RecoveryFailureThreshold
            let stamps, restartDue =
                requestWedgeRestart (nowWallMs, config.RecoveryCooldownMs, state.Stamps, wantsRestart)
            let updated = { state with InitFailStreak = streak; Stamps = stamps }
            let action = if restartDue then Action.Restart RestartReason.CameraWedged else Action.NoAction
            { State = updated; Action = action }
        | Event.CaptureFailed ->
            if not state.HasSucceededOnce then
                { State = state; Action = Action.NoAction }
            else
                let (WallClockMs nowWallMs) = nowWall
                let stamps, restartDue =
                    requestWedgeRestart (nowWallMs, config.RecoveryCooldownMs, state.Stamps, true)
                let updated = { state with Stamps = stamps }
                let action = if restartDue then Action.Restart RestartReason.CameraWedged else Action.NoAction
                { State = updated; Action = action }
        | Event.BetterCameraAvailable ->
            if not state.HasSucceededOnce then
                { State = state; Action = Action.NoAction }
            else
                let (WallClockMs nowWallMs) = nowWall
                let upgradeDue = cooldownElapsed (nowWallMs, config.UpgradeCooldownMs, state.Stamps.UpgradeAt)
                let stamps =
                    if upgradeDue then
                        { state.Stamps with UpgradeAt = System.Nullable(nowWallMs) }
                    else
                        state.Stamps
                let updated = { state with Stamps = stamps }
                let action = if upgradeDue then Action.Restart RestartReason.CameraUpgrade else Action.NoAction
                { State = updated; Action = action }
        | Event.Reconcile
        | Event.SessionLocked
        | Event.SessionUnlocked
        | Event.Paused
        | Event.Resumed ->
            // Reconcile: no decision-state change, no Action -- the wrapper's edge rule plus
            // tail is its entire effect (Event.Reconcile's doc comment in Types.fs); never
            // emits Lock, since a lock decision needs a current observation and Reconcile
            // carries none. Legacy session/pause events: decision-state no-ops in the levels
            // path, transitional only -- suppression is now derived from StepInputs, not from
            // these events, so they carry no levels-path meaning.
            { State = state; Action = Action.NoAction }

    /// Levels-path step (RFC 0002-environment-levels, "Suppression and the single edge
    /// rule"): a thin wrapper composing (1) suppression-edge derivation and baseline reset,
    /// (2) the while-suppressed `Sample` no-op (hoisted out of the per-event match so
    /// `dispatch` is fully suppression-blind), (3) suppression-blind `dispatch`, (4) a single
    /// tail that is the only writer of `LastInputs` and the only caller of levels-status
    /// computation. This is what makes the edge rule sound: every call -- every event,
    /// suppressed or not, no-op or not -- ends by recording the current inputs into
    /// `LastInputs` and recomputing `CachedStatus`, so an edge can be delayed by at most one
    /// call, never lost, because both sides of the `suppressed` comparison are re-supplied
    /// fresh on every call.
    let stepLevels (config: PolicyConfig, state: State, ctx: StepContext, event: Event) : StepResult =
        let edge = suppressed state.LastInputs && not (suppressed ctx.Inputs)
        let state' = if edge then applyBaselineReset ctx.Now state else state
        let result =
            match event with
            | Event.Sample _ when suppressed ctx.Inputs -> { State = state'; Action = Action.NoAction }
            | _ -> dispatch (config, state', ctx, event)
        { result with
            State =
                { result.State with LastInputs = ctx.Inputs }
                |> withRecomputedLevelsStatus (config, ctx.Now) }

    /// = `LastInputs.LockInhibited` as of the most recent `stepLevels` call; cached like
    /// `Status` (RFC 0002-environment-levels, "Status"). The fire-time lock GATE itself is a
    /// later slice -- this slice only carries and projects the field, exactly as pinned:
    /// `LockInhibited` never influences any decision here.
    let lockInhibited (state: State) : bool = state.LastInputs.LockInhibited

    /// `step` for `Sample`/`InitSucceeded` — the slice-3 lock decision, plus slice 4's signal
    /// accounting for `NoFrame`/`DarkFrame` — plus slice 5's session/pause re-baselining. The
    /// still-unimplemented placeholder cases are recovery events, owned by slice 6.
    let step
        (config: PolicyConfig, state: State, now: MonotonicMs, nowWall: WallClockMs, event: Event)
        : StepResult =
        match event with
        // Defense in depth (0001-core-brain.md, "Sample events outside the watching window"):
        // `Sample` is a complete no-op — unchanged `State`, no effects — whenever paused or
        // session-locked, regardless of observation kind. This must be checked ahead of the
        // per-observation arms below, not folded into each of them individually, since it
        // overrides all of them uniformly.
        | Event.Sample(_, _) when state.IsPaused || state.IsSessionLocked ->
            { State = state; Action = Action.NoAction }
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
        | Event.SessionLocked ->
            // Not a baseline event -- only marks the OS fact. Re-delivery while already
            // session-locked is idempotent (setting an already-true flag changes nothing).
            // Status priority (Paused outranks SessionLocked) gives "stays Paused" for free
            // when this arrives mid-pause -- no pause check needed here.
            let updated = { state with IsSessionLocked = true } |> withRecomputedStatus (config, now)
            { State = updated; Action = Action.NoAction }
        | Event.SessionUnlocked ->
            if state.IsPaused then
                // Pause-precedence (0001-core-brain.md truth table): a SessionUnlocked while
                // paused is a complete no-op, matching Program.cs's `if (paused) return;` guard
                // in OnSessionSwitch -- it must not re-baseline, arm, or move Status away from
                // Paused. Only an explicit Resumed clears the pause.
                { State = state; Action = Action.NoAction }
            else
                let updated =
                    { applyBaselineReset now state with IsSessionLocked = false }
                    |> withRecomputedStatus (config, now)
                { State = updated; Action = Action.NoAction }
        | Event.Paused ->
            // Not a baseline event -- Armed/away/signal-health survive a pause unchanged, so
            // Resumed can restore exactly where sampling left off. Re-delivery while already
            // paused is idempotent.
            let updated = { state with IsPaused = true } |> withRecomputedStatus (config, now)
            { State = updated; Action = Action.NoAction }
        | Event.Resumed ->
            if state.IsPaused then
                let updated =
                    { state with IsPaused = false }
                    |> applyBaselineReset now
                    |> withRecomputedStatus (config, now)
                { State = updated; Action = Action.NoAction }
            else
                // Session-event idempotency (R1-36): Resumed while already running is a no-op
                // -- it must not re-baseline (which would incorrectly disarm/reset the away
                // clock on a stray duplicate SessionSwitch).
                { State = state; Action = Action.NoAction }
        | Event.InitFailed handleInvalid ->
            // Pre-success only in practice (0001-core-brain.md, "Notes: recovery boundary").
            // InitFailStreak counts every consecutive pre-success InitFailed regardless of
            // classification -- the Status priority table's row 3 reads "no InitSucceeded yet;
            // >=1 InitFailed seen," not ">=1 handle-invalid InitFailed seen," so Status.Recovering
            // engages on the very first failure of either kind (this is what makes the unplugged-
            // camera retry loop show as Recovering, per "Notes: Camera unplug/replug, analyzed").
            // The wedge-recovery *restart*, however, additionally requires this triggering event
            // to be handle-invalid: a run of handleInvalid:false failures (no camera present) can
            // grow the streak arbitrarily without ever restarting, because restarting the process
            // cannot summon a camera that isn't there -- exactly the unplug analysis's "the
            // pre-success streak gate requires handle-invalid failures."
            let streak = state.InitFailStreak + 1
            let (WallClockMs nowWallMs) = nowWall
            let wantsRestart = handleInvalid && streak >= config.RecoveryFailureThreshold
            let stamps, restartDue =
                requestWedgeRestart (nowWallMs, config.RecoveryCooldownMs, state.Stamps, wantsRestart)
            let updated =
                { state with InitFailStreak = streak; Stamps = stamps }
                |> withRecomputedStatus (config, now)
            let action = if restartDue then Action.Restart RestartReason.CameraWedged else Action.NoAction
            { State = updated; Action = action }
        | Event.CaptureFailed ->
            // A previously-live capture just died (0001-core-brain.md, "Notes: recovery
            // boundary"). Deliberately *not* gated by IsPaused/IsSessionLocked -- unlike Sample,
            // failure events are pause-immune by design (R2-10): the camera is kept alive while
            // paused, so its death is a real fact requiring recovery regardless, and this branch
            // never touches IsPaused/IsSessionLocked, so the restart preserves the pause (the
            // R1-30 intent) exactly as it preserves every other baseline field it doesn't own.
            // Once InitSucceeded has occurred, any CaptureFailed requests the restart
            // unconditionally on its first occurrence, subject only to RecoveryCooldownMs --
            // never to RecoveryFailureThreshold, and there is no classification to gate on (the
            // event carries none). Pre-success (HasSucceededOnce = false) cannot occur along the
            // shell's real call path (MediaCapture.Failed only fires on a live capture) -- kept
            // as a no-op here for totality rather than as a recovery trigger the RFC never
            // describes.
            if not state.HasSucceededOnce then
                { State = state; Action = Action.NoAction }
            else
                let (WallClockMs nowWallMs) = nowWall
                let stamps, restartDue =
                    requestWedgeRestart (nowWallMs, config.RecoveryCooldownMs, state.Stamps, true)
                let updated = { state with Stamps = stamps } |> withRecomputedStatus (config, now)
                let action = if restartDue then Action.Restart RestartReason.CameraWedged else Action.NoAction
                { State = updated; Action = action }
        | Event.BetterCameraAvailable ->
            // RFC addendum 2026-08-01 (slice 10: camera-arrival upgrade). Elective restart:
            // deliberately not gated by IsPaused/IsSessionLocked, for the same one-coherent-
            // rule-beats-three-special-cases reasoning as the other restart-requesting events
            // (R2-10) -- pause persists across the restart, and an elective restart while
            // paused/locked is harmless. Pre-first-success this is a no-op: the acquisition
            // retry loop already re-runs camera selection on every attempt, so a restart here
            // would be pure waste (mirrors CaptureFailed's pre-success no-op, which is real
            // here too since a device-arrival check only ever runs once a camera is in use).
            // Single call site for CameraUpgrade, so the cooldown/stamp handling is inline
            // via cooldownElapsed -- the same idiom requestWedgeRestart factors out for the
            // two CameraWedged call sites -- mirroring ReevalAt's inline handling above rather
            // than introducing a shared helper for a reason with only one producer.
            if not state.HasSucceededOnce then
                { State = state; Action = Action.NoAction }
            else
                let (WallClockMs nowWallMs) = nowWall
                let upgradeDue = cooldownElapsed (nowWallMs, config.UpgradeCooldownMs, state.Stamps.UpgradeAt)
                let stamps =
                    if upgradeDue then
                        { state.Stamps with UpgradeAt = System.Nullable(nowWallMs) }
                    else
                        state.Stamps
                let updated = { state with Stamps = stamps } |> withRecomputedStatus (config, now)
                let action = if upgradeDue then Action.Restart RestartReason.CameraUpgrade else Action.NoAction
                { State = updated; Action = action }

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
          LastReevalRestartAt = state.Stamps.ReevalAt
          LastUpgradeRestartAt = state.Stamps.UpgradeAt }
