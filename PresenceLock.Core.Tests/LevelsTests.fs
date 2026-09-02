module PresenceLock.Core.Tests.LevelsTests

open Xunit
open FsCheck
open FsCheck.Xunit
open PresenceLock.Core
open PresenceLock.Core.Tests.Generators

/// RFC 0002-environment-levels: tests for the `StepInputs`/`StepContext` contract, exercised
/// via `Policy.start`/`Policy.step`. Helpers mirror Tests.fs's `noStamps`/`someConfig`
/// conventions; duplicated locally rather than shared because the originals are `private` to
/// the `Tests` module.

let private noStamps: RestartStamps =
    { WedgeAt = System.Nullable(); ReevalAt = System.Nullable(); UpgradeAt = System.Nullable() }

let private someConfig: PolicyConfig =
    { AwayThresholdMs = 5000L
      InputIdleRequiredMs = 10000L
      GraceMs = 10000L
      NoSignalReportAfterMs = 10000L
      ReevaluateAfterMs = 20000L
      ReevaluateCooldownMs = 600000L
      RecoveryFailureThreshold = 3
      RecoveryCooldownMs = 600000L
      UpgradeCooldownMs = 600000L }

let private unsuppressedInputs: StepInputs =
    { SessionLocked = false
      Paused = false
      LockInhibited = false
      InputIdleMs = 0L }

let private ctx (nowMs: int64) (inputs: StepInputs) : StepContext =
    { Now = MonotonicMs nowMs; NowWall = WallClockMs nowMs; Inputs = inputs }

[<Fact>]
let ``tracer: pause, session-lock, unlock, resume as pure StepInputs transitions still locks (incident fixed point)`` () =
    // 0001-core-brain.md's folded IsPaused/IsSessionLocked contract made this exact sequence
    // permanently unlockable (RFC 0002 Motivation, incident 2026-08-17): SessionUnlocked while
    // paused was specified as a no-op, so the pause flag never cleared and sampling was
    // discarded forever. The levels path replaces the fold with sampled StepInputs and a
    // single suppression edge rule — this sequence must now reach an armable, lockable state.
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let initResult =
        Policy.step (someConfig, state, ctx 0L unsuppressedInputs, Event.InitSucceeded)

    // Pause (user toggles the tray while unlocked).
    let pausedInputs = { unsuppressedInputs with Paused = true }
    let pausedResult =
        Policy.step (someConfig, initResult.State, ctx 1L pausedInputs, Event.Reconcile)
    Assert.Equal(Status.Paused, Policy.status pausedResult.State)

    // Session lock arrives on top of the pause (both suppressing). Status stays Paused --
    // priority row 1 outranks row 2 -- but SessionLocked is now also true in LastInputs,
    // which is what the next step (unlock while still paused) needs to exercise.
    let lockedInputs = { pausedInputs with SessionLocked = true }
    let lockedResult =
        Policy.step (someConfig, pausedResult.State, ctx 2L lockedInputs, Event.Reconcile)
    Assert.Equal(Status.Paused, Policy.status lockedResult.State)

    // Session unlock -- still paused, so still suppressed (this is exactly the incident's
    // "SessionUnlocked while paused" moment, now expressed as a StepInputs change).
    let unlockedStillPausedInputs = { lockedInputs with SessionLocked = false }
    let unlockedStillPausedResult =
        Policy.step (someConfig, lockedResult.State, ctx 3L unlockedStillPausedInputs, Event.Reconcile)
    Assert.Equal(Status.Paused, Policy.status unlockedStillPausedResult.State)

    // Resume (user un-pauses) -- the suppressed -> unsuppressed edge fires here, re-baselining.
    let resumedResult =
        Policy.step (someConfig, unlockedStillPausedResult.State, ctx 10001L unsuppressedInputs, Event.Reconcile)
    Assert.Equal(Status.Watching, Policy.status resumedResult.State)

    // Arm with a FaceSeen sample, then drive NoFace samples past every threshold -- the
    // fixed-point claim: this must lock, where the old contract never could.
    let armedResult =
        Policy.step (
            someConfig,
            resumedResult.State,
            ctx 20002L unsuppressedInputs,
            Event.Sample(Observation.FaceSeen)
        )
    Assert.True((Policy.snapshot (someConfig, armedResult.State, MonotonicMs 20002L)).Armed)

    let idleInputs = { unsuppressedInputs with InputIdleMs = someConfig.InputIdleRequiredMs }
    let lockResult =
        Policy.step (
            someConfig,
            armedResult.State,
            ctx (20002L + someConfig.AwayThresholdMs) idleInputs,
            Event.Sample(Observation.NoFace)
        )
    Assert.Equal(Action.Lock, lockResult.Action)

