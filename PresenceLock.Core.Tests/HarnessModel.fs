/// RFC 0002-environment-levels, "Verification harness (in PresenceLock.Core.Tests)". A
/// test-side environment model plus an exhaustive BFS explorer over the reachable
/// (env x abstracted-core-state) graph. See HarnessTests.fs for the properties/replay/mutant
/// tests built on top of this module.
module PresenceLock.Core.Tests.HarnessModel

open System.Collections.Generic
open PresenceLock.Core

// --------------------------------------------------------------------------------------------
// The seam (RFC-pinned verbatim): the explorer is generic over an auxiliary per-implementation
// state carried alongside the real, opaque `State` -- required because the folded-flag mutant's
// defining state (IsPaused/IsSessionLocked booleans) no longer exists in `State` at all, so
// parameterizing over the step *function* alone could never express it. `State`'s representation
// is `internal` to PresenceLock.Core (invisible to this assembly), so neither the real
// implementation nor any mutant can construct one from scratch -- every `StepUnderTest<'aux>`
// must delegate to the real `Policy.start`/`Policy.step` for the `State` component. A mutant's
// only lever is therefore what it FEEDS those functions (and what it remembers in `'aux`), never
// a from-scratch reimplementation of decision logic -- see the two mutants below.
// --------------------------------------------------------------------------------------------
type StepUnderTest<'aux> =
    { Init: MonotonicMs -> RestartStamps -> StepInputs -> State * 'aux
      Step: PolicyConfig -> State * 'aux -> StepContext -> Event -> StepResult * 'aux }

/// The real, unmodified core under test.
let realStepUnderTest: StepUnderTest<unit> =
    { Init = fun now stamps inputs -> (Policy.start (now, stamps, inputs), ())
      Step = fun config (state, aux) ctx event -> (Policy.step (config, state, ctx, event), aux) }

// --------------------------------------------------------------------------------------------
// Environment model: ground truth + a derived input-idle clock the model owns (StepInputs.
// InputIdleMs is not part of Policy's own State -- the real shell derives it fresh from
// GetLastInputInfo on every Advance, so the model owns an equivalent OS-external clock).
// --------------------------------------------------------------------------------------------

/// Ground truth (RFC "Verification harness"). `MediaPlaying` feeds `StepInputs.LockInhibited`
/// and is a live axis from slice 3 onward: `ToggleMediaPlaying` in the action alphabet below
/// flips it, firing `Reconcile` synchronously at the transition (same fidelity rule as every
/// other level change), so the explored graph now covers the inhibited/uninhibited product of
/// every other reachable state, not merely the uninhibited-only graph slice 2 explored.
type EnvTruth =
    { OsLocked: bool
      Paused: bool
      CameraAlive: bool
      MediaPlaying: bool
      FacePresent: bool
      /// True iff the environment currently holds "no real input activity" -- the discrete gate
      /// an explicit action flips. The actual elapsed idle-ms fed to `StepInputs.InputIdleMs`
      /// (`Env.IdleSinceMs` below) is a clock that runs only while this gate holds, exactly
      /// paralleling how `CameraAlive` (ground truth) drives `NoFrame` observations while
      /// Policy's own `BadSignalSince` is the derived clock.
      InputIdle: bool }

let private suppressedTruth (t: EnvTruth) : bool = t.OsLocked || t.Paused

/// One (env, abstracted-core-state) world node. `Now`/`NowWall` back `MonotonicMs`/`WallClockMs`
/// and are advanced together -- decoupling them would only matter for restart-cooldown
/// scenarios, which the harness's fixed recovery axes (see `initialWorld`) make unreachable.
type Env =
    { Truth: EnvTruth
      Now: int64
      NowWall: int64
      /// `Some t` = `InputIdle` has held continuously since monotonic time `t`; `None` = active
      /// (idle-ms reads 0). Mirrors `State.BadSignalSince`'s own latch-and-clear shape.
      IdleSinceMs: int64 option }

let idleMsOf (env: Env) : int64 =
    match env.IdleSinceMs with
    | Some since -> env.Now - since
    | None -> 0L

/// `StepInputs` as the real shell would build them from these levels this instant.
let stepInputsOf (env: Env) : StepInputs =
    { SessionLocked = env.Truth.OsLocked
      Paused = env.Truth.Paused
      LockInhibited = env.Truth.MediaPlaying
      InputIdleMs = idleMsOf env }

let ctxOf (env: Env) (inputs: StepInputs) : StepContext =
    { Now = MonotonicMs env.Now; NowWall = WallClockMs env.NowWall; Inputs = inputs }

