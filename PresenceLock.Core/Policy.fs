namespace PresenceLock.Core

/// The decision core: pure functions from `(PolicyConfig, State, time, Event)` to a new
/// `State` plus a single `Action`. No clocks, no I/O — every `now`/`nowWall` is a caller-
/// supplied parameter (0001-core-brain.md, "Time semantics").
module Policy =

    /// Called exactly once per process, in the shell's `WatcherContext` constructor, before
    /// the first camera-acquisition attempt. All later re-baselining goes through events into
    /// the same `State` — never a second `start` call. Takes the initial `StepInputs` the
    /// shell sampled before acquisition begins (WTS query + pause consume-and-clear complete
    /// first — restart pause-inheritance becomes ordinary input passing). `LastInputs` is
    /// initialized to `inputs` VERBATIM — pinned, not left to inference (RFC 0002-environment-
    /// levels): the suppression-edge rule's first-call correctness depends on it, since any
    /// other initial value (a default reads "unsuppressed") would make `suppressed
    /// LastInputs` false on a restart-while-locked process and silently skip the baseline
    /// reset on the first real unlock it observes. The initial `CachedStatus` is likewise
    /// computed from `inputs` directly (rows 1-2 of the priority table) rather than via
    /// `computeStatus`: pre-first-event, `InitFailStreak` is always 0 and `HasSucceededOnce`
    /// is always false, so rows 3/5/6 can never apply and no `PolicyConfig` is needed. A
    /// restart-while-paused process therefore reports `Paused` from its very first render,
    /// never a one-frame `AcquiringCamera` flash. Takes no `PolicyConfig` otherwise: `start`
    /// only stamps the initial baseline and imports the persisted stamps; every threshold is
    /// compared live at `step`/`snapshot` time, so a config parameter here would be dead — and
    /// ambiguous against `step`'s.
    let start (now: MonotonicMs, stamps: RestartStamps, inputs: StepInputs) : State =
        let initialStatus =
            if inputs.Paused then Status.Paused
            elif inputs.SessionLocked then Status.SessionLocked
            else Status.AcquiringCamera
        { HasSucceededOnce = false
          InitFailStreak = 0
          Armed = false
          LastInputs = inputs
          GraceBaselineAt = now
          AwayBaselineAt = now
          BadSignalSince = None
          Stamps = stamps
          CachedStatus = initialStatus }

    /// Total, priority-ordered `State -> Status` projection (0001-core-brain.md, the Status
    /// priority table in "Notes"; rows 1-2 read `state.LastInputs` per RFC 0002-environment-
    /// levels, "Status"). `PolicyConfig`/`now` are needed only for row 5 (`NoSignal`): whether
    /// the current continuous bad-signal run has crossed `NoSignalReportAfterMs` as of this
    /// step's `now`. Rows 1-4 are pure `State` predicates. Called only from `step`'s tail
    /// (after `LastInputs` has already been overwritten with the current call's inputs) and
    /// from `start` (pre-first-event, where rows 3/5/6 can never apply since `InitFailStreak`
    /// is always 0 and `HasSucceededOnce` is always false). Cached in `State.CachedStatus` —
    /// see the module-level doc on `status`.
    let private computeStatus (config: PolicyConfig, state: State, now: MonotonicMs) : Status =
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

    /// Recomputes and caches `Status` against the state as of this `step`'s `now` — every call
    /// ends with this, since `Status`'s `NoSignal` row (row 5) depends on `now` even when no
    /// other field changed (slice 4).
    let private withRecomputedStatus (config: PolicyConfig, now: MonotonicMs) (state: State) : State =
        { state with CachedStatus = computeStatus (config, state, now) }

    /// `suppressed inputs = inputs.SessionLocked || inputs.Paused` (RFC 0002-environment-
    /// levels, "Suppression and the single edge rule") — the entire former truth table
    /// collapses into this one predicate plus the edge rule in `step`.
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

    /// A baseline reset shared by `InitSucceeded` and the suppressed -> unsuppressed edge rule
    /// in `step`: disarm, re-baseline grace/away from `now`, and reset the signal-health clock
    /// (0001-core-brain.md, R2-20) — parity with `ResumeSampling()`'s `noSignalStreak = 0`.
    let private applyBaselineReset (now: MonotonicMs) (state: State) : State =
        { state with
            Armed = false
            GraceBaselineAt = now
            AwayBaselineAt = now
            BadSignalSince = None }

    /// Per-event match (RFC 0002-environment-levels, "Suppression and the single edge rule").
    /// Fully suppression-blind: never reads `LastInputs`/suppression and never writes
    /// `LastInputs`/`CachedStatus` — both are wrapper-owned by `step`'s single tail, which is
    /// thereby the only writer of `LastInputs` and the only caller of status computation.
    /// Idle is read from `ctx.Inputs.InputIdleMs`, never from an event payload. Failure/
    /// restart events (`InitFailed`, `CaptureFailed`, `BetterCameraAvailable`) were already
    /// pause-immune (0001-core-brain.md R2-10), i.e. already suppression-blind, so they carry
    /// over unchanged.
    let private dispatch (config: PolicyConfig, state: State, ctx: StepContext, event: Event) : StepResult =
        let now = ctx.Now
        let nowWall = ctx.NowWall
        match event with
        | Event.Sample Observation.FaceSeen ->
            let updated =
                { state with
                    Armed = true
                    AwayBaselineAt = now
                    BadSignalSince = None }
            { State = updated; Action = Action.NoAction }
        | Event.Sample Observation.NoFace ->
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
        | Event.Sample(Observation.NoFrame | Observation.DarkFrame) ->
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
        | Event.Reconcile ->
            // No decision-state change, no Action -- the wrapper's edge rule plus tail is its
            // entire effect (Event.Reconcile's doc comment in Types.fs); never emits Lock,
            // since a lock decision needs a current observation and Reconcile carries none.
            { State = state; Action = Action.NoAction }

    /// A thin wrapper composing (1) suppression-edge derivation and baseline reset, (2) the
    /// while-suppressed `Sample` no-op (hoisted out of the per-event match so `dispatch` is
    /// fully suppression-blind), (3) suppression-blind `dispatch`, (4) a single tail that is
    /// the only writer of `LastInputs` and the only caller of status computation (RFC
    /// 0002-environment-levels, "Suppression and the single edge rule"). This is what makes
    /// the edge rule sound: every call -- every event, suppressed or not, no-op or not -- ends
    /// by recording the current inputs into `LastInputs` and recomputing `CachedStatus`, so an
    /// edge can be delayed by at most one call, never lost, because both sides of the
    /// `suppressed` comparison are re-supplied fresh on every call.
    let step (config: PolicyConfig, state: State, ctx: StepContext, event: Event) : StepResult =
        let edge = suppressed state.LastInputs && not (suppressed ctx.Inputs)
        let state' = if edge then applyBaselineReset ctx.Now state else state
        let result =
            match event with
            | Event.Sample _ when suppressed ctx.Inputs -> { State = state'; Action = Action.NoAction }
            | _ -> dispatch (config, state', ctx, event)
        { result with
            State =
                { result.State with LastInputs = ctx.Inputs }
                |> withRecomputedStatus (config, ctx.Now) }

    /// = `LastInputs.LockInhibited` as of the most recent `step` call; cached like `Status`
    /// (RFC 0002-environment-levels, "Status"). The fire-time lock GATE itself is a later
    /// slice -- this slice only carries and projects the field, exactly as pinned:
    /// `LockInhibited` never influences any decision here.
    let lockInhibited (state: State) : bool = state.LastInputs.LockInhibited

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