// --- First-call edge pin (RFC "Policy.start initializes LastInputs to those same initial
// inputs verbatim ... pinned ... a core test pins it from both directions") --------------

[<Fact>]
let ``start with suppressed inputs then an unsuppressed Reconcile fires the baseline reset`` () =
    let suppressedAtStart = { unsuppressedInputs with SessionLocked = true }
    let state = Policy.start (MonotonicMs 0L, noStamps, suppressedAtStart)
    // Far enough past both GraceMs and AwayThresholdMs, measured from t=0, that only a fired
    // reset (re-stamping the baselines to this call's `now`) can make InGrace true /
    // AwayForMs zero below -- an unfired reset would leave both stale at t=0.
    let farNow = 50000L
    let result = Policy.step (someConfig, state, ctx farNow unsuppressedInputs, Event.Reconcile)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs farNow)
    Assert.False(snap.Armed)
    Assert.True(snap.InGrace)
    Assert.Equal(0L, snap.AwayForMs)

[<Fact>]
let ``start with unsuppressed inputs then an unsuppressed call fires no baseline reset`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let farNow = 50000L
    let result = Policy.step (someConfig, state, ctx farNow unsuppressedInputs, Event.Reconcile)
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs farNow)
    // No edge (LastInputs was never suppressed) -- baselines stay at start's t=0, so both
    // clocks read as fully elapsed at farNow rather than freshly reset.
    Assert.False(snap.InGrace)
    Assert.Equal(farNow, snap.AwayForMs)

// --- start's LastInputs verbatim carry, the remaining two fields (RFC "Policy.start
// initializes LastInputs to those same initial inputs verbatim ... for all four fields"): the
// two edge tests just above exercise SessionLocked/Paused (the two fields `suppressed` reads)
// via the suppression edge. LockInhibited and InputIdleMs need their own coverage. -----------

[<Fact>]
let ``start carries LockInhibited verbatim into LastInputs, observable directly via the lockInhibited projection`` () =
    let inhibitedAtStart = { unsuppressedInputs with LockInhibited = true }
    let inhibitedState = Policy.start (MonotonicMs 0L, noStamps, inhibitedAtStart)
    Assert.True(Policy.lockInhibited inhibitedState)
    let uninhibitedState = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    Assert.False(Policy.lockInhibited uninhibitedState)

// InputIdleMs's verbatim carry at `start` is NOT independently observable -- not just before
// the first Sample, but ever. `dispatch`'s `NoFace` arm is the only place `InputIdleMs` is
// read for a decision (`idleSatisfied`), and it reads `ctx.Inputs.InputIdleMs` -- the CURRENT
// call's freshly sampled idle value -- never `state.LastInputs.InputIdleMs`. Of `LastInputs`'s
// four fields, only `SessionLocked`/`Paused` (via `suppressed`) and `LockInhibited` (via the
// `lockInhibited` projection, pinned above) ever feed a decision or a public projection;
// `InputIdleMs` is written into `LastInputs` on every `step`/`start` call but is otherwise
// write-only from that point on -- there is no `Policy.inputIdleMs` projection and no dispatch
// arm ever reads it back off `state`. So there is no observable, at `start` or afterward,
// through which a verbatim-vs-defaulted initial `InputIdleMs` could ever diverge; per the
// finding's own escape hatch, that is documented here rather than pinned with a contrived test.

// --- While-suppressed Sample: decision-state/Action no-op, but LastInputs/CachedStatus
// still update (RFC: "'No-op' refers to decision state ... never to LastInputs/CachedStatus")

[<Fact>]
let ``a Sample delivered while still suppressed is a decision-state and Action no-op, but LastInputs and CachedStatus still update`` () =
    let sessionLockedInputs = { unsuppressedInputs with SessionLocked = true }
    let state = Policy.start (MonotonicMs 0L, noStamps, sessionLockedInputs)
    Assert.Equal(Status.SessionLocked, Policy.status state)
    // Still suppressed (now Paused instead of SessionLocked -- no edge), carrying a FaceSeen
    // sample that would otherwise arm, plus a flipped LockInhibited.
    let newSuppressedInputs =
        { SessionLocked = false; Paused = true; LockInhibited = true; InputIdleMs = 123L }
    let result =
        Policy.step (someConfig, state, ctx 100L newSuppressedInputs, Event.Sample(Observation.FaceSeen))
    Assert.Equal(Action.NoAction, result.Action)
    Assert.False((Policy.snapshot (someConfig, result.State, MonotonicMs 100L)).Armed)
    // Status recomputed from the NEW LastInputs (Paused now outranks the old SessionLocked),
    // not merely carried over from the previous call -- proves CachedStatus updated.
    Assert.Equal(Status.Paused, Policy.status result.State)
    // Proves LastInputs itself updated to the new inputs, not just CachedStatus in isolation.
    Assert.True(Policy.lockInhibited result.State)

// --- Edge delayed-not-lost: the edge rule sits above dispatch, so ANY event (not only
// Reconcile) carrying an unsuppressed StepInputs after a suppressed LastInputs fires the
// reset -----------------------------------------------------------------------------------

[<Fact>]
let ``the suppression edge fires even when delivered via a Sample, not only via Reconcile`` () =
    let suppressedAtStart = { unsuppressedInputs with Paused = true }
    let state = Policy.start (MonotonicMs 0L, noStamps, suppressedAtStart)
    let farNow = 50000L
    // A Sample carrying unsuppressed inputs, arriving directly on the suppressed->unsuppressed
    // edge -- no intervening Reconcile. The edge rule (evaluated before dispatch, for every
    // event) must still fire the reset, and dispatch must still run normally against the
    // reset state (unlike the while-suppressed no-op, which does not apply here since
    // ctx.Inputs is unsuppressed).
    let result =
        Policy.step (someConfig, state, ctx farNow unsuppressedInputs, Event.Sample(Observation.FaceSeen))
    let snap = Policy.snapshot (someConfig, result.State, MonotonicMs farNow)
    Assert.True(snap.InGrace)
    Assert.Equal(0L, snap.AwayForMs)
    // Dispatch ran (not suppressed) -- FaceSeen armed on top of the reset state.
    Assert.True(snap.Armed)

// --- start initial status: computed from the initial inputs, never a one-frame
// AcquiringCamera flash on a restart-while-suppressed process --------------------------

[<Fact>]
let ``start renders Paused immediately when the initial inputs are paused`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, { unsuppressedInputs with Paused = true })
    Assert.Equal(Status.Paused, Policy.status state)

