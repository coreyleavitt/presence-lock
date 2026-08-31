namespace PresenceLock.Core

open System

/// The full public type surface of the Core decision boundary (0001-core-brain.md, "Types").
/// `PresenceLock.Core` is pure decision logic: no clocks, no I/O, no mutation visible to
/// callers. Time is always a parameter — see the two clock-domain wrappers immediately below.

/// Monotonic milliseconds, shell-supplied via `Environment.TickCount64`. Drives grace/away/
/// idle/streak timing within `step`. `TickCount64` is boot-relative, not process-relative: it
/// is continuous across the plain process restarts this design performs routinely, so `now`
/// before and after an `Action.Restart` are directly comparable, and resets near zero only on
/// an actual OS reboot. A real CLR struct type (not a `[<Measure>]` unit, which would erase to
/// a bare `int64` exactly at the C# boundary where the transposition risk lives) — so passing
/// a `WallClockMs` where `MonotonicMs` is expected is a compile error in both languages, never
/// a latent unit bug. Never compared to `WallClockMs`.
[<Struct>]
type MonotonicMs = MonotonicMs of int64

/// Wall-clock milliseconds since the Unix epoch
/// (`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`), used exclusively for the restart-
/// cooldown gates (`RecoveryCooldownMs` / `ReevaluateCooldownMs`), because those gates alone
/// must remain meaningful across a reboot — a genuinely wedged/broken camera doesn't get
/// better after a reboot, and the whole point of the cooldown is to survive exactly that.
/// Never compared to `MonotonicMs`.
[<Struct>]
type WallClockMs = WallClockMs of int64

/// Tunable policy thresholds, read live on every `step` (see "Live config" in the RFC): the
/// baselines stored in `State` are timestamps, and thresholds are compared against them at
/// step/snapshot time, so a value changed in Settings mid-grace or mid-away-countdown applies
/// on the very next sample without discarding accumulated state.
type PolicyConfig =
    { AwayThresholdMs: int64
      InputIdleRequiredMs: int64
      GraceMs: int64
      /// Continuous bad signal (NoFrame/DarkFrame) before surfacing `Status.NoSignal`.
      NoSignalReportAfterMs: int64
      /// Continuous bad signal before requesting a `CameraReevaluation` restart.
      ReevaluateAfterMs: int64
      /// Minimum wall-clock gap between `CameraReevaluation` restarts.
      ReevaluateCooldownMs: int64
      /// Consecutive pre-success `InitFailed` events before requesting a recovery restart.
      RecoveryFailureThreshold: int
      /// Minimum wall-clock gap between `CameraWedged` restarts.
      RecoveryCooldownMs: int64
      /// Minimum wall-clock gap between `CameraUpgrade` restarts (RFC addendum 2026-08-01,
      /// slice 10: camera-arrival upgrade). Ninth and final field of the validation unit.
      UpgradeCooldownMs: int64 }

[<RequireQualifiedAccess>]
type Observation =
    | FaceSeen
    | NoFace
    | NoFrame
    | DarkFrame

[<RequireQualifiedAccess>]
type Event =
    | Sample of Observation
    /// RFC 0002 (environment-levels): "nothing happened, except the levels may have
    /// changed." Payload-free — the shell fires it synchronously whenever any `StepInputs`
    /// mirror changes (pause toggle, session switch, WTS reconciliation correction,
    /// inhibitor-aggregate transition), so edge derivation and status recomputation run
    /// promptly even while the sample timer is stopped (suppressed). Never emits
    /// `Action.Lock`: a lock decision needs a current observation, and `Reconcile` carries
    /// none.
    | Reconcile
    | InitSucceeded
    /// Startup/retry failure, before any `InitSucceeded` this process. Streak-gated by
    /// `RecoveryFailureThreshold` — see "Notes: recovery boundary" in the RFC.
    | InitFailed of handleInvalid: bool
    /// A previously-live capture died mid-session (shell's `MediaCapture.Failed`). Carries no
    /// payload: post-success, the restart decision no longer depends on the failure's
    /// classification — see "Notes: recovery boundary."
    | CaptureFailed
    /// A preferred camera became available post-acquisition (RFC addendum 2026-08-01, slice
    /// 10: camera-arrival upgrade) — fed by the shell after device-arrival settle + a
    /// preference check finds the would-pick-now camera differs from the in-use one. Carries
    /// no payload: the decision is cooldown-gated only, with no classification to gate on —
    /// see "Notes: recovery boundary"'s `CaptureFailed` for the identical rationale shape.
    | BetterCameraAvailable

