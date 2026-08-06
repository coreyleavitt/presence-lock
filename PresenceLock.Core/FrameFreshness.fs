namespace PresenceLock.Core

/// Frozen-frame staleness detection (bug fix note: `TryAcquireLatestFrame` can re-serve a
/// cached frame forever -- a known WinRT quirk, Program.cs's frame-acquisition comment). A
/// non-advancing `SystemRelativeTime` used to be logged (a parallel 10-consecutive-sample
/// counter) but never changed the decision path, so a stale frame's stale `FaceSeen` could
/// reset the away clock indefinitely while `Status` still showed `Watching`. This module is
/// the authoritative freshness signal the shell now gates classification on: time-based, not
/// count-based, for the identical R1-27 reason `PresenceFilter`'s redesign cites -- coupling
/// staleness to a sample count ties its real-world meaning to `SampleIntervalMs`. A pure
/// sibling of `PresenceFilter`/`Policy`, not inside either: it filters *sensing* (is this
/// frame's timestamp still moving), not decision.

/// Tunables for `FrameFreshness.step`. `Default` treats 5 continuous seconds of a frozen
/// timestamp as conclusive evidence of a stuck pipe -- long enough that an actually-live but
/// momentarily-idle capture (a real static scene, whose frames still carry advancing
/// timestamps even when their pixel content doesn't change) is never mistaken for stale.
type FreshnessConfig =
    { StaleAfterMs: int64 }

    static member Default: FreshnessConfig = { StaleAfterMs = 5000L }

/// Opaque freshness state, carried by the shell exactly like `FilterState`/`Policy.State`:
/// constructed only by `FrameFreshness.initial`, threaded through `step`, never pattern-
/// matched or hand-rolled outside this assembly. `LastTimestamp` is opaque (frame-source clock
/// ticks, `MediaFrameReference.SystemRelativeTime.Ticks` in the shell) -- this module only ever
/// compares it for equality, never interprets its value or unit. Always `Some`/`None` in
/// lockstep with `LastAdvanceAt`: both are set together whenever the timestamp advances, and
/// both stay untouched (not cleared) while it doesn't.
type FreshnessState =
    internal
        { LastTimestamp: int64 option
          LastAdvanceAt: MonotonicMs option }

/// Named struct result (see `StepResult`'s rationale in Types.fs): no per-sample tuple
/// allocation on the sampling-timer hot path, and C# reads `.State`/`.IsFresh` instead of
/// `.Item1`/`.Item2`.
[<Struct>]
type FreshnessResult =
    { State: FreshnessState
      IsFresh: bool }

/// Pure per-frame freshness tracker, sibling to `PresenceFilter`/`Policy` (see the module-level
/// rationale above). No clocks, no I/O: `step` is a pure function of the previous
/// `FreshnessState` and this frame's timestamp.
module FrameFreshness =

    /// The state before any frame has been observed -- reset everywhere `PresenceFilter.initial`
    /// is (fresh camera acquisition, and every baseline-reset point the shell resets the
    /// presence filter at): a fresh acquisition's or watching episode's first frame must not be
    /// judged stale against a previous acquisition's or episode's last-seen timestamp.
    let initial: FreshnessState = { LastTimestamp = None; LastAdvanceAt = None }

    /// Advances the freshness tracker by one frame. `now` is the shell's monotonic clock at
    /// this sample (same clock domain as `PresenceFilter.step`/`Policy.step`). `frameTimestamp`
    /// is the frame source's own opaque timestamp ticks for this frame.
    ///
    /// A timestamp that differs from the last-seen one is always fresh (and becomes the new
    /// baseline both fields advance from). An identical timestamp is fresh only while less than
    /// `StaleAfterMs` has elapsed since it was last seen to advance -- stale at exactly
    /// `StaleAfterMs` (`>=`, matching `Policy`'s own convention that `>=` marks the threshold-
    /// crossed/"bad" side, e.g. `NoSignalReportAfterMs`).
    let step (config: FreshnessConfig, state: FreshnessState, now: MonotonicMs, frameTimestamp: int64) : FreshnessResult =
        let advanced =
            match state.LastTimestamp with
            | Some last -> last <> frameTimestamp
            | None -> true
        if advanced then
            { State = { LastTimestamp = Some frameTimestamp; LastAdvanceAt = Some now }
              IsFresh = true }
        else
            let (MonotonicMs nowMs) = now
            // `LastAdvanceAt` is always `Some` here: `advanced` is false only when
            // `state.LastTimestamp` was already `Some`, which -- by the invariant on
            // `FreshnessState` -- means `state.LastAdvanceAt` is `Some` too.
            let (MonotonicMs lastAdvanceMs) = state.LastAdvanceAt.Value
            { State = state
              IsFresh = nowMs - lastAdvanceMs < config.StaleAfterMs }