[<Fact>]
let ``start renders SessionLocked immediately when the initial inputs are session-locked`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, { unsuppressedInputs with SessionLocked = true })
    Assert.Equal(Status.SessionLocked, Policy.status state)

[<Fact>]
let ``start renders AcquiringCamera immediately when the initial inputs are unsuppressed`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    Assert.Equal(Status.AcquiringCamera, Policy.status state)

// --- Level idempotence (RFC, scoped precisely): identical consecutive StepInputs trigger
// no edge and no baseline reset -- decision-state fields other than those a Sample's own
// observation legitimately changes are unaffected -----------------------------------------

[<Fact>]
let ``identical consecutive StepInputs across a Reconcile trigger no edge and no baseline reset`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let initResult = Policy.step (someConfig, state, ctx 0L unsuppressedInputs, Event.InitSucceeded)
    let armedResult =
        Policy.step (someConfig, initResult.State, ctx 1L unsuppressedInputs, Event.Sample(Observation.FaceSeen))
    // Two Reconciles in a row with the exact same (unsuppressed) inputs as the previous call
    // -- no edge either time, so the away clock set by the FaceSeen sample above must keep
    // accumulating from t=1 uninterrupted, never re-stamped to either Reconcile's `now`.
    let firstReconcile =
        Policy.step (someConfig, armedResult.State, ctx 1000L unsuppressedInputs, Event.Reconcile)
    let firstSnap = Policy.snapshot (someConfig, firstReconcile.State, MonotonicMs 1000L)
    Assert.True(firstSnap.Armed)
    Assert.Equal(999L, firstSnap.AwayForMs)
    let secondReconcile =
        Policy.step (someConfig, firstReconcile.State, ctx 2000L unsuppressedInputs, Event.Reconcile)
    let secondSnap = Policy.snapshot (someConfig, secondReconcile.State, MonotonicMs 2000L)
    Assert.True(secondSnap.Armed)
    Assert.Equal(1999L, secondSnap.AwayForMs)

// --- Level idempotence, generalized: the RFC pins this as a PROPERTY, not a single
// hand-picked scenario -- an identical-input no-op `step` call must leave EVERY decision-
// state field unchanged, not merely the two the fact above happens to probe (Armed/
// AwayForMs). Scoped to `Event.Reconcile` specifically (Reconcile carries no observation),
// so this stays compatible with Tests.fs property 9 (a `Sample` may legitimately re-derive
// Lock on repetition) -- this property only ever drives the SECOND, repeated call via
// Reconcile, never via Sample. `State`'s representation is `internal` (Types.fs) and this
// file has no `InternalsVisibleTo` grant, so every field below is checked the only way a
// real caller could: through `Policy.snapshot`/`status`/`lockInhibited`'s public projections
// -- which happen to cover exactly the RFC's named decision-state fields (Armed, grace/away
// baselines via GraceForMs/AwayForMs, BadSignalSince via NoSignalForMs, InitFailStreak, and
// every `RestartStamps` field via the three `Last*RestartAt` snapshot fields). -------------

/// Local to this file, not `Generators.fs` (no `StepInputs` arbitrary is registered there,
/// and this property is the only consumer). `InputIdleMs` is bounded like
/// `Generators.policyConfigGen`'s own idle-adjacent draw (0, 3_600_000) rather than FsCheck's
/// full `int64` range, so generated calls explore comparably against
/// `PolicyConfig.InputIdleRequiredMs`'s (1, 60_000) draw instead of drowning it in
/// astronomically large idle values that can never realistically cross a threshold.
let private stepInputsGen : Gen<StepInputs> =
    gen {
        let! sessionLocked = Arb.generate<bool>
        let! paused = Arb.generate<bool>
        let! lockInhibited = Arb.generate<bool>
        let! inputIdleMs = Gen.choose (0, 3_600_000) |> Gen.map int64
        return
            { SessionLocked = sessionLocked
              Paused = paused
              LockInhibited = lockInhibited
              InputIdleMs = inputIdleMs }
    }

/// One prior "setup" tick, reaching an arbitrary reachable decision state before the two-call
/// no-op pair under test: `deltaMs` of monotonic time elapses, then `step` runs against an
/// arbitrary sampled `StepInputs` and the full generic event alphabet (`Generators.eventGen`,
/// reused rather than redefined -- the same alphabet Tests.fs's properties 9/12 walk over).
let private setupTicksGen : Gen<(int64 * StepInputs * Event) list> =
    Gen.listOf (
        gen {
            let! deltaMs = Gen.choose (0, 20_000) |> Gen.map int64
            let! inputs = stepInputsGen
            let! event = eventGen
            return (deltaMs, inputs, event)
        }
    )

/// The two-call pair under test, plus the arbitrary warm-up walk that precedes it. `FirstEvent`
/// ranges over the full alphabet (any event may legitimately produce the "post-first-call
/// state" the redundant Reconcile must then leave untouched); the second call is always
/// `Event.Reconcile`, carrying `FirstInputs` again bitwise-unchanged.
type private NoopReconcileScenario =
    { Setup: (int64 * StepInputs * Event) list
      FirstDeltaMs: int64
      FirstInputs: StepInputs
      FirstEvent: Event
      SecondDeltaMs: int64 }

let private noopReconcileScenarioGen : Gen<NoopReconcileScenario> =
    gen {
        let! setup = setupTicksGen
        let! firstDeltaMs = Gen.choose (0, 20_000) |> Gen.map int64
        let! firstInputs = stepInputsGen
        let! firstEvent = eventGen
        let! secondDeltaMs = Gen.choose (0, 20_000) |> Gen.map int64
        return
            { Setup = setup
              FirstDeltaMs = firstDeltaMs
              FirstInputs = firstInputs
              FirstEvent = firstEvent
              SecondDeltaMs = secondDeltaMs }
    }

[<Properties(Arbitrary = [| typeof<Generators> |])>]
module PropertyTests =

    [<Property>]
    let ``identical consecutive StepInputs across a Reconcile leave every publicly observable decision-state field unchanged from the post-first-call state``
        (config: PolicyConfig)
        (stamps: RestartStamps)
        (startAt: MonotonicMs)
        =
        let (MonotonicMs startMs) = startAt
        Prop.forAll (Arb.fromGen noopReconcileScenarioGen) (fun scenario ->
            let initialState = Policy.start (MonotonicMs startMs, stamps, unsuppressedInputs)
            let folder (state, nowMs) (deltaMs, inputs, event) =
                let nowMs' = nowMs + deltaMs
                let result = Policy.step (config, state, ctx nowMs' inputs, event)
                (result.State, nowMs')
            let warmedState, warmedNowMs = List.fold folder (initialState, startMs) scenario.Setup
            // The first call of the pair -- establishes the post-first-call state the
            // redundant Reconcile below must leave untouched.
            let firstNowMs = warmedNowMs + scenario.FirstDeltaMs
            let firstResult =
                Policy.step (config, warmedState, ctx firstNowMs scenario.FirstInputs, scenario.FirstEvent)
            // The second call: StepInputs bitwise-identical to the first's (`scenario.FirstInputs`
            // again, not re-generated), driven by Event.Reconcile.
            let secondNowMs = firstNowMs + scenario.SecondDeltaMs
            let secondResult =
                Policy.step (config, firstResult.State, ctx secondNowMs scenario.FirstInputs, Event.Reconcile)
            // Querying the world at the SAME instant (secondNowMs) must read identically whether
            // or not the redundant Reconcile fired -- isolates "did anything mutate" from
            // ordinary clock progression, without needing access to `State`'s internal fields.
            //
            // `Policy.status` is deliberately NOT compared here: `CachedStatus` is a snapshot
            // of status AS OF the call that produced it, and Reconcile's entire purpose is to
            // re-derive status from elapsed time against unchanged baselines (Generators.fs:
            // "the mostly no-op, but re-derives status noise case") -- e.g. Watching -> NoSignal
            // as NoSignalForMs crosses the threshold between `firstNowMs` and `secondNowMs` is
            // correct, expected behavior, not a decision-state mutation. `lockInhibited` is
            // likewise not compared: it is a pure projection of `LastInputs.LockInhibited`,
            // trivially equal here since both calls carry the same `scenario.FirstInputs`.
            let asIfNoRedundantCallHadHappened = Policy.snapshot (config, firstResult.State, MonotonicMs secondNowMs)
            let afterRedundantCall = Policy.snapshot (config, secondResult.State, MonotonicMs secondNowMs)
            asIfNoRedundantCallHadHappened = afterRedundantCall)

// --- lockInhibited projection agrees with the inputs last passed (RFC "Status": the
// projection's consumers rely on it tracking LastInputs.LockInhibited exactly) ------------

[<Fact>]
let ``lockInhibited reflects LastInputs.LockInhibited as of the most recent step call`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    Assert.False(Policy.lockInhibited state)
    let inhibitedInputs = { unsuppressedInputs with LockInhibited = true }
    let inhibitedResult = Policy.step (someConfig, state, ctx 1L inhibitedInputs, Event.Reconcile)
    Assert.True(Policy.lockInhibited inhibitedResult.State)
    let clearedResult =
        Policy.step (someConfig, inhibitedResult.State, ctx 2L unsuppressedInputs, Event.Reconcile)
    Assert.False(Policy.lockInhibited clearedResult.State)