/// Semantic status; the shell renders strings from it (pulled, never pushed) — see
/// `Policy.status`. Total and priority-ordered over `State` — see the priority table in the
/// RFC's "Notes" section.
[<RequireQualifiedAccess>]
type Status =
    | Watching
    | NoSignal
    | SessionLocked
    | Paused
    | AcquiringCamera
    | Recovering

[<RequireQualifiedAccess>]
type RestartReason =
    | CameraWedged
    | CameraReevaluation
    /// RFC addendum 2026-08-01, slice 10: a preferred camera arrived and the shell's ranking
    /// function would now pick differently than the in-use camera.
    | CameraUpgrade

/// Exactly one `Action` per step — cardinality is enforced by the type, not by convention.
[<RequireQualifiedAccess>]
type Action =
    | NoAction
    | Lock
    /// Process restart: recovery OR camera re-evaluation, decided by the core, executed by
    /// the shell (`RestartProcess()`). In-process re-init is never an option — see Motivation.
    | Restart of reason: RestartReason

/// Persisted wall-clock (Unix-epoch ms) restart stamps, one per `RestartReason`, carried
/// across process restarts by the shell. `Nullable` (not `option`) deliberately: this type is
/// constructed at the C# boundary (`RestartStamps.WedgeAt`/`.ReevalAt`/`.UpgradeAt` set from a
/// JSON stamps file) and never compared against the monotonic clock. A `Map<RestartReason,
/// int64>` shape indexed by the DU was considered and deferred at two reasons (R2-40); slice
/// 10 (RFC addendum 2026-08-01) adds the third, previously-named candidate (`CameraUpgrade`),
/// confirming named `Nullable` fields remain the right shape at three — the flat-json schema
/// stays backward compatible (an absent key loads as null, exactly like the first two fields
/// did before any restart of that reason had ever fired).
type RestartStamps =
    { WedgeAt: Nullable<int64>
      ReevalAt: Nullable<int64>
      UpgradeAt: Nullable<int64> }

/// Sampled environment truth (RFC 0002-environment-levels), passed on EVERY `Policy.step`/
/// `Policy.start` call — never folded from events, so it can never silently desync from
/// what the shell actually observes (the incident this RFC's Motivation traces to a folded
/// `IsSessionLocked`/`IsPaused` belief that one swallowed edge diverges permanently).
/// Deliberately a plain reference record, NOT `[<Struct>]`: as a struct,
/// `default(StepInputs)` reads as the *dangerous* value (unsuppressed, uninhibited,
/// idle) and is reachable through zero-init paths no call-site discipline can guard
/// (`Array.zeroCreate`, a dictionary miss, `Unchecked.defaultof`, an uninitialized field); as
/// a reference type, every such path yields `null` and fails loud with an NRE on first field
/// access instead of reading as plausible truth.
type StepInputs =
    { SessionLocked: bool // OS fact: shell mirrors SessionSwitch, reconciled via WTS query
      Paused: bool // user intent: the shell owns the tray toggle and its persistence
      LockInhibited: bool // true iff any registered shell-side inhibitor is active
      InputIdleMs: int64 } // OS fact: GetLastInputInfo, queried fresh on every Advance

/// Everything about "the world right now" that isn't config, state, or the event being
/// processed, bundled into one record rather than adjacent positional parameters — the same
/// call-site transposition-hazard reasoning behind `MonotonicMs`/`WallClockMs`, one level up.
/// `Event` stays a separate trailing parameter to `Policy.step`: the *reason* the call
/// happened, categorically different from ambient context.
[<Struct>]
type StepContext =
    { Now: MonotonicMs
      NowWall: WallClockMs
      Inputs: StepInputs }