/// The `Observation` a `Sample` fired at this instant would carry -- `DarkFrame` is not modeled
/// separately from `NoFrame`: `Policy.dispatch` handles `NoFrame`/`DarkFrame` identically
/// (`Event.Sample(Observation.NoFrame | Observation.DarkFrame)`), so modeling only one loses no
/// decision-relevant coverage.
let private observationOf (t: EnvTruth) : Observation =
    if not t.CameraAlive then Observation.NoFrame
    elif t.FacePresent then Observation.FaceSeen
    else Observation.NoFace

/// World: the env plus the abstracted-implementation-under-test's own state (real `Policy.State`
/// plus whatever `'aux` its `StepUnderTest` carries).
type World<'aux> =
    { Env: Env
      Core: State
      Aux: 'aux }

// --------------------------------------------------------------------------------------------
// Legal-action alphabet (RFC-pinned: OsLock/OsUnlock alternate; pause toggle requires an
// unlocked session -- the tray is unreachable through a lock screen; Tick advances time and
// fires Sample exactly when the modeled shell would). CameraAlive/FacePresent/InputIdle toggles
// are ground-truth-only actions: none of the three is a StepInputs "mirror" level the real shell
// fires a dedicated Reconcile for (only SessionLocked/Paused/LockInhibited are), so they update
// Env without ever calling into the step under test -- their effect surfaces only at the next
// Tick-driven Sample (or, for InputIdle, at whatever call next reads `stepInputsOf`).
// `ToggleMediaPlaying` (slice 3) IS a mirror action, unlike its ground-truth-only siblings above:
// it feeds `StepInputs.LockInhibited` directly (`stepInputsOf`), and the real shell's inhibitor
// registry fires `Advance(Reconcile)` synchronously on every aggregate transition -- unlike a
// pause toggle it carries no lock-screen precondition, since SMTC/media state is independent of
// session state, so it is legal from every truth.
// --------------------------------------------------------------------------------------------
type EnvAction =
    | OsLock
    | OsUnlock
    | TogglePause
    | ToggleMediaPlaying
    | ToggleCameraAlive
    | ToggleFacePresent
    | ToggleInputIdle
    | Tick of int64

/// Model fidelity (RFC): every level-changing action that IS a StepInputs mirror (OsLock,
/// OsUnlock, pause toggle, media toggle) calls the step under test with `Reconcile` synchronously
/// at the transition, the same `Advance(Reconcile)` contract the real shell implements.
let private applyMirrorChange
    (sut: StepUnderTest<'aux>)
    (config: PolicyConfig)
    (world: World<'aux>)
    (mutateTruth: EnvTruth -> EnvTruth)
    : World<'aux> * Action =
    let env' = { world.Env with Truth = mutateTruth world.Env.Truth }
    let ctx = ctxOf env' (stepInputsOf env')
    let result, aux' = sut.Step config (world.Core, world.Aux) ctx Event.Reconcile
    { Env = env'; Core = result.State; Aux = aux' }, result.Action

let private applyGroundTruthOnly (world: World<'aux>) (mutateTruth: EnvTruth -> EnvTruth) : World<'aux> * Action =
    { world with Env = { world.Env with Truth = mutateTruth world.Env.Truth } }, Action.NoAction

let private applyToggleInputIdle (world: World<'aux>) : World<'aux> * Action =
    let t = world.Env.Truth
    let env' =
        if t.InputIdle then
            { world.Env with Truth = { t with InputIdle = false }; IdleSinceMs = None }
        else
            { world.Env with Truth = { t with InputIdle = true }; IdleSinceMs = Some world.Env.Now }
    { world with Env = env' }, Action.NoAction

/// Closes the loop on `Action.Restart` (RFC: "`apply`'s `Restart` handling exists for totality
/// but is unreachable in the explored graph" -- the harness's fixed recovery axes and
/// beyond-horizon cooldowns keep `ReevaluateAfterMs`/etc. from ever firing a restart in this
/// slice's exploration). Simulates the documented process handoff: a fresh `Init` with carried
/// stamps (reconstructed from the outgoing `Snapshot`, the only public window onto them) and
/// current levels.
let private applyRestart
    (sut: StepUnderTest<'aux>)
    (config: PolicyConfig)
    (env: Env)
    (coreBeforeRestart: State)
    : World<'aux> =
    let snap = Policy.snapshot (config, coreBeforeRestart, MonotonicMs env.Now)
    let stamps: RestartStamps =
        { WedgeAt = snap.LastWedgeRestartAt
          ReevalAt = snap.LastReevalRestartAt
          UpgradeAt = snap.LastUpgradeRestartAt }
    let state', aux' = sut.Init (MonotonicMs env.Now) stamps (stepInputsOf env)
    { Env = env; Core = state'; Aux = aux' }

/// `Tick`: advances monotonic/wall time, then fires `Sample` iff the modeled shell would (i.e.
/// iff not currently suppressed -- the real sample timer is stopped while suppressed, so no
/// `step` call of any kind happens during that window; fidelity the folded-flag/stale-LastInputs
/// mutants both depend on). `apply` closes the loop on the resulting `Action` (RFC: "`Lock` sets
/// `OsLocked` and is reflected in the next inputs") via the identical mirror-change mechanics an
/// explorer-driven `OsLock` would use -- from Policy's perspective an environment-driven lock and
/// an explored `OsLock` action are indistinguishable inputs.
let private applyTick
    (sut: StepUnderTest<'aux>)
    (config: PolicyConfig)
    (world: World<'aux>)
    (deltaMs: int64)
    : World<'aux> * Action =
    let env' =
        { world.Env with
            Now = world.Env.Now + deltaMs
            NowWall = world.Env.NowWall + deltaMs }
    if suppressedTruth env'.Truth then
        { world with Env = env' }, Action.NoAction
    else
        let inputs = stepInputsOf env'
        let ctx = ctxOf env' inputs
        let result, aux' = sut.Step config (world.Core, world.Aux) ctx (Event.Sample(observationOf env'.Truth))
        let world' = { Env = env'; Core = result.State; Aux = aux' }
        match result.Action with
        | Action.Lock ->
            let world'', _ = applyMirrorChange sut config world' (fun t -> { t with OsLocked = true })
            world'', Action.Lock
        | Action.Restart _ -> applyRestart sut config env' result.State, result.Action
        | Action.NoAction -> world', Action.NoAction

/// Applies one legal `EnvAction`; `None` iff the action is illegal from `world`'s current truth
/// (OsLock while already locked, OsUnlock while already unlocked, a pause toggle while locked).
let apply (sut: StepUnderTest<'aux>) (config: PolicyConfig) (world: World<'aux>) (action: EnvAction) : (World<'aux> * Action) option =
    let t = world.Env.Truth
    match action with
    | OsLock when not t.OsLocked -> Some(applyMirrorChange sut config world (fun t -> { t with OsLocked = true }))
    | OsLock -> None
    | OsUnlock when t.OsLocked -> Some(applyMirrorChange sut config world (fun t -> { t with OsLocked = false }))
    | OsUnlock -> None
    | TogglePause when not t.OsLocked -> Some(applyMirrorChange sut config world (fun t -> { t with Paused = not t.Paused }))
    | TogglePause -> None
    | ToggleMediaPlaying -> Some(applyMirrorChange sut config world (fun t -> { t with MediaPlaying = not t.MediaPlaying }))
    | ToggleCameraAlive -> Some(applyGroundTruthOnly world (fun t -> { t with CameraAlive = not t.CameraAlive }))
    | ToggleFacePresent -> Some(applyGroundTruthOnly world (fun t -> { t with FacePresent = not t.FacePresent }))
    | ToggleInputIdle -> Some(applyToggleInputIdle world)
    | Tick deltaMs -> Some(applyTick sut config world deltaMs)

/// Every legal action from the given truth (deltas fixed per exploration -- see `tickDeltas`).
let legalActions (t: EnvTruth) (deltas: int64 list) : EnvAction list =
    [ if not t.OsLocked then
          yield OsLock
      if t.OsLocked then
          yield OsUnlock
      if not t.OsLocked then
          yield TogglePause
      yield ToggleMediaPlaying
      yield ToggleCameraAlive
      yield ToggleFacePresent
      yield ToggleInputIdle
      for d in deltas do
          yield Tick d ]

// --------------------------------------------------------------------------------------------
// Tick delta set: boundary-value style, derived from Cadence plus the model config (RFC
// pinning: "for each modeled threshold, one delta landing just below it and one at or past it
// -- so delta granularity and bucket boundaries agree by construction").
//
// Deliberately NOT included: a boundary pair derived from `Cadence.WatchdogIntervalMs`. `Tick`
// advances the model's core `step` time and fires a `Sample` at the deltas above (`applyTick`) --
// it models the sample-timer cadence, never the shell's watchdog poll loop. The watchdog tick
// (`WatchdogTickStep` in Program.cs: reconcile, then inhibitor `Refresh()`, then the sampling
// watchdog step) isn't simulated at tick granularity here at all, and no modeled `EnvAction`
// is gated on watchdog cadence: `ToggleMediaPlaying` (the action that feeds `LockInhibited`) is
// a mirror action that calls the step under test with `Reconcile` synchronously on every toggle
// (`applyMirrorChange`), exactly like `OsLock`/`OsUnlock`/`TogglePause` -- not something that
// only takes effect on the next watchdog-cadence inhibitor refresh. So `WatchdogIntervalMs` has
// no modeled threshold to derive a boundary pair against; this is a scope decision, not a gap.
let tickDeltas (config: PolicyConfig) : int64 list =
    let boundary t = [ max 1L (t - 1L); t ]
    (int64 Cadence.DefaultSampleIntervalMs
     :: (boundary config.GraceMs
         @ boundary config.AwayThresholdMs
         @ boundary config.NoSignalReportAfterMs
         @ boundary config.InputIdleRequiredMs))
    |> List.distinct
    |> List.sort

// --------------------------------------------------------------------------------------------
// Dedup key: (Armed, grace bucket, away bucket, bad-signal bucket, input-idle bucket) x the
// env-truth tuple x 'aux, each clock bucketed against its single governing threshold. The RFC
// frames the bad-signal component as `ClockBucket option` (None while no continuous bad-signal
// run is in progress); collapsed here to a plain `Zero`-inclusive `ClockBucket` because `State`'s
// representation is `internal` (invisible outside PresenceLock.Core) so `BadSignalSince`'s
// None-vs-Some can't be observed directly through the public `Snapshot`/`status` surface -- and
// the collapse is behaviorally sound, not a loss of precision: `dispatch`'s `NoFrame`/`DarkFrame`
// arm treats `None` identically to `Some now` (`match state.BadSignalSince with Some s -> s |
// None -> now`), and every other event resets it to `None` regardless of its prior value, so
// `None` and "Some, zero elapsed" are provably equivalent for every future transition.
// --------------------------------------------------------------------------------------------
type ClockBucket =
    | Zero
    | Mid
    | Past

let private bucket (thresholdMs: int64) (elapsedMs: int64) : ClockBucket =
    if elapsedMs <= 0L then Zero
    elif elapsedMs >= thresholdMs then Past
    else Mid

type AbstractedCore =
    { Armed: bool
      GraceBucket: ClockBucket
      AwayBucket: ClockBucket
      BadSignalBucket: ClockBucket
      InputIdleBucket: ClockBucket }

let abstractCore (config: PolicyConfig) (env: Env) (state: State) : AbstractedCore =
    let snap = Policy.snapshot (config, state, MonotonicMs env.Now)
    { Armed = snap.Armed
      GraceBucket = bucket config.GraceMs snap.GraceForMs
      AwayBucket = bucket config.AwayThresholdMs snap.AwayForMs
      BadSignalBucket = bucket config.NoSignalReportAfterMs snap.NoSignalForMs
      InputIdleBucket = bucket config.InputIdleRequiredMs (idleMsOf env) }

type NodeKey<'aux> = AbstractedCore * EnvTruth * 'aux

let keyOf (config: PolicyConfig) (world: World<'aux>) : NodeKey<'aux> =
    abstractCore config world.Env world.Core, world.Env.Truth, world.Aux

// --------------------------------------------------------------------------------------------
// Harness-wide fixtures: recovery axes fixed (HasSucceededOnce = true, InitFailStreak = 0, all
// restart stamps unset), thresholds within reach except the restart-gating ones (kept beyond
// the modeled horizon so restart machinery stays quiescent -- `apply`'s `Restart` handling
// exists for totality but is unreachable in the graph this config explores).
// --------------------------------------------------------------------------------------------
let noStamps: RestartStamps =
    { WedgeAt = System.Nullable(); ReevalAt = System.Nullable(); UpgradeAt = System.Nullable() }

let harnessConfig: PolicyConfig =
    { AwayThresholdMs = 3000L
      InputIdleRequiredMs = 2500L
      GraceMs = 2000L
      NoSignalReportAfterMs = 4000L
      ReevaluateAfterMs = 10_000_000L
      ReevaluateCooldownMs = 10_000_000L
      RecoveryFailureThreshold = 1000
      RecoveryCooldownMs = 10_000_000L
      UpgradeCooldownMs = 10_000_000L }

let private initialTruth: EnvTruth =
    { OsLocked = false
      Paused = false
      CameraAlive = true
      MediaPlaying = false
      FacePresent = false
      InputIdle = false }

let private initialEnv: Env =
    { Truth = initialTruth; Now = 0L; NowWall = 0L; IdleSinceMs = None }

/// The explorer's root: `Init` at t=0, then one `InitSucceeded` to fix the recovery axes
/// (`HasSucceededOnce = true`, `InitFailStreak = 0`) before exploration begins -- `Policy.start`
/// itself always yields `HasSucceededOnce = false`, so this one setup call is required to reach
/// the scope the RFC pins ("every reachable state" means reachable under this alphabet with
/// those axes fixed).
let initialWorld (sut: StepUnderTest<'aux>) (config: PolicyConfig) : World<'aux> =
    let inputs = stepInputsOf initialEnv
    let state0, aux0 = sut.Init (MonotonicMs initialEnv.Now) noStamps inputs
    let ctx = ctxOf initialEnv inputs
    let result, aux1 = sut.Step config (state0, aux0) ctx Event.InitSucceeded
    { Env = initialEnv; Core = result.State; Aux = aux1 }

// --------------------------------------------------------------------------------------------
// Property 13 -- status/level agreement, PLUS (slice 3) the `lockInhibited` agreement clause:
// `Policy.lockInhibited state = inputs.LockInhibited` after every step -- real regression
// coverage for the `LastInputs` invariant, since `lockInhibited` is a pure echo of
// `LastInputs.LockInhibited` and can only drift if some call site fails to route the current
// inputs through `step`'s tail. Rows 3/4 (Recovering/AcquiringCamera) require
// `not HasSucceededOnce`, which the harness's fixed recovery axes hold permanently false (see
// `initialWorld`) -- structurally unreachable here, exactly as the RFC's Scope section states,
// not silently skipped.
// --------------------------------------------------------------------------------------------
exception HarnessViolation of string

let private traceString (trace: EnvAction list) : string = sprintf "%A" (List.rev trace)

let assertStatusAgreement (config: PolicyConfig) (world: World<'aux>) (trace: EnvAction list) : unit =
    let inputs = stepInputsOf world.Env
    let snap = Policy.snapshot (config, world.Core, MonotonicMs world.Env.Now)
    let actual = Policy.status world.Core
    let expected =
        if inputs.Paused then Status.Paused
        elif inputs.SessionLocked then Status.SessionLocked
        elif snap.NoSignalForMs >= config.NoSignalReportAfterMs then Status.NoSignal
        else Status.Watching
    if actual <> expected then
        raise (
            HarnessViolation(
                sprintf
                    "property 13 (status): expected %A, got %A (truth=%A inputs=%A snap=%A)\nTrace: %s"
                    expected
                    actual
                    world.Env.Truth
                    inputs
                    snap
                    (traceString trace)
            )
        )
    let actualInhibited = Policy.lockInhibited world.Core
    if actualInhibited <> inputs.LockInhibited then
        raise (
            HarnessViolation(
                sprintf
                    "property 13 (lockInhibited agreement): expected %b, got %b (truth=%A inputs=%A)\nTrace: %s"
                    inputs.LockInhibited
                    actualInhibited
                    world.Env.Truth
                    inputs
                    (traceString trace)
            )
        )

// --------------------------------------------------------------------------------------------
// Property 14 -- protection liveness. From the given world, force (unlocked, unpaused,
// uninhibited, camera alive, face seen to arm, then absent + idle past the away threshold) and
// assert the resulting Tick fires Action.Lock. `MediaPlaying`/`LockInhibited` is a live axis from
// slice 3 onward (see `EnvTruth`), so the drive must now explicitly clear it -- without this
// step, any node reached with the inhibitor active would (correctly, per the slice-3 gate) never
// produce Action.Lock, and this property would fail on every such node, not because liveness is
// broken but because the drive stopped forcing every field its own name promises.
// --------------------------------------------------------------------------------------------
let driveToLock (sut: StepUnderTest<'aux>) (config: PolicyConfig) (world: World<'aux>) : Action * EnvAction list =
    let steps = ResizeArray<EnvAction>()
    let mutable w = world
    let doAction (a: EnvAction) : Action =
        match apply sut config w a with
        | Some(w', act) ->
            steps.Add a
            w <- w'
            act
        | None -> failwithf "property 14 drive: illegal action %A from truth %A" a w.Env.Truth
    // 1. Unsuppress -- unlock first, then unpause (pause toggle requires an unlocked session).
    if w.Env.Truth.OsLocked then
        doAction OsUnlock |> ignore
    if w.Env.Truth.Paused then
        doAction TogglePause |> ignore
    // 1b. Uninhibit.
    if w.Env.Truth.MediaPlaying then
        doAction ToggleMediaPlaying |> ignore
    // 2. Camera alive.
    if not w.Env.Truth.CameraAlive then
        doAction ToggleCameraAlive |> ignore
    // 3. Face seen to arm -- a clean off/on edge, then a tiny tick delivers FaceSeen (which also
    // re-baselines AwayBaselineAt to "now").
    if w.Env.Truth.FacePresent then
        doAction ToggleFacePresent |> ignore
    doAction ToggleFacePresent |> ignore
    doAction (Tick 1L) |> ignore
    // 4. Absent again.
    doAction ToggleFacePresent |> ignore
    // 5. Idle, from a clean edge so the idle clock starts at exactly "now".
    if w.Env.Truth.InputIdle then
        doAction ToggleInputIdle |> ignore
    doAction ToggleInputIdle |> ignore
    // 6. One tick past every remaining threshold at once: grace (measured from whatever baseline
    // is current -- read live via the newly-added Snapshot.GraceForMs), away and idle (both
    // freshly zeroed just above).
    let snap = Policy.snapshot (config, w.Core, MonotonicMs w.Env.Now)
    let graceRemaining = max 0L (config.GraceMs - snap.GraceForMs)
    let delta = List.max [ graceRemaining; config.AwayThresholdMs; config.InputIdleRequiredMs ] + 1L
    let finalAction = doAction (Tick delta)
    finalAction, List.ofSeq steps

let assertProtectionLiveness (sut: StepUnderTest<'aux>) (config: PolicyConfig) (world: World<'aux>) (trace: EnvAction list) : unit =
    let action, driveSteps = driveToLock sut config world
    if action <> Action.Lock then
        raise (
            HarnessViolation(
                sprintf
                    "property 14: drive did not produce Action.Lock (got %A) from node reached by %s\nTrace: drive steps %A"
                    action
                    (traceString trace)
                    driveSteps
            )
        )

// --------------------------------------------------------------------------------------------
// Harness check 3 -- inhibitor semantics (RFC "Verification harness"): from the given world,
// drive to (unlocked, unpaused, camera alive, face seen to arm, then absent + idle past every
// remaining threshold) while INHIBITED, and assert Lock never fires -- neither on the Tick that
// crosses every threshold while still inhibited, nor on the Reconcile that announces the
// inhibitor clearing (a lock decision needs a current observation, which Reconcile never
// carries). Then assert the very next Sample, still absent, fires Lock -- bounded by one sample
// interval, mirroring `driveToLock`'s own final step exactly but with the inhibitor as the one
// remaining gate to clear.
// --------------------------------------------------------------------------------------------
let driveInhibitorSemantics
    (sut: StepUnderTest<'aux>)
    (config: PolicyConfig)
    (world: World<'aux>)
    : Action * Action * Action * EnvAction list =
    let steps = ResizeArray<EnvAction>()
    let mutable w = world
    let doAction (a: EnvAction) : Action =
        match apply sut config w a with
        | Some(w', act) ->
            steps.Add a
            w <- w'
            act
        | None -> failwithf "harness check 3 drive: illegal action %A from truth %A" a w.Env.Truth
    // 1. Unsuppress -- unlock first, then unpause (pause toggle requires an unlocked session).
    if w.Env.Truth.OsLocked then
        doAction OsUnlock |> ignore
    if w.Env.Truth.Paused then
        doAction TogglePause |> ignore
    // 2. Camera alive.
    if not w.Env.Truth.CameraAlive then
        doAction ToggleCameraAlive |> ignore
    // 3. Inhibit -- from a clean edge, so the Reconcile below fires exactly at the transition.
    if w.Env.Truth.MediaPlaying then
        doAction ToggleMediaPlaying |> ignore
    doAction ToggleMediaPlaying |> ignore
    // 4. Face seen to arm -- a clean off/on edge, then a tiny tick delivers FaceSeen.
    if w.Env.Truth.FacePresent then
        doAction ToggleFacePresent |> ignore
    doAction ToggleFacePresent |> ignore
    doAction (Tick 1L) |> ignore
    // 5. Absent again.
    doAction ToggleFacePresent |> ignore
    // 6. Idle, from a clean edge so the idle clock starts at exactly "now".
    if w.Env.Truth.InputIdle then
        doAction ToggleInputIdle |> ignore
    doAction ToggleInputIdle |> ignore
    // 7. One tick past every remaining threshold at once, while still inhibited -- must NOT lock.
    let snap = Policy.snapshot (config, w.Core, MonotonicMs w.Env.Now)
    let graceRemaining = max 0L (config.GraceMs - snap.GraceForMs)
    let delta = List.max [ graceRemaining; config.AwayThresholdMs; config.InputIdleRequiredMs ] + 1L
    let duringInhibitionAction = doAction (Tick delta)
    // 8. Clear the inhibitor -- the announcing Reconcile must not lock either.
    let clearingAction = doAction ToggleMediaPlaying
    // 9. The very next Sample, still absent, must lock -- one full sample interval, matching the
    // real shell's cadence rather than an arbitrary small delta.
    let nextSampleAction = doAction (Tick(int64 Cadence.DefaultSampleIntervalMs))
    duringInhibitionAction, clearingAction, nextSampleAction, List.ofSeq steps

let assertInhibitorSemantics (sut: StepUnderTest<'aux>) (config: PolicyConfig) (world: World<'aux>) (trace: EnvAction list) : unit =
    let duringInhibition, clearing, nextSample, driveSteps = driveInhibitorSemantics sut config world
    let fail msg =
        raise (HarnessViolation(sprintf "%s\nTrace: drive steps %A (from %s)" msg driveSteps (traceString trace)))
    if duringInhibition = Action.Lock then
        fail "harness check 3: Lock fired while LockInhibited was active and every other threshold was past"
    if clearing = Action.Lock then
        fail "harness check 3: Lock fired on the Reconcile that announced the inhibitor clearing"
    if nextSample <> Action.Lock then
        fail (
            sprintf
                "harness check 3: expected Lock on the next Sample after the inhibitor cleared, still absent, got %A"
                nextSample
        )

// --------------------------------------------------------------------------------------------
// Edge-reset postcondition (RFC property 14's complementary check): at every suppressed ->
// unsuppressed edge the explorer takes, the reset's full postcondition holds.
// --------------------------------------------------------------------------------------------
let assertEdgeResetPostcondition (config: PolicyConfig) (world: World<'aux>) (trace: EnvAction list) : unit =
    let snap = Policy.snapshot (config, world.Core, MonotonicMs world.Env.Now)
    let fail msg =
        raise (HarnessViolation(sprintf "%s\nTrace: %s" msg (traceString trace)))
    if snap.Armed then
        fail "edge-reset postcondition: expected Armed = false after a suppressed -> unsuppressed edge"
    if not snap.InGrace || snap.GraceForMs <> 0L then
        fail "edge-reset postcondition: expected GraceBaselineAt = now (InGrace, GraceForMs = 0)"
    if snap.AwayForMs <> 0L then
        fail "edge-reset postcondition: expected AwayBaselineAt = now (AwayForMs = 0)"
    if snap.NoSignalForMs <> 0L then
        fail "edge-reset postcondition: expected BadSignalSince clear (NoSignalForMs = 0)"

// --------------------------------------------------------------------------------------------
// Exhaustive explorer: BFS over the reachable (env x abstracted-core-state) graph, depth-bounded,
// dedup'd via `keyOf`, asserting property 13 + property 14 + harness check 3 (inhibitor
// semantics, slice 3) at every node and the edge-reset postcondition at every suppressed ->
// unsuppressed transition it takes. Full path-provenance
// tracking is deliberately not attempted beyond one path per node (the first BFS finds) --
// exactly enough to print a trace on violation, per the RFC ("Full path-provenance tracking
// inside the BFS is explicitly not required -- it would fight the dedup that keeps the graph
// finite, and the replay checks the same thing directly").
// --------------------------------------------------------------------------------------------
type ExplorerResult<'aux> =
    { Visited: HashSet<NodeKey<'aux>>
      NodesExplored: int
      MaxDepthReached: int }

let explore<'aux when 'aux: equality> (sut: StepUnderTest<'aux>) (config: PolicyConfig) (maxDepth: int) : ExplorerResult<'aux> =
    let deltas = tickDeltas config
    let root = initialWorld sut config
    let visited = HashSet<NodeKey<'aux>>(HashIdentity.Structural)
    let queue = Queue<World<'aux> * EnvAction list * int>()
    visited.Add(keyOf config root) |> ignore
    queue.Enqueue(root, [], 0)
    let mutable nodesExplored = 0
    let mutable maxDepthSeen = 0

    while queue.Count > 0 do
        let world, trace, depth = queue.Dequeue()
        nodesExplored <- nodesExplored + 1
        maxDepthSeen <- max maxDepthSeen depth
        assertStatusAgreement config world trace
        assertProtectionLiveness sut config world trace
        assertInhibitorSemantics sut config world trace

        if depth < maxDepth then
            for action in legalActions world.Env.Truth deltas do
                match apply sut config world action with
                | None -> ()
                | Some(world', _) ->
                    let trace' = action :: trace
                    if suppressedTruth world.Env.Truth && not (suppressedTruth world'.Env.Truth) then
                        assertEdgeResetPostcondition config world' trace'
                    let key' = keyOf config world'
                    if visited.Add key' then
                        queue.Enqueue(world', trace', depth + 1)

    { Visited = visited
      NodesExplored = nodesExplored
      MaxDepthReached = maxDepthSeen }

// --------------------------------------------------------------------------------------------
// Mutant 1: the folded-flag variant (the historical mechanism the incident traces to). Cannot
// reimplement decision logic from scratch (`State` is opaque outside PresenceLock.Core) -- so it
// delegates to the real, unmodified `Policy.step`/`Policy.start`, but feeds them a FOLDED view of
// SessionLocked/Paused derived from consecutive true `StepInputs` (aux-held previous inputs),
// reproducing the pre-RFC architecture (folded beliefs from events, including the exact
// "SessionUnlocked while paused is a no-op" rule -- 0001-core-brain.md's truth table) through
// input translation in front of an otherwise-untouched core.
// --------------------------------------------------------------------------------------------
type FoldedAux =
    { FoldedSessionLocked: bool
      FoldedPaused: bool
      PrevSessionLocked: bool
      PrevPaused: bool }

let private foldTransition (aux: FoldedAux) (trueInputs: StepInputs) : FoldedAux =
    let foldedSessionLocked =
        if not aux.PrevSessionLocked && trueInputs.SessionLocked then
            true // SessionLocked event: always latches (0001 truth table row 1), pause or not.
        elif aux.PrevSessionLocked && not trueInputs.SessionLocked then
            // SessionUnlocked event: no-op while (still) paused -- the incident's exact defect.
            if aux.FoldedPaused then aux.FoldedSessionLocked else false
        else
            aux.FoldedSessionLocked
    let foldedPaused =
        if not aux.PrevPaused && trueInputs.Paused then true // Paused event: always sets.
        elif aux.PrevPaused && not trueInputs.Paused then false // Resumed event: always clears.
        else aux.FoldedPaused
    { FoldedSessionLocked = foldedSessionLocked
      FoldedPaused = foldedPaused
      PrevSessionLocked = trueInputs.SessionLocked
      PrevPaused = trueInputs.Paused }

let foldedFlagStepUnderTest: StepUnderTest<FoldedAux> =
    { Init =
        fun now stamps inputs ->
            let state = Policy.start (now, stamps, inputs)
            let aux =
                { FoldedSessionLocked = inputs.SessionLocked
                  FoldedPaused = inputs.Paused
                  PrevSessionLocked = inputs.SessionLocked
                  PrevPaused = inputs.Paused }
            state, aux
      Step =
        fun config (state, aux) ctx event ->
            let aux' = foldTransition aux ctx.Inputs
            let doctoredInputs =
                { ctx.Inputs with
                    SessionLocked = aux'.FoldedSessionLocked
                    Paused = aux'.FoldedPaused }
            let result = Policy.step (config, state, { ctx with Inputs = doctoredInputs }, event)
            result, aux' }

// --------------------------------------------------------------------------------------------
// Mutant 2: the stale-LastInputs variant (a new mechanism a levels design could fail by). Skips
// updating what it feeds the real `Policy.step` as inputs while the TRUE environment is
// suppressed -- reproducing "LastInputs never re-synced during a suppression window" via the
// same input-shim construction the opacity of `State` forces on mutant 1: `Policy.step`'s own
// tail always writes its internal `LastInputs` from whatever `ctx.Inputs` it was handed, so
// feeding it a frozen snapshot while suppressed makes ITS `LastInputs` stale by construction,
// without touching Core. `InputIdleMs` is canonicalized to a threshold-relative flag (0 below
// `InputIdleRequiredMs`, exactly at it otherwise) before being frozen/stored: `dispatch` only
// ever compares `InputIdleMs` against that one threshold, so the canonicalized value is
// behaviorally identical to the raw one for every decision Policy could make, while keeping
// `'aux` bounded for the explorer's dedup (an unbounded raw ms value would defeat it).
// --------------------------------------------------------------------------------------------
type StaleAux = { LastFedInputs: StepInputs }

let private canonicalizeIdle (config: PolicyConfig) (inputs: StepInputs) : StepInputs =
    { inputs with
        InputIdleMs = if inputs.InputIdleMs >= config.InputIdleRequiredMs then config.InputIdleRequiredMs else 0L }

let staleLastInputsStepUnderTest: StepUnderTest<StaleAux> =
    { Init =
        fun now stamps inputs ->
            let state = Policy.start (now, stamps, inputs)
            state, { LastFedInputs = canonicalizeIdle harnessConfig inputs }
      Step =
        fun config (state, aux) ctx event ->
            let suppressed = ctx.Inputs.SessionLocked || ctx.Inputs.Paused
            // Full fidelity while unsuppressed (feed the exact true inputs, matching real
            // behavior identically); the freeze -- the bug under test -- applies only while
            // suppressed, re-feeding whatever was last stored instead of the new true inputs.
            let feed = if suppressed then aux.LastFedInputs else ctx.Inputs
            let result = Policy.step (config, state, { ctx with Inputs = feed }, event)
            result, { LastFedInputs = canonicalizeIdle config feed } }