// --- Reconcile never emits Lock (RFC "Reconcile": "a lock decision needs a current
// observation, and Reconcile carries none") ------------------------------------------------

[<Fact>]
let ``Reconcile never emits Lock even when away/idle conditions would satisfy a Sample-driven lock`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let initResult = Policy.step (someConfig, state, ctx 0L unsuppressedInputs, Event.InitSucceeded)
    let armedResult =
        Policy.step (
            someConfig,
            initResult.State,
            ctx (someConfig.GraceMs + 1L) unsuppressedInputs,
            Event.Sample(Observation.FaceSeen)
        )
    let idleInputs = { unsuppressedInputs with InputIdleMs = someConfig.InputIdleRequiredMs }
    let farNow = someConfig.GraceMs + 1L + someConfig.AwayThresholdMs
    let reconcileResult = Policy.step (someConfig, armedResult.State, ctx farNow idleInputs, Event.Reconcile)
    Assert.Equal(Action.NoAction, reconcileResult.Action)
    // Confirms the away/idle/grace conditions really were satisfied -- an identical Sample
    // (not Reconcile) at the same instant does lock, so Reconcile's non-lock is the event
    // type's own behavior, not an accidental gate elsewhere.
    let sampleResult =
        Policy.step (someConfig, armedResult.State, ctx farNow idleInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.Lock, sampleResult.Action)

// --- Lock inhibition: fire-time gate on the NoFace arm's Lock emission, symmetric with the
// existing input-idle gate (RFC "Lock inhibition") -----------------------------------------

[<Fact>]
let ``an active LockInhibited vetoes Lock when armed and grace/away/idle are all past threshold, and the away clock keeps measuring instead of resetting`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let initResult = Policy.step (someConfig, state, ctx 0L unsuppressedInputs, Event.InitSucceeded)
    let armedResult =
        Policy.step (someConfig, initResult.State, ctx 0L unsuppressedInputs, Event.Sample(Observation.FaceSeen))
    let farNow = someConfig.GraceMs + someConfig.AwayThresholdMs + 1L
    let inhibitedIdleInputs =
        { unsuppressedInputs with
            LockInhibited = true
            InputIdleMs = someConfig.InputIdleRequiredMs }
    let lockResult =
        Policy.step (someConfig, armedResult.State, ctx farNow inhibitedIdleInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.NoAction, lockResult.Action)
    // Away clock kept measuring truth (still baselined at the FaceSeen's `now` = 0), not
    // saturated-then-reset by the veto -- "saturated, not reset" per the RFC.
    let snap = Policy.snapshot (someConfig, lockResult.State, MonotonicMs farNow)
    Assert.Equal(farNow, snap.AwayForMs)
    Assert.True(snap.AwayForMs >= someConfig.AwayThresholdMs)