/// Opaque decision state. The type itself is public — the shell holds and threads a `State`
/// value through its `Advance` chokepoint — but its representation is `internal`: invisible
/// outside this assembly, so the shell can never construct, pattern-match, or hand-roll one.
/// `Policy.snapshot` is the only sanctioned window in.
///
/// Sketches its full final shape now — the fields needed through slice 6's recovery policy —
/// even though slice 2 exercises only the baseline subset (`HasSucceededOnce` = false,
/// `InitFailStreak` = 0, `Armed` = false, baselines = `start`'s `now`, no bad signal, stamps
/// carried through unchanged). Later slices fill in `step`'s logic against these fields, not
/// shape churn (0001-core-brain.md finding R2-33).
type State =
    internal
        { /// Whether `InitSucceeded` has ever fired this process — gates `Status.AcquiringCamera`
          /// / `Status.Recovering` (pre-success only) vs. the post-success `Status.NoSignal`
          /// backstop for a dead camera.
          HasSucceededOnce: bool
          /// Consecutive pre-success `InitFailed` events since `start` (or since the last
          /// `InitSucceeded`, which cannot recur this process). Drives `Status.Recovering`
          /// and, from slice 6, the `RecoveryFailureThreshold` gate.
          InitFailStreak: int
          /// Whether a `FaceSeen` has occurred since the last baseline event (`start`,
          /// `InitSucceeded`, or the suppressed -> unsuppressed edge).
          Armed: bool
          /// The previous `step`/`start` call's sampled environment truth, for suppression-
          /// edge derivation and status computation (RFC 0002-environment-levels). Lives
          /// inside opaque `State` — not a shell-held (previous, current) pair — so it has
          /// exactly one writer (`step`'s tail) and can never desync from what `step` last
          /// saw. `start` initializes it to the initial `StepInputs` verbatim.
          LastInputs: StepInputs
          /// Timestamp grace is measured live against — reset only by baseline events, never
          /// by an ordinary `Sample`.
          GraceBaselineAt: MonotonicMs
          /// Timestamp the away threshold is measured live against — reset by `FaceSeen`,
          /// `NoFrame`, and `DarkFrame` alike (the fail-open rule), and by every baseline event.
          AwayBaselineAt: MonotonicMs
          /// Start of the current continuous bad-signal (`NoFrame`/`DarkFrame`) run; `None`
          /// while signal is healthy. Reset by any healthy sample and by every baseline event.
          BadSignalSince: MonotonicMs option
          /// Restart stamps, carried from `start` and updated in place whenever `step` requests
          /// an `Action.Restart` — the same values `Policy.snapshot` surfaces verbatim.
          Stamps: RestartStamps
          /// `Status` as of the most recent `step` (or `start`, pre-first-event) — cached here
          /// so `Policy.status` is a plain projection rather than a recomputation; see the
          /// module-level doc on `status`.
          CachedStatus: Status }

/// Test/diagnostic projection — never used by the shell for control flow. Exists because the
/// test plan (baseline-is-unarmed-and-in-grace; "no Lock unless armed") requires observing
/// facts an opaque `State` can't otherwise expose, and because the shell logs it alongside
/// every `Lock`/`Restart` action so today's diagnostic-rich log lines survive the extraction.
/// Unlike `Status`, every field here is computed live at projection time from `PolicyConfig`
/// and `State`'s stored baselines — never latched — so `InGrace`/`AwayForMs` always reflect
/// the caller's `now`, not the `now` of the most recent `step`.
type Snapshot =
    { Armed: bool
      InGrace: bool
      /// Now minus the last away-baseline reset.
      AwayForMs: int64
      /// 0 while signal is healthy.
      NoSignalForMs: int64
      InitFailStreak: int
      /// Nullable, not option — same C#-boundary rationale as `RestartStamps`: the shell's
      /// logging code consumes `Snapshot` directly and must never touch `FSharpOption`.
      LastWedgeRestartAt: Nullable<int64>
      LastReevalRestartAt: Nullable<int64>
      LastUpgradeRestartAt: Nullable<int64> }

/// Named struct rather than a positional `State * Action` tuple: no per-sample tuple
/// allocation on the hot path, and C# reads `.State`/`.Action` instead of `.Item1`/`.Item2`.
[<Struct>]
type StepResult = { State: State; Action: Action }
