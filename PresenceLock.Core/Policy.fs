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

    /// Placeholder for slice 2: no `Event` yet drives a real transition. `step` is total —
    /// it terminates and returns without throwing for any input — but every case here is an
    /// identity no-op until slices 3-6 wire in arming, away/grace/idle gating, signal
    /// accounting, session/pause re-baselining, and recovery policy against these same fields.
    let step
        (_config: PolicyConfig, state: State, _now: MonotonicMs, _nowWall: WallClockMs, _event: Event)
        : StepResult =
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