[<Fact>]
let ``clearing the inhibitor while still absent does not lock via the announcing Reconcile, but locks on the very next Sample`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let initResult = Policy.step (someConfig, state, ctx 0L unsuppressedInputs, Event.InitSucceeded)
    let armedResult =
        Policy.step (someConfig, initResult.State, ctx 0L unsuppressedInputs, Event.Sample(Observation.FaceSeen))
    let farNow = someConfig.GraceMs + someConfig.AwayThresholdMs + 1L
    let idleInputs = { unsuppressedInputs with InputIdleMs = someConfig.InputIdleRequiredMs }
    let inhibitedIdleInputs = { idleInputs with LockInhibited = true }
    let inhibitedResult =
        Policy.step (someConfig, armedResult.State, ctx farNow inhibitedIdleInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.NoAction, inhibitedResult.Action)
    // Inhibitor clears -- the shell announces it via Reconcile, which must NOT lock: a lock
    // decision needs a current observation, and Reconcile carries none.
    let reconcileResult =
        Policy.step (someConfig, inhibitedResult.State, ctx (farNow + 1L) idleInputs, Event.Reconcile)
    Assert.Equal(Action.NoAction, reconcileResult.Action)
    // The very next Sample, still absent, fires the lock -- bounded by one sample interval.
    let sampleResult =
        Policy.step (someConfig, reconcileResult.State, ctx (farNow + 2L) idleInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.Lock, sampleResult.Action)

[<Fact>]
let ``an active inhibitor changes only the emitted Action, never Armed, grace/away baselines, or signal health`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let initResult = Policy.step (someConfig, state, ctx 0L unsuppressedInputs, Event.InitSucceeded)
    let armedResult =
        Policy.step (someConfig, initResult.State, ctx 0L unsuppressedInputs, Event.Sample(Observation.FaceSeen))
    let farNow = someConfig.GraceMs + someConfig.AwayThresholdMs + 1L
    let idleInputs = { unsuppressedInputs with InputIdleMs = someConfig.InputIdleRequiredMs }
    let inhibitedInputs = { idleInputs with LockInhibited = true }
    // Same state, same instant, differing only in LockInhibited -- isolates the gate's effect
    // from everything else NoFace already does (BadSignalSince clearing, etc).
    let inhibitedResult =
        Policy.step (someConfig, armedResult.State, ctx farNow inhibitedInputs, Event.Sample(Observation.NoFace))
    let uninhibitedResult =
        Policy.step (someConfig, armedResult.State, ctx farNow idleInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.NoAction, inhibitedResult.Action)
    Assert.Equal(Action.Lock, uninhibitedResult.Action)
    let inhibitedSnap = Policy.snapshot (someConfig, inhibitedResult.State, MonotonicMs farNow)
    let uninhibitedSnap = Policy.snapshot (someConfig, uninhibitedResult.State, MonotonicMs farNow)
    Assert.Equal(uninhibitedSnap.Armed, inhibitedSnap.Armed)
    Assert.Equal(uninhibitedSnap.InGrace, inhibitedSnap.InGrace)
    Assert.Equal(uninhibitedSnap.GraceForMs, inhibitedSnap.GraceForMs)
    Assert.Equal(uninhibitedSnap.AwayForMs, inhibitedSnap.AwayForMs)
    Assert.Equal(uninhibitedSnap.NoSignalForMs, inhibitedSnap.NoSignalForMs)

