# RFC: Environment levels and lock inhibitors

Status: Implemented; stage-4 review to floor (2 rounds, 2026-09-01) — 7-lens review over this scope and 0001-core-brain.md's shared core/shell; 1 Critical + 2 High + 4 Med fixed, floor = 0 Critical/High/Medium, core 91/91 + shell 143/143 green. Residual same-user-threat-model hardening tracked as follow-ups in the handoff. (all 6 slices landed 2026-08-31; 1.1.0.0 installed and live-smoked same day)
Depends on: 0001 (supersedes its session/pause event contract; its decision-state,
signal-health, and recovery contracts are unchanged and remain authoritative).

## Motivation

Incident 2026-08-17: the sequence pause → session lock → session unlock → resume left the
core permanently believing the session was locked while the user sat in an unlocked
session. Sampling was discarded, the away clock never ran, no lock could ever fire, and
the tray showed a plausible status. The root cause was not a coding error — the behavior
was specified (0001-core-brain.md pause/unlock truth table: "SessionUnlocked while paused
is a no-op"), tested (a test pinned the no-op), and inherited from the legacy shell.

The structural cause: session-locked and paused are **environment facts**, but the
contract delivered them as *edge events* that the core folded into beliefs
(`IsSessionLocked`, `IsPaused`). A folded belief has no way to re-synchronize with truth:
one swallowed edge diverges it permanently, and correctness of the fold requires an
interleaving-correct truth table over every event ordering — exactly where the spec was
wrong. Every property test derived from that truth table inherited its blind spot.

This RFC replaces folded beliefs with **sampled levels**, generalizes the pattern into a
**lock-inhibitor** architecture (first consumer: do not lock while media is playing), and
adds a **model-based verification harness** (protection-liveness property + exhaustive
reachable-state exploration) so any future absorbing "silently cannot lock" trap in the
state that legitimately remains historical becomes a test counterexample instead of an
incident.

Design principle, stated once: **the core's decision is a pure function of decision state,
time, and sampled environment facts. Facts gate actions; they never rewrite history.
Every environment fact is either owned by the shell or periodically re-sampled from the
OS — nothing is a fold that can silently drift.**

Terminology, pinned: **suppression** (paused or session-locked) freezes sampling at the
top of the pipeline — nothing is decided while it holds. **Inhibition** lets the entire
pipeline run and vetoes only the final `Lock` action. The English words are
near-synonyms; here they name opposite ends of the pipeline and are used strictly in
these senses throughout the code and docs.

## Core contract changes

### StepInputs and StepContext

```fsharp
/// Sampled environment truth, passed on EVERY step call — never folded from events.
/// Deliberately a plain reference record, NOT [<Struct>] — see Construction discipline.
type StepInputs =
    { SessionLocked: bool     // OS fact: shell mirrors SessionSwitch, reconciled via WTS query
      Paused: bool            // user intent: the shell owns the tray toggle and its persistence
      LockInhibited: bool     // true iff any registered shell-side inhibitor is active
      InputIdleMs: int64 }    // OS fact: GetLastInputInfo, queried fresh on every Advance

/// Everything about "the world right now" that isn't config, state, or the event being
/// processed. One record rather than three adjacent positional parameters — the same
/// call-site transposition-hazard reasoning that made MonotonicMs/WallClockMs distinct
/// CLR structs, applied one level up. Event stays a separate trailing parameter: it is
/// the *reason* the call happened, categorically different from ambient context.
[<Struct>]
type StepContext =
    { Now: MonotonicMs
      NowWall: WallClockMs
      Inputs: StepInputs }

// step  : PolicyConfig * State * StepContext * Event -> StepResult
// start : MonotonicMs * RestartStamps * StepInputs -> State
```

Construction discipline (rounds 2–3): the record bundling does *not* buy the
compiler-level protection `MonotonicMs`/`WallClockMs` have — `StepInputs` has three
adjacent same-typed bools, so a positional C# constructor call with two of them
transposed still compiles. Round 3 removes the sharper half of the hazard structurally:
as a `[<Struct>]` record, `default(StepInputs)` read as the *dangerous* value
(unsuppressed, uninhibited, idle) and was reachable through zero-init paths no
call-site discipline can guard (`Array.zeroCreate`, a dictionary miss,
`Unchecked.defaultof`, an uninitialized field). `StepInputs` is therefore a plain
reference record: every such path now yields `null` and fails loud with an NRE on
first field access instead of reading as plausible truth — a runtime fail-loud, not a
compile-time one (F# emits no NRT metadata for C# consumers), but strictly better than
plausible-wrong, and the cost is one small allocation per `Advance` at sample cadence,
immaterial. `StepContext` stays `[<Struct>]`: nesting a reference-type field is legal,
and `default(StepContext).Inputs` is likewise `null`, never a silent unsuppressed
world. The transposition half stays pinned rather than implied: the shell constructs
`StepInputs` in exactly one helper (used by `Advance`'s input assembly and the startup
`Policy.start` call), always via named arguments; a `PresenceLock.Tests` case
constructs it with each bool flipped independently and asserts the field-to-value
mapping, so a future reordering of the F# record's declared fields can never silently
transpose a positional call. (A per-field single-case DU — `LockInhibited of bool`,
the `MonotonicMs` treatment — was considered and rejected at this weight: the
one-helper rule plus the flip test already covers the class at lower interop cost.) The startup ordering — WTS query and pause consume-and-clear complete *before*
the first `StepInputs` is built — is likewise pinned by a shell test in slice 4, because
a misordered constructor would fail silently as "unsuppressed": sampling and decisions
proceeding as if the session weren't actually locked, the opposite of failing loud.

`InputIdleMs` moves out of the `Sample` payload and into `StepInputs`: it is an
always-askable OS fact — strictly more level-like than `SessionLocked`, which at least
needs WTS reconciliation — and belongs with the other levels by this RFC's own principle.
`Observation` alone stays event-shaped, deliberately: an observation is the product of a
completed capture-and-classify pass — episodic evidence with an arrival time, not a
resting value one can ask for at any instant — and "`Sample` is a no-op while suppressed"
is only expressible while samples are events. (A fully level-based design —
`Observation option` in the inputs, `Sample` replaced by a bare tick — was considered
and rejected: it would churn the suppressed-no-op semantics this RFC deliberately leaves
unchanged, for no gain beyond deleting one clause from the test model.)

The events `SessionLocked`, `SessionUnlocked`, `Paused`, `Resumed` are **removed** from
the `Event` DU. `Sample` becomes `Sample of Observation`. One event is **added**:

- `Reconcile` — payload-free: "nothing happened, except the levels may have changed."
  The shell fires it synchronously whenever any mirror changes (pause toggle, session
  switch, WTS reconciliation correction, inhibitor-aggregate transition). It exists
  because deleting the four events otherwise leaves those call sites with nothing to
  hand `Advance` — and with the sample timer stopped while suppressed, no `step` call
  would ever run during a suppression window, so `LastInputs`, `CachedStatus`, the tray
  text, and the unlock-time `KickAcquisitionIfNeeded` check would all go stale until the
  next natural `Sample`. A stale `LastInputs` can swallow the suppression edge outright
  (both sides of the comparison equal, reset never fires) — the incident class again,
  via a new mechanism. `Reconcile` performs edge derivation and status recomputation
  only; it never emits `Lock` (a lock decision needs a current observation, and
  `Reconcile` carries none — an inhibitor clearing therefore produces the lock on the
  next `Sample`, bounded by one sample interval; see Lock inhibition).

Remaining events: `Sample`, `Reconcile`, `InitSucceeded`, `InitFailed`, `CaptureFailed`,
`BetterCameraAvailable`.

`State` drops `IsSessionLocked`/`IsPaused` and gains `LastInputs: StepInputs` (the
previous call's levels, for edge derivation and cached-status computation). `LastInputs`
lives inside opaque `State` — not in a shell-held (previous, current) parameter pair —
so it has exactly one writer, `step` itself, and can never desync from what `step` last
saw. `Policy.start` takes the initial `StepInputs` (the shell samples truth before the
first step; restart pause-inheritance becomes ordinary input passing) and computes the
initial `CachedStatus` from those inputs — a restart-while-paused process reports
`Paused` from its very first render, never a one-frame `AcquiringCamera` flash.
`Policy.start` initializes `LastInputs` to those same initial inputs **verbatim** —
pinned, not left to inference (round 3), because the edge rule's first-call correctness
depends on it: a restart-while-locked process whose `LastInputs` began as anything else
(a default would read "unsuppressed") would evaluate `suppressed LastInputs` as false
and silently skip the baseline reset on the first real unlock it observes — a
single-call miss landing exactly on the restart-while-suppressed path this RFC treats
as first-class everywhere else. A core test pins it from both directions: `start` with
suppressed inputs followed immediately by an unsuppressed `Reconcile` fires the edge
reset; `start` with unsuppressed inputs followed by an unsuppressed call fires none.

### Suppression and the single edge rule

Define `suppressed inputs = inputs.SessionLocked || inputs.Paused`. The entire former
truth table collapses into one edge rule, evaluated at the top of EVERY `step` call,
before event dispatch:

- On the suppressed→unsuppressed edge (`LastInputs` suppressed, current inputs not):
  apply the baseline reset (disarm, re-baseline grace/away, reset signal-health) — the
  same `applyBaselineReset` as today — then dispatch the event against the reset state.
- On the unsuppressed→suppressed edge: nothing (armed/away/signal-health survive,
  matching today's pause and lock semantics).
- While suppressed (current inputs suppressed): `Sample` events are no-ops with respect
  to decision state and `Action` (unchanged semantics).

Invariant, structural rather than pinned by prose (round 2): every `step` call — every
event, suppressed or not, no-op or not — ends by recording the current inputs into
`LastInputs` and recomputing `CachedStatus`. "No-op" refers to decision state (armed,
baselines, signal health, stamps) and the action, never to `LastInputs`/`CachedStatus`.
This is what makes the edge rule sound: an edge can be *delayed* by at most one call,
never *lost*, because both sides of the comparison are re-supplied fresh on every call.
Because a per-arm discipline ("each of ten match arms remembers the trailing
bookkeeping") would be the Motivation's own defect shape one level down, the invariant
is enforced by construction: `step` becomes a thin wrapper — edge derivation and
baseline reset first, then the while-suppressed `Sample` no-op (hoisted out of the
per-event match into the wrapper, round 3: with it, all three bullets of the edge rule
are wrapper-owned and `dispatch` is *fully* suppression-blind, instead of "blind except
one `when` guard" — today's `when state.IsPaused || state.IsSessionLocked` guard on the
`Sample` arms is deleted, not ported), then a private `dispatch` holding the per-event
match (which never touches `LastInputs`/`CachedStatus` and never reads suppression),
then a single tail that writes both. `step`
is thereby the *only* writer of `LastInputs` and the *only* caller of the status
computation — cardinality by structure, the same reasoning that made `StepResult` a
type rather than a convention. (Today's `Policy.fs` ends every arm with its own
`withRecomputedStatus` call; slice 1 subsumes those into the wrapper tail.)

```fsharp
let step (config, state, ctx: StepContext, event) : StepResult =
    let edge = suppressed state.LastInputs && not (suppressed ctx.Inputs)
    let state' = if edge then applyBaselineReset ctx.Now state else state
    let result =
        match event with
        | Event.Sample _ when suppressed ctx.Inputs -> { State = state'; Action = Action.NoAction }
        | _ -> dispatch (config, state', ctx, event)  // per-event match only
    { result with
        State =
            { result.State with LastInputs = ctx.Inputs }
            |> withRecomputedStatus (config, ctx.Now) }
```

There is no pause-precedence interaction to specify: precedence exists only in status
presentation (below). Failure and restart events (`InitFailed`, `CaptureFailed`,
`BetterCameraAvailable`) remain level-immune exactly as they are pause-immune today
(0001-core-brain.md R2-10): the camera's death is a fact regardless of suppression.

### Lock inhibition

`LockInhibited` is a **fire-time gate**, symmetric with the input-idle gate: the away
clock keeps measuring truth; the gate vetoes only the `Lock` action at the moment it
would fire. Consequences, by construction:

- Inhibitor active + user absent past threshold: no lock, away clock saturated.
- Inhibitor clears with user still absent: `Lock` fires on the next `Sample` (lock is
  re-derived per property 9, never latched). The `Reconcile` fired on the clearing
  transition updates status promptly, but the lock itself waits for the next `Sample` —
  bounded by one sample interval, deliberately: only a sample knows current presence.
  The bound assumes sampling stays unsuppressed through that interval (round 3): a
  session lock or pause intervening defers the lock to the next unsuppressed sample,
  and the exit edge re-baselines the away clock — benign, since the intervening
  suppression is itself protection (the session locked) or explicit intent (a pause).
- Inhibition never disarms, never re-baselines, never touches signal health.

Considered and rejected: a shell-side veto (core still emits `Action.Lock`, shell
declines to execute it). That would recreate the exact "silently cannot lock" pattern
the Motivation names — the core's state, status, and log would all claim a lock fired
that never did. The gate belongs where the decision lives.

### Status

`Status` keeps its **six cases** and the 0001-core-brain.md priority table unchanged —
rows 1 and 2 now read from `LastInputs` instead of the deleted folded flags:

| Priority | Status | Condition |
|---|---|---|
| 1 | Paused | `inputs.Paused` |
| 2 | SessionLocked | `inputs.SessionLocked` (and not paused) |
| 3 | Recovering | pre-first-success, `InitFailStreak >= 1` |
| 4 | AcquiringCamera | pre-first-success otherwise |
| 5 | NoSignal | bad signal past `NoSignalReportAfterMs` |
| 6 | Watching | otherwise |

There is deliberately **no seventh `Inhibited` row**. Inhibition is not a lifecycle
phase: it can co-occur with any row (media can play while the camera is dead), so a
priority row would (a) force a contentless ranking against `NoSignal` — nothing in the
*system* needs one to outrank the other, only a single rendered line would — and
(b) hide inhibition whenever any higher row applies, which is precisely the "silently
cannot lock" opacity this RFC exists to eliminate. It would also add a case to every exhaustive
C# switch over `Status`: `PolicyBridge.ExpectsSampling` ends in `default: throw new
UnreachableException()`, so a seventh case would crash the watchdog on its first
inhibited tick.

Instead, inhibition is an **orthogonal projection**, cached alongside status:

```fsharp
// = LastInputs.LockInhibited as of the most recent step; cached like Status.
val lockInhibited : State -> bool
```

The projection's sole consumer is the verification harness: property 13 asserts it agrees
with `inputs.LockInhibited` — real regression coverage for the `LastInputs` invariant.
There is deliberately **no product-code consumer**. Product-side inhibition diagnostics —
the watchdog-tick transition log ("lock inhibitors active/cleared") and the `StatusText`
annotation — are sourced from the inhibitor registry's own cached snapshot, not from this
core echo (see the render-path note below). (An earlier draft also listed "diagnostic
logging" as a consumer of the projection; that role is filled by the registry-sourced
logging instead, so the projection earns its keep purely through the property-13 invariant
check, not through any product read.) The *render path* deliberately does not round-trip
through it:
the shell's `StatusText` reads the inhibitor registry's own cached snapshot directly for
**both** halves of the annotation — the active bool and the provider names — so one
annotation has one owner instead of sourcing its bool from a core echo of a value the
shell itself supplied and its names from the shell. (`Advance` calls `step` and the
re-render synchronously back-to-back, so the two sources are identical at render time
either way — this is an ownership-clarity decision, not a correctness one. Unlike
`Paused`/`SessionLocked`, whose render truth must come from core because `Status`
priority genuinely arbitrates them against other core state, `LockInhibited` never
participates in core-internal arbitration.)

The shell's `StatusText` appends an annotation — e.g. "Watching · lock inhibited
(media-playing)" — whenever the registry snapshot is active, *regardless of which row is
showing*, with active provider names supplying the detail (shell-side enrichment, same
precedent as the dark-frame distinction). So `NoSignal` and inhibition are visible simultaneously
instead of one hiding the other, and a vetoed lock is always visible (the recurring
lesson of this project: a silent cannot-lock breeds "why didn't it lock" incidents). The
annotation shows whenever an inhibitor is active, not only when a lock is imminent:
transparency is the point, the primary row still reads `Watching`, and an annotation
that appeared only while the user is away would be one nobody ever sees.
`ExpectsSampling`, `IsAcquiringOrRecovering`, and every other total match over `Status`
need no new arm. `CachedStatus` semantics unchanged (computed from `LastInputs`).

## Shell changes

### Session mirror with reconciliation

The shell holds a `sessionLocked: bool` mirror — a **new field** (today the shell has no
such bool; the fold lives inside core `State`, which is exactly what this RFC deletes) —
updated by `SessionSwitch` events (the fold moves to the thinnest possible layer,
adjacent to the OS boundary), and **reconciled against ground truth on every watchdog
tick** via `WTSQuerySessionInformation` (`WTSSessionInfoEx` → `SessionFlags`,
`WTS_SESSIONSTATE_LOCK`/`_UNLOCK`). A missed SessionSwitch edge now self-heals in
bounded time instead of persisting forever — worst case one SessionSwitch-adjacency
skip window plus two hysteresis ticks ≈ **three watchdog intervals** (~15 s at the 5 s
default), stated precisely because "within one interval" would be the same class of
spec-claims-a-bound-the-mechanism-doesn't-deliver error the Motivation describes.

Considered and rejected: a fold-free design querying WTS fresh on every `Advance`,
matching `InputIdleMs`'s treatment. `GetLastInputInfo` is a cheap local read;
`WTSQuerySessionInformation` is an RPC-backed call on the UI thread at sample cadence
(~500 ms), can transiently fail, and (per the hysteresis rationale below) can lag a real
transition — so a fresh-query design would need the same staleness defenses anyway,
paying the RPC cost per sample for no added correctness. Fold-plus-reconciliation keeps
the prompt path event-driven and bounds divergence at the watchdog cadence.

Pinned details:

- **Startup**: the constructor queries WTS synchronously before building the initial
  `StepInputs` for `Policy.start` — the *first* sample is not exempt from
  sample-and-reconcile. (Without this, a restart-while-locked — `CaptureFailed` and
  `BetterCameraAvailable` restarts are deliberately not gated by session state — would
  run unsuppressed with a wrong tray status for up to one watchdog interval.)
- **Fail-open**: if the WTS query fails (RPC error, invalid handle), the mirror keeps
  its current value and reconciliation is skipped, with a rate-limited log line. A
  failed query never *sets* `SessionLocked = true`: defaulting broken data to "locked"
  would silently suppress protection — the fail-closed dim-light mistake in new clothes.
- **Hysteresis**: a correction is applied only after two consecutive watchdog ticks
  disagree with the mirror, and reconciliation is skipped entirely within one watchdog
  interval of a `SessionSwitch`-driven update. `SessionFlags` can lag a real transition;
  without hysteresis a stale read could "correct" a correct mirror every tick, each
  spurious unsuppress edge re-baselining the away clock — a new "silently cannot lock"
  trap, the very class this RFC exists to kill. The two-tick count and the skip window
  are deliberately hard-coded, not tunables: they trade correction latency against
  false-correction risk at the mechanism level, have no user-meaningful unit, and
  changing them requires touching the tests that pin them anyway. Two boundary rules,
  pinned (round 3) because each is otherwise a
  spec-claims-a-bound-the-mechanism-doesn't-deliver hole: (1) a failed WTS query
  **holds** the consecutive-disagreement counter — it neither counts toward it nor
  resets it — so a transient failure costs at most one extra tick; a reset-on-failure
  reading would let an intermittent-failure pattern (disagree, fail, disagree, fail, …)
  starve the correction forever, making the self-heal unbounded under a failure mode
  this RFC itself names as real. The three-interval bound is therefore conditional on
  bounded consecutive query failures, each failure extending it by one tick — stated,
  not implied. (2) The skip window restarts only on a `SessionSwitch`-driven update
  that **changes** the mirror value: Windows demonstrably double-fires `SessionSwitch`
  (0001-core-brain.md), and an idempotent re-delivery restarting the window could push a
  stuck mirror's heal out indefinitely under unrelated session traffic — the bound
  would silently depend on quiet SessionSwitch weather.
- **Pure decision function, unit-tested**: the reconciliation decision — given the
  mirror, the queried value, whether the query succeeded, ticks since the last
  `SessionSwitch`-driven update, and the consecutive-disagreement count, produce
  (new mirror, new count, correction-applied) — is extracted into a pure named function
  in `PolicyBridge` (e.g. `ReconciliationStep`), mirroring the existing
  `SamplingWatchdogStep` precedent, with a dedicated `PresenceLock.Tests` class
  covering: fail-open on query failure, correction on exactly the second disagreeing
  tick, a failed query interleaved between two disagreeing ticks (counter held, one
  extra tick — never a restarted count), an idempotent duplicate `SessionSwitch` not
  restarting the skip window, skip-window suppression, and the compounding
  skip-plus-hysteresis worst case behind the three-interval bound above. Without this, a several-branch state machine's
  only verification would be one manual live-smoke observation — materially weaker
  coverage than everything else this RFC hardens, on the mechanism closest to the
  original incident.
- **Corrections drive behavior, not just the bool**: a reconciliation correction runs
  the same path as a live `SessionSwitch` — mirror update, then `Advance(Reconcile)`.
  Timer stop/start and `KickAcquisitionIfNeeded` are deliberately *not* the call
  site's job: they ride the `Advance`-internal suppression-transition rule (round 3 —
  see Advance and timers), so a correction gets them by construction rather than by a
  hand-copied chore. Healing the fact without healing the behavior would leave
  sampling frozen behind a truthful mirror — the "silently cannot lock" trap rebuilt
  through the very mechanism built to close it — and a forgotten per-site timer
  restart is invisible to the verification harness, which models `step` semantics, not
  WinForms timers; hence the rule lives in `Advance`, and a slice-4 shell test pins
  that sampling resumes after a WTS-correction-driven unlock. Each correction logs a
  warning (a correction is itself a signal worth seeing).
- **Spike deliverable, before wiring**: `SessionFlags` semantics inverted between
  Windows 7 and later versions; verify and document the constant's meaning on Windows 11
  (a live lock/unlock cycle with the reconciliation log inspected) *before* the mirror
  feeds live `StepInputs` — "document" meaning edits to this RFC's pinned details in
  the slice-4 commit, not a commit message or the handoff (round 3). Wired in inverted, reconciliation would report
  locked-while-unlocked — the incident class itself, silently. While there, observe
  fast-user-switch/RDP behavior (`WTSSessionInfoEx` often marks disconnected sessions
  locked) and update 0001-core-brain.md's fast-user-switching known-limitation note
  either way.
  **Spike results (2026-08-31, Windows 11 26200, live console-session probe):** the
  documented Win8+ semantics hold — a demonstrably unlocked session (no LogonUI
  process) reports `SessionFlags = 1` (`WTS_SESSIONSTATE_UNLOCK`) and a demonstrably
  locked one (LogonUI present) reports `SessionFlags = 0` (`WTS_SESSIONSTATE_LOCK`);
  no Windows-7-style inversion. One marshaling trap observed live, now pinned: the
  `WTSINFOEX` union holds `LARGE_INTEGER`s, so `Data` is 8-byte aligned — `Level` at
  offset 0, four padding bytes, `SessionId`@8, `SessionState`@12, `SessionFlags`@16.
  A naive offset-4 read returns `SessionState` where `SessionFlags` is expected and
  reads as a plausible wrong answer (the probe's own first version made exactly this
  error; `WTSActive = 0` masquerades as `WTS_SESSIONSTATE_LOCK`). A full unlock
  transition was also observed live: `SessionFlags` flipped to 1 roughly two seconds
  before the LogonUI process exited — direct evidence that WTS state and UI-visible
  lock state skew around transitions, supporting the two-tick hysteresis and the
  SessionSwitch-adjacency skip window. The
  fast-user-switch/RDP observation requires a second interactive session and is
  deferred to slice 6's live smoke (recorded in the handoff as an open item, not
  silently dropped).

### Pause ownership

`TogglePause` flips a shell-owned bool and calls `Advance(Reconcile)` — the pause takes
effect and renders synchronously with the click, as today. Durability stays
**restart-scoped**, exactly as pinned in 0001-core-brain.md: only `RestartProcess()`
persists the flag (writing the mirror's current value), and startup consume-and-clears
the file into the mirror before the first `Policy.start`. An ordinary toggle never
touches disk; a tray Exit or a reboot never inherits a stale pause — for a
lock-enforcement app, pause silently surviving a Windows-Update reboot and leaving the
machine unguarded indefinitely is the worse failure, and that accepted-risk analysis is
inherited unchanged. What is deleted is only the *refeed* half of the old protocol
(`ConsumePersistedPausedFlag` feeding an `Event.Paused` injection): inheritance is now
ordinary input passing into `Policy.start`. `RestartProcess` reads the shell's own pause
mirror directly — it no longer derives pause by round-tripping through `Policy.status`.

### Advance and timers

`Advance` builds `StepInputs` from the mirrors + the inhibitor aggregate + a fresh
`GetLastInputInfo` read on every call. Every mirror-changing site — `TogglePause`,
`OnSessionSwitch`, a WTS reconciliation correction, an inhibitor-aggregate transition —
calls `Advance(Reconcile)` synchronously, immediately after updating its mirror. That
call is what makes edges prompt: baseline resets are timestamped at the real transition
(today's synchronous semantics, preserved), and tray text / `KickAcquisitionIfNeeded`
are never stale. Timer management (stop sampling while paused/locked, restart on
resume/unlock — unconditionally, per the 1.0.8.4 fix) keeps its semantics but moves
**inside `Advance`** as part of the suppression-transition rule below (round 3); the
core no longer depends on it for flag correctness, and no mirror-changing call site
carries its own copy. The `SampleAsync` status guard
and the watchdog's `ExpectsSampling` keep their `Status`-based signatures unchanged —
no new `Status` case exists, and rows 1–2 are now level-derived, so they read the same
truth — and `ExpectsSampling` returns `true` while inhibited-and-watching by
construction: inhibition never appears in `Status`, and sampling must continue while
inhibited (the away clock still needs data).

**Suppression-exit shell resets, generalized (round 2)**: today's unlock/resume paths
also reset `presenceFilterState`/`frameFreshnessState` to `.initial` — the 2026-07-28
burn-in fix: spatial coherence measured against a previous watching episode's frames
must never carry into a new one — and that reset condition is currently hand-copied at
the call sites (`OnSessionSwitch`'s unlock branch, `TogglePause`'s resume branch). With
levels, those triggers collapse into one condition the shell can observe uniformly:
whenever any `Advance` call sees `previousStatus ∈ {Paused, SessionLocked}` and
`newStatus ∉ {Paused, SessionLocked}` (the same before/after pair `Advance` already
computes for the NoSignal transition log line), it resets
`presenceFilterState`/`frameFreshnessState`, restarts the sample timer, and runs
`KickAcquisitionIfNeeded`; on the reverse transition it stops the sample timer —
implemented **once, inside `Advance`**, never per mirror-changing call site. Round 3
extends the round-2 rule to the timers: same defect shape, same fix — a
WTS-correction unlock that healed the mirror and reset the filters but left the
sample timer stopped would show a truthful `Watching` tray over a frozen pipeline, the
incident class again. Startup timer state stays constructor-owned (there is no prior
status to compare). This matters most for the WTS-correction path,
which is new, rare, and least likely to be caught manually: a locked→unlocked self-heal
that healed the mirror but skipped the filter reset would resume sampling against a
pre-suppression coherence baseline — the burn-in incident class through a codepath no
one watches. The per-call-site copies — filter resets and timer stop/start/kick alike —
are deleted in slice 1 (the `Advance`-internal rule subsumes them).

**Watchdog tick composition order, pinned**: within one watchdog tick, WTS
reconciliation runs first, then inhibitor `Refresh()`/aggregation (each firing its
`Advance(Reconcile)` on a change), and only then does `SamplingWatchdogStep` read
`Policy.status` — so the sampling-starvation verdict always reflects the current tick's
corrected truth. (Core correctness is order-independent — every `Advance` re-samples
fresh inputs — but the watchdog's shell-side verdict is order-sensitive for one tick,
and this RFC pins ordering wherever ordering matters. Until slice 5 lands there is no
registry to refresh, and the tick is simply reconcile → `SamplingWatchdogStep`.)

**Threading invariant, pinned (round 3)**: the single-writer soundness of the mirrors,
the registry snapshot, and `State` itself rests entirely on every mirror mutation,
registry access, and `Advance` call executing on the UI thread — the existing
`ui.Post` discipline (`SessionSwitch`, `DeviceWatcher`) stated as a rule rather than
left as a pattern: any async continuation that touches a mirror, the registry, or
calls `Advance` must resume on the UI `SynchronizationContext` — never
`ConfigureAwait(false)`, never a raw thread-pool callback. (The SMTC acquisition
continuation in slice 5 is the first new code this rule governs; a stray
`ConfigureAwait(false)` added by someone guarding against STA deadlock — ironically
this RFC's *other* named async hazard — would reintroduce a genuine data race no test
catches.) `Advance` opens with a `Debug.Assert` on the WinForms synchronization
context as the fail-loud backstop. The same synchrony underwrites the Status section's
registry-snapshot render argument: `Advance` calls `step` and the re-render
synchronously back-to-back, and any future render throttling must preserve that
pairing or the annotation's two halves regain two owners.

`GetLastInputInfo` note: `Advance` reads idle fresh on every call, and
`LogSensingDiagnostics` in `SampleAsync` logs its own read — two P/Invoke calls per
sample tick, accepted explicitly: the call is a cheap local read, and the two values
may differ by a few milliseconds without consequence (the diagnostic line is prose, the
decision input is `Advance`'s own read). Not worth threading a value through the call
graph to deduplicate.

### Inhibitor registry

```csharp
// The entire extension surface for future gates (presentation mode, quiet hours,
// on-battery profiles): one named, cached level per provider.
sealed class LockInhibitor(string name, Func<bool> query)
{
    public string Name { get; } = name;
    public bool Active { get; private set; }   // read by Advance — cheap, no I/O
    // Called ONLY from the watchdog tick. Fail-open by contract: a throwing query
    // (WinRT/COM reads can die with a zombie session — the RFC's own named failure
    // mode) is caught, logged rate-limited (WTS-reconciliation precedent), and
    // reported inactive. "Assume uninhibited" is the safe direction for an inhibitor:
    // "assume active forever" would be the silent-cannot-lock trap in new clothes,
    // and an escaped exception would take down the entire watchdog tick handler on
    // the UI thread — WTS reconciliation and the sampling watchdog with it.
    public void Refresh()
    {
        try { Active = query(); }
        catch (Exception ex) { Active = false; Log.WriteRateLimited($"inhibitor {Name} query failed: {ex.Message}"); }
    }
}

// The aggregate owner round 1's sketch left implicit: something must compute Any,
// remember last tick's value, and detect transitions. Detect and act stay separate —
// the registry reports "changed"; the caller (the watchdog tick) owns logging and
// Advance(Reconcile). Aggregation/change-detection is a PURE function (round 3) — the
// ReconciliationStep/SamplingWatchdogStep house precedent applied to this RFC's own
// new code: slice 5's "shell tests for the pure aggregation pieces" target Compute
// directly with plain tuples, no LockInhibitor/Func<bool> fakes; Refresh keeps only
// the I/O fan-out and the field writes.
internal readonly record struct InhibitorAggregate(bool Active, IReadOnlyList<string> ActiveNames);

internal static class LockInhibitorAggregation
{
    internal static (InhibitorAggregate Aggregate, bool Changed) Compute(
        IReadOnlyList<(string Name, bool Active)> providers, bool previousActive)
    {
        var names = providers.Where(p => p.Active).Select(p => p.Name).ToList();
        bool active = names.Count > 0;
        return (new InhibitorAggregate(active, names), active != previousActive);
    }
}

sealed class LockInhibitorRegistry(IReadOnlyList<LockInhibitor> providers)
{
    public bool Active { get; private set; }
    public IReadOnlyList<string> ActiveNames { get; private set; } = [];

    public bool Refresh()   // called ONLY from the watchdog tick; returns "aggregate changed"
    {
        foreach (var p in providers) p.Refresh();
        var (agg, changed) = LockInhibitorAggregation.Compute(
            [.. providers.Select(p => (p.Name, p.Active))], Active);
        (Active, ActiveNames) = (agg.Active, agg.ActiveNames);
        return changed;
    }
}
```

The cached/slow-cadence discipline lives in the types, not in prose: `Advance` reads
`.Active` at any cadence; the watchdog tick is the sole caller of `Refresh()`. (A bare
`(string Name, Func<bool> IsActive)[]` registry was considered and rejected: a live-query
shape gives no cue that SMTC must not be queried on every sample tick — the discipline
would live in a comment.) On `Refresh()` returning true, the watchdog tick logs the
transition ("lock inhibitors active: media-playing" / "lock inhibitors cleared") so the
log always explains a non-lock, then fires `Advance(Reconcile)`. `StatusText` reads
`Active`/`ActiveNames` off the same cached snapshot — one owner for both halves of the
annotation, no second query for display (see Status).

### Media provider (first inhibitor)

`GlobalSystemMediaTransportControlsSessionManager` (WinRT, user-mode, no elevation):
active iff any media session reports `PlaybackStatus == Playing`. Browsers surface
YouTube/Netflix playback there; Spotify and native players likewise. The manager is
acquired **once, asynchronously, off the tick path** (`RequestAsync` awaited at startup
or first use); `Refresh()` then performs only synchronous property reads over cached
session objects — never a blocking wait on a WinRT async call from the synchronous
watchdog `Tick` handler (a classic STA sync-over-async deadlock). File-only kill switch:
`MediaInhibitorEnabled` (default `true`), same no-Settings-UI precedent as the existing
file-only tunables — this feature deliberately weakens the lock, so it gets an off
switch. A late-completing acquisition continuation after tray Exit checks the existing
shutdown/exiting state before touching any field and no-ops — the same guard class as
`RestartProcess`'s audited ordering, one line, stated so it isn't invented mid-slice.

**Spike deliverable, before enabling by default** (round 2 — the WTS mirror gets a
pre-wiring spike for its OS-semantics unknowns; SMTC has two of the same class, plus
one packaging unknown added in round 3):
(a) confirm the once-acquired manager still enumerates and reports correctly after a
sleep/resume cycle — media sessions may be torn down and recreated on resume, and if
the cached manager goes stale the level silently sticks, in either direction; (b)
observe whether session enumeration is scoped to the calling session or leaks another
session's playback under fast user switching (a leak would falsely inhibit the console
session's lock — likely moot while the other session holds the screen, since
`SessionLocked` suppression outranks inhibition, but observed rather than assumed);
(c) round 3 — confirm no `AppxManifest.xml` capability declaration is required for
`RequestAsync` under `runFullTrust` (the manifest today declares only `runFullTrust`
and `webcam`; an unexpectedly required capability is a manifest/repackage ripple to
catch here, not mid-slice). Record the answers as known-limitation notes alongside the
ones below — edits to this RFC's list, in the slice-5 commit, never commit-message-only;
if (a) shows staleness, re-acquire the manager on resume (one
`SystemEvents.PowerModeChanged` hook) rather than shipping a sticky level.
**Spike results so far (2026-08-31, installed 1.1.0.0):** (c) confirmed — the packaged
app (manifest declaring only `runFullTrust` and `webcam`) acquired the SMTC manager and
detected live playback with no additional capability; the inhibitor activated within
one watchdog tick of playback starting and cleared within one tick of the player
pausing, both logged. (a) sleep/resume and (b) fast-user-switch scoping remain pending
an interactive session (tracked in the handoff's open forks).

Known limitations, accepted:

- Teams/Zoom calls do not appear in SMTC; a future `presentation` provider
  (`SHQueryUserNotificationState`) covers that class with one registry line.
- Muted or background autoplay still reports `Playing` — a false positive that inhibits
  the lock with nothing actually attended. SMTC carries no volume/mute signal; a
  volume-aware refinement would need the WASAPI audio-session APIs and is deferred. The
  kill switch and the always-visible status annotation are the v1 mitigations.
- The cached level can be up to one watchdog interval stale in either direction: a lock
  may fire in the first seconds of playback, and a stopped video may inhibit a few
  seconds longer. Zombie SMTC sessions that linger after playback ends (seen with
  browsers) would stick the level active — the transition log is the designated
  detection mechanism for exactly that failure.

## Verification harness (in PresenceLock.Core.Tests)

A test-side **environment model**: ground truth (`OsLocked`, `Paused`, `CameraAlive`,
`MediaPlaying`, `FacePresent`, `InputIdle`) plus a legal-action alphabet (`OsLock`/
`OsUnlock` alternate; pause toggles require an unlocked session — the tray is
unreachable through a lock screen; `Tick` advances time from a representative delta set
and fires `Sample` exactly when the modeled shell would). Model fidelity is pinned to
the shell's own discipline: every level-changing action (`OsLock`, `OsUnlock`, pause
toggle, media toggle) calls `step` with `Reconcile` synchronously at the transition —
the same `Advance(Reconcile)` contract the shell implements — so the model can neither
hide nor invent staleness the real shell doesn't have. `apply` closes the loop on
actions: `Lock` sets `OsLocked` and is reflected in the next inputs; `Restart` simulates
the documented process handoff (fresh `Policy.start` with carried stamps and current
levels).

Scope, stated precisely: the explorer holds the recovery axes fixed —
`HasSucceededOnce = true`, `InitFailStreak = 0`, all restart stamps unset — and the
model config places `ReevaluateAfterMs` and every cooldown beyond the modeled horizon,
so restart machinery stays quiescent (`apply`'s `Restart` handling exists for totality
but is unreachable in the explored graph). Recovery is excluded because
0001-core-brain.md's properties 5/6/9/12 already own it; "every reachable state" below
means reachable under this alphabet with those axes fixed — a precise claim, not an
overclaim. `CameraAlive = false` drives `NoFrame` observations, so the bad-signal clock
*is* exercised; its buckets are bounded by `NoSignalReportAfterMs` only (the reevaluate
threshold sits beyond the horizon, so no second threshold needs its own bucket).

Checks:

1. **Property 13 — status/level agreement**: after every step, the **full six-row
   priority table** holds against the current levels and state — not just the
   `Paused`/`SessionLocked` rows — and `Policy.lockInhibited` agrees with
   `inputs.LockInhibited`. The table is an artifact this RFC argues for in prose;
   without asserting all of it, a silent priority regression passes every other check.
2. **Property 14 — protection liveness**: from *every* reachable state, driving the
   environment to (unlocked, unpaused, uninhibited, camera alive, face seen to arm, then
   absent + idle past the away threshold) produces `Action.Lock` within bounded ticks.
   This is the app's purpose as a property; it catches any future absorbing
   cannot-lock trap in the fields the drive doesn't overwrite. Scope, stated precisely
   (round 2): for a *suppressed* source state the drive necessarily begins with an
   unlock/resume, whose edge reset overwrites exactly the fields the subsequent lock
   decision reads (`Armed`, grace/away baselines, `BadSignalSince`) — so for those
   states this property verifies the reset itself and the post-reset trajectory, not
   corruption of fields the reset masks by construction. The complementary check is
   direct: the explorer asserts, at every suppressed→unsuppressed edge it takes, the
   reset's full postcondition (disarmed, both baselines = now, `BadSignalSince` clear)
   — so a broken or partial reset is caught at the edge itself, not inferred through
   liveness.
3. **Inhibitor semantics**: absent + idle + past-threshold + inhibited never locks;
   the same state with the inhibitor cleared locks on the next sample.
4. **Exhaustive explorer**: BFS over the reachable (env × abstracted core state) graph —
   clock values quotiented to buckets (zero / mid / past-threshold), deduplicated,
   depth-bounded — asserting 1–3 at every node, plus the edge-reset postcondition from
   property 14's note at every suppressed→unsuppressed transition, and printing the
   full action trace on any violation. The abstraction and the delta set are pinned,
   not improvised mid-slice (round 3): the dedup key is (`Armed`, grace bucket, away
   bucket, bad-signal bucket, input-idle bucket) × the env-truth tuple ×
   `'aux`, each clock bucketed against its single governing threshold (`GraceMs`,
   `AwayThresholdMs`, `NoSignalReportAfterMs`, `InputIdleRequiredMs`). The bad-signal
   bucket is a plain `ClockBucket`, not a `ClockBucket option`: `BadSignalSince = None`
   and `Some now` are observationally identical through the opaque `State` (both bucket to
   `Zero` and drive `dispatch` the same way), so the `None`/`Some` distinction adds no
   node identity and collapsing it keeps the key a flat record without shrinking the graph.
   `Tick`'s delta
   set is derived boundary-value style from `Cadence` plus the model config — for each
   modeled threshold, one delta landing just below it and one at or past it — so delta
   granularity and bucket boundaries agree by construction (too-coarse deltas would
   make a "mid" bucket unreachable and silently shrink the explored graph while still
   reporting "every reachable state" green). The incident-sequence claim is pinned to a
   concrete, cheap check (round 2): a standalone regression **replays**
   pause → lock → unlock → resume with a concrete time schedule and asserts (a) each
   resulting abstracted state is a member of the explorer's visited set and (b) the
   final state locks under the property-14 drive. Full path-provenance tracking inside
   the BFS is explicitly *not* required — it would fight the dedup that keeps the graph
   finite, and the replay checks the same thing directly. Two mutation checks, **both
   committed permanently as negative fixtures** — each mutant is a small test-local
   alternate implementation, never a production edit-and-revert. The seam is pinned
   (round 2): parameterizing over the step *function* alone cannot express the
   folded-flag mutant, whose defining state (`IsPaused`/`IsSessionLocked` booleans) no
   longer exists in `State` — the explorer is therefore generic over an auxiliary
   per-implementation state carried alongside `State`, with the BFS dedup key extended
   to include it:

   ```fsharp
   /// 'aux: unit for the real step; folded booleans for the folded-flag mutant; a
   /// skipped-write marker for the stale-LastInputs mutant. Dedup key = abstracted
   /// State * 'aux, so each mutant gets correct-for-its-shape node identity.
   type StepUnderTest<'aux> =
       { Init: MonotonicMs -> RestartStamps -> StepInputs -> State * 'aux
         Step: PolicyConfig -> State * 'aux -> StepContext -> Event -> StepResult * 'aux }
   ```

   - a folded-flag variant (the historical mechanism): the explorer must print the
     incident trace as its counterexample — the deleted session/pause events no longer
     exist to fold from, so this mutant derives its own edges from consecutive
     `StepContext.Inputs` via aux-held previous inputs (round 3: stated so its
     construction needn't be reverse-engineered mid-slice);
   - a variant that skips the `LastInputs` update while suppressed (the new mechanism a
     levels design could fail by): the explorer must produce the stale-edge
     counterexample.

Properties that cannot fail naturally post-fix follow the repo's established
mutation-check protocol.

Cadence knowledge, structurally shared rather than comment-guarded (round 2): the
static cadences — the watchdog interval (which is also the inhibitor poll cadence, since
`Refresh()` rides the watchdog tick) and the default sample interval — move into a small
`Cadence` module in `PresenceLock.Core` — its own `Cadence.fs`, deliberately *not*
beside `PolicyConfig` in `Types.fs` (round 3), with a doc comment pinning that
`step`/`start` take no dependency on it: `PolicyConfig` feeds decisions; `Cadence`
exists only so `Program.cs` and the model read one truth, and adjacency to
`PolicyConfig` would invite conflating the two roles. `Program.cs` reads `Core.Cadence.WatchdogIntervalMs`
instead of a bare `5000`, and the model's `Tick` delta set derives from the same
constants — drift between shell and model becomes impossible by construction for
everything static. The genuinely irreducible remainder is `SampleIntervalMs`'s
*runtime* tunability: the model necessarily explores at one representative interval
(the shared default), and a user who retunes it at runtime is outside the modeled
cadence — accepted, and now the *only* accepted cost here.

## Out of scope

- Modeling dock churn / `BetterCameraAvailable` in the environment model (property 12
  already covers upgrade cooldowns; upgrade restarts do not interact with levels).
- Additional inhibitor providers beyond media playback (the registry is the extension
  point; each future provider is its own small slice).
- Any change to camera lifecycle, FrameServer invariants, PresenceFilter, FrameFreshness,
  or restart/cooldown machinery.

## Slices

1. **Core contract swap + mechanical shell shim** — one slice, deliberately: the shell
   already live-references every signature this slice changes, and `pack.ps1` hard-gates
   shell tests and the publish, so a core-only slice would leave the tree red until
   slice 4.
   - Core: `StepInputs`, `StepContext`, `LastInputs`, event DU shrink + `Reconcile`,
     suppression edge rule factored as the wrapper-around-`dispatch` composition (the
     structural `LastInputs`/`CachedStatus` invariant — the per-arm
     `withRecomputedStatus` calls are subsumed into the wrapper tail), `Policy.start`
     signature (initial inputs; `LastInputs` initialized to them verbatim, pinned by
     the first-call edge test; initial status computed from them), `lockInhibited`
     projection.
   - Shell shim (mechanical only, but honestly scoped — round 2): **introduce** the
     `sessionLocked`/`paused` mirror fields (they do not exist today; the shell
     currently derives pause from `Policy.status`, e.g. `TogglePause`'s
     `status.Tag == Paused` branch) and rewire `TogglePause`/`OnSessionSwitch` onto
     them — including `OnSessionSwitch`'s unlock-branch pause-precedence guard
     (Program.cs:602, the second `Policy.status` pause round-trip of the same shape —
     round 3), which is subsumed outright rather than rewired: pause precedence is now
     the core edge rule plus the `Advance`-internal transition rule, so the guard is
     deleted; collapse the constructor's start-then-`Advance(Event.Paused)`
     pause-inheritance sequence into consume-flag → build initial `StepInputs` → one
     `Policy.start`; the five `Advance(Event.SessionLocked/…)` call sites become mirror
     updates + `Advance(Reconcile)`; the `step`/`start` call sites gain
     `StepContext`/initial inputs; `LockInhibited` hard-coded `false` until slice 5;
     `InputIdleMs` moves from the sample path into `Advance`'s input assembly (and out
     of `ClassifySample`'s signature); the two suppression-exit
     `presenceFilterState`/`frameFreshnessState` reset call sites (2 fields × 2 sites:
     `OnSessionSwitch`'s unlock branch, `TogglePause`'s resume branch) collapse —
     along with those sites' timer stop/start and `KickAcquisitionIfNeeded` — into the
     single `Advance`-internal suppression-transition rule (round 3, scoped precisely:
     the constructor's and `StartWatchingAsync`'s resets are fresh-acquisition resets,
     orthogonal to suppression, and remain untouched — deleting them would be exactly
     the silent unreviewed loss the Motivation warns about); the named-argument
     `StepInputs` construction rule plus its field-mapping flip test land in
     `PresenceLock.Tests` — first, before the mechanical migration pass, since it
     validates the C#-constructs-an-F#-record pattern every shim call site then
     relies on.
     No WTS, no registry, no persistence change yet — behavior identical, just
     re-expressed as levels.
   - This is a single migration slice, not decomposable RED-GREEN units — but "not
     TDD-decomposable" is not "not sequenceable" (round 3, correcting round 2's
     "intermediate green states are impossible": they aren't). Recommended order
     inside the slice: expand → migrate → contract. Add the new types,
     `Event.Reconcile`, and a temporarily distinct `stepLevels`/`startLevels`
     alongside the old shape (F# module `let` functions don't overload, hence distinct
     names renamed at the end; nothing outside `Policy.step` matches `Event`
     exhaustively and Core has no `WarningsAsErrors`, so the interim states compile);
     migrate all 170 Tests.fs call sites against the fast Linux loop; only then cut
     the shell over, delete the old events/fields/signatures, and rename — paying the
     slow, flaky Windows-container gate exactly once, at the end, with confidence
     already established. Test migration, counted against the actual suite (round 2): 120 `Policy.step`
     + 50 `Policy.start` call sites = 170 total; a `defaultInputs`/`levelsOf` helper
     makes the **~155** that never touch session/pause a mechanical append pass; the ~15
     event-keyed scenario/property tests are hand-rewritten. The C# suite has exactly
     one casualty — `ClassifySampleTests` (PolicyBridgeTests.cs:320), which asserts the
     deleted `Sample` idle payload — named here so the Windows-container gate's failure
     is accounted for in advance, not discovered mid-slice. Deliverable: an old-test →
     new-test mapping table (covering BOTH test projects) at one-row-per-test-function
     grain — 53 test functions in Tests.fs, not 170 call-site rows; seven fold-based
     properties each wrap one textual call site exercised unboundedly per FsCheck run,
     so call sites are the wrong unit (round 3) — with the mechanical majority
     collapsed into one summary row ("N tests: append `defaultInputs` via the shared
     helper, no assertion change") and individual rows naming each hand-rewritten,
     superseded, or deleted test with its replacement or deletion-with-reason — known
     hotspots: `applyPauseLockModel` (Tests.fs:1021)
     rewritten as a levels-based model; property 8 superseded by property 13; the
     session-event idempotency tests replaced by a level-idempotence property, scoped
     precisely (round 2): identical consecutive `StepInputs` trigger no edge and no
     baseline reset — decision-state fields other than those a `Sample`'s own
     observation legitimately changes are unaffected. This is compatible with, not a
     replacement for, property 9: lock re-derivation on repeated identical `Sample`s is
     a per-observation decision, not an edge effect, so "no-op" here never means "no
     second `Action.Lock`". The incident sequence becomes unrepresentable — tracer test
     proves the fixed point: unlock-while-paused followed by resume yields Watching.
2. **Verification harness** (swapped ahead of inhibition — round 2: slice 1 is the
   riskiest change in the RFC and the harness is its designated safety net; landing an
   unrelated feature between them would leave the incident-class detectors unbuilt
   while a second change stacks on the unverified contract): environment model with
   pinned `Reconcile`-at-transition fidelity, properties 13 (minus the `lockInhibited`
   agreement clause) and 14, the edge-reset postcondition check, the incident-sequence
   replay, exhaustive explorer over the `StepUnderTest<'aux>` seam with both committed
   mutation fixtures. Everything here is buildable against slice 1 alone —
   `LockInhibited` already exists in `StepInputs` (hard-coded `false` by the shim), and
   only the gate's semantics await slice 3.
3. **Inhibition in core**: `LockInhibited` fire-time gate, `lockInhibited` projection
   wiring, gate/clear-promptness tests (clear → lock on next `Sample`, never on
   `Reconcile`); extend the already-standing harness with the inhibitor-specific
   checks (property 13's `lockInhibited` clause; harness check 3) — extending a working
   harness, rather than building the whole harness on top of two unverified slices.
4. **Shell rewiring (real)**: WTS spike deliverable first (`SessionFlags` semantics on
   Windows 11, documented, live lock/unlock cycle inspected) *before* reconciliation
   feeds live inputs; reconciliation with fail-open + hysteresis +
   corrections-drive-behavior, its decision extracted as the pure `ReconciliationStep`
   in `PolicyBridge` with its dedicated test class (fail-open, exactly-2-tick,
   skip-window, held-counter-across-failure, duplicate-`SessionSwitch`-no-restart,
   compounding worst case); watchdog-tick composition order pinned as far as it exists
   this slice — reconcile → `SamplingWatchdogStep`, gaining its middle
   inhibitor-refresh step only in slice 5 (round 3: slice 4 has no registry to
   refresh); startup WTS query into
   `Policy.start`'s inputs, with the shell test pinning that no `StepInputs` is built
   before the WTS query and pause consume-and-clear complete; restart-scoped pause
   persistence (consume-and-clear retained, refeed deleted); `RestartProcess` reads the
   pause mirror; a shell test asserts sampling resumes after a WTS-correction-driven
   unlock (round 3: the harness models `step`, not WinForms timers — this leg only a
   shell test can pin); a shell test asserts every `Status` case renders without
   throwing (pinning that no guard needed a new arm). (Round 3: the `StatusText`
   inhibition annotation moved wholly to slice 5 — it sources from the registry
   snapshot, which does not exist until then.)
5. **Inhibitor registry + media provider**: `LockInhibitor` (fail-open `Refresh`
   exception contract) + `LockInhibitorRegistry` (aggregate, transition detection),
   transition logging + `Advance(Reconcile)` on aggregate transitions; the SMTC spike
   (sleep/resume manager validity, FUS/RDP session scoping) *before* the provider is
   enabled by default; async-once SMTC acquisition with synchronous cached reads and
   the shutdown-continuation guard; `MediaInhibitorEnabled` kill switch; status-text
   enrichment sourced from the registry snapshot; shell tests for the pure
   aggregation/classification pieces.
6. **Docs + release**: ARCHITECTURE.md rewrite of the affected sections; annotate
   0001-core-brain.md's now-historical prose in place — the session/pause truth table,
   the `Event` DU's four session/pause cases, the `State` sketch's folded flags, and
   property 8 — each with an inline "superseded by 0002-environment-levels.md
   (StepInputs/Reconcile)" pointer, so a reader of that doc alone cannot mistake
   superseded contract for current contract (the reverse direction of this RFC's own
   Supersedes header); version bump, pack, install, live smoke: media inhibitor
   (YouTube playing → walk away → no lock, status annotated; stop video → lock within
   a sample interval); a lock/unlock cycle with the reconciliation log inspected (no
   spurious corrections in normal use; a forced missed edge self-heals in the correct
   direction within the pinned three-interval bound); fast-user-switch behavior
   recorded against 0001-core-brain.md's known-limitation note; tray Exit → relaunch
   does not inherit a pause.

## Follow-ups (stage-4 review, 2026-09-01)

The stage-4 review reached the floor (0 Critical/High/Medium; see the handoff ledger for
the full per-finding record). These remain as real, deferred work:

- **Defeat-resistance for the SEC-1/SEC-4/SEC-2 same-user residuals → re-homed to
  0003-defeat-resistance-layering.md** (2026-09-01). The short version: an app-level
  self-protection service is the *wrong* answer (same-user code is already inside the boundary
  the screen lock defends), so real defeat-resistance belongs in an OS-enforced inactivity
  lock, and the only legitimate app-side remnant is a restart-only *reliability* watchdog that
  keeps failures visible. Full rationale, layering, and open questions live in RFC-0003.
- **Status annotation when config makes locking effectively unreachable.** The SEC-3 fix
  added upper-bound ceilings so an absurd `AwayThresholdSeconds` is rejected outright; a
  belt-and-suspenders annotation (surfacing "config out of range → defaults in use", the way
  the SMTC path annotates inhibition) was deferred.
- **SMTC false-positive hardening** (already an accepted known-limitation): muted/background
  autoplay, and a same-user process registering a phantom `Playing` session, both inhibit the
  lock. Kill switch + always-visible annotation are the v1 mitigations; a volume/attention-aware
  refinement (WASAPI) remains future work.
- **Doc/polish batch** (deferred Lows) — ✅ landed 2026-09-01 (2nd fix pass, suite green
  core 92/92 + shell 149/149): `starvationMs` extracted to
  `PolicyBridge.SamplingStarvationThresholdMs` + tests (DES-4); `NowMonotonic()` helper
  removes the 7-site boilerplate (DES-5); `Policy.lockInhibited` RFC prose corrected to
  harness-only, product diagnostics sourced from the registry snapshot (LIVE-1); explorer
  `BadSignalBucket` prose reconciled to `ClockBucket` with the None/Some(0)-equivalence
  rationale (RFC-1); the harness-Tick watchdog-cadence exclusion documented at `tickDeltas`
  (TEST-2); `Policy.start` `LockInhibited` verbatim-init pinned via the projection, and
  `InputIdleMs` found write-only-in-`LastInputs` so documented rather than tested (TEST-3);
  0001-core-brain.md restart-streak prose tightened (CORE-2). `OnSessionSwitch`'s redundant
  `Advance(Reconcile)` gated on `changed` and adversarially verified sound (SHELL-1).
  Remaining, deliberately kept: DES-2b (one throwaway alloc/startup — removing it forces a
  CS8602 on the watchdog closure's flow-state; not worth it).
- **Non-hermetic test** (observed, pre-existing): `RestartStampsPersistenceTests` writes/deletes
  a real file under `%LOCALAPPDATA%`, which flaked once in the Windows container with
  `UnauthorizedAccessException`; it should be sandboxed to a temp dir.