[<Fact>]
let ``an inhibited lock opportunity is not latched -- it is re-derived fresh and fires exactly once after the inhibitor clears`` () =
    let state = Policy.start (MonotonicMs 0L, noStamps, unsuppressedInputs)
    let initResult = Policy.step (someConfig, state, ctx 0L unsuppressedInputs, Event.InitSucceeded)
    let armedResult =
        Policy.step (someConfig, initResult.State, ctx 0L unsuppressedInputs, Event.Sample(Observation.FaceSeen))
    let idleInputs = { unsuppressedInputs with InputIdleMs = someConfig.InputIdleRequiredMs }
    let inhibitedInputs = { idleInputs with LockInhibited = true }
    let farNow = someConfig.GraceMs + someConfig.AwayThresholdMs + 1L
    // Two would-be-lock samples in a row while inhibited -- each individually vetoed; nothing
    // is remembered ("missed lock count", etc) that would fire twice once cleared.
    let firstInhibited =
        Policy.step (someConfig, armedResult.State, ctx farNow inhibitedInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.NoAction, firstInhibited.Action)
    let secondInhibited =
        Policy.step (someConfig, firstInhibited.State, ctx (farNow + 500L) inhibitedInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.NoAction, secondInhibited.Action)
    // Clearing via Reconcile still must not lock.
    let reconcileResult =
        Policy.step (someConfig, secondInhibited.State, ctx (farNow + 501L) idleInputs, Event.Reconcile)
    Assert.Equal(Action.NoAction, reconcileResult.Action)
    // The next Sample re-derives the decision fresh against current truth and locks exactly once.
    let lockResult =
        Policy.step (someConfig, reconcileResult.State, ctx (farNow + 502L) idleInputs, Event.Sample(Observation.NoFace))
    Assert.Equal(Action.Lock, lockResult.Action)
