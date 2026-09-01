# Architecture

PresenceLock is a **functional core / imperative shell** design split across two
languages. This document describes the current state of the design and the invariants
that must hold; the original design process, including the full decision record, is
preserved in [`0001-core-brain.md`](0001-core-brain.md) (its session/pause event
contract has since been superseded — see "Environment levels and the suppression edge
rule" below — but its decision-state, signal-health, and recovery contracts are
unchanged and remain authoritative) and in
[`0002-environment-levels.md`](0002-environment-levels.md).

## The core/shell boundary

- **`PresenceLock.Core` (F#)** — the entire decision surface. A pure function
  `step : PolicyConfig * State * StepContext * Event -> StepResult` maps the current
  state, the sampled environment, and an event to the next state and at most one
  action (`Lock` or `Restart`). No clocks, no I/O, no hidden mutation — every notion
  of "now" and "the world right now" is a caller-supplied parameter. The core targets
  plain `net10.0` with no Windows dependency, so its tests run anywhere, including
  inside a build container, without a camera or a real session to lock.
- **The C# shell** (`Program.cs`, `SettingsForm.cs`, `PolicyBridge.cs`) — a WinForms
  tray app that owns everything impure: the camera (`MediaCapture`), face detection,
  the Win32 session lock, process lifecycle, persistence, and the sampled facts about
  the environment (session-lock state, pause, lock inhibitors, input idle). It samples
  the camera, classifies each observation into an `Event`, hands it to the core
  through a single `Advance(Event)` chokepoint — which also assembles the current
  `StepInputs` — and executes whatever `Action` comes back.

The shell decides *nothing*; the core touches *nothing*. That boundary is what makes
the safety-critical logic — when to lock, when not to, when to recover — exhaustively
unit- and property-testable.

## Environment levels

The core's decision is a pure function of decision state, time, and **sampled
environment facts**. Facts gate actions; they never rewrite history. Every
environment fact is either owned by the shell or periodically re-sampled from the
OS — nothing is a fold that can silently drift. This replaced an earlier design
(0001-core-brain.md) where session-lock and pause were delivered as edge events
(`SessionLocked`/`SessionUnlocked`/`Paused`/`Resumed`) that the core folded into
belief flags. A folded belief has no way to re-synchronize with truth: one swallowed
edge diverges it permanently. An incident (2026-08-17) reached exactly this state —
pause → session lock → session unlock → resume left the core permanently believing
the session was locked while the user sat in an unlocked session, specified and
tested behavior notwithstanding — which is why the contract below exists.

Two words are used in a pinned, narrow sense throughout the code and docs:
**suppression** (paused or session-locked) freezes sampling at the top of the
pipeline — nothing is decided while it holds. **Inhibition** (see "Lock inhibitors"
below) lets the entire pipeline run and vetoes only the final `Lock` action. They are
near-synonyms in English; here they name opposite ends of the pipeline.

- **`StepInputs`, sampled fresh on every step.** `{ SessionLocked; Paused;
  LockInhibited; InputIdleMs }` is passed on every `step` call — never folded from an
  event — bundled with `Now`/`NowWall` into a `StepContext`. It is deliberately a
  plain reference record, not a `[<Struct>]`: three adjacent same-typed bools give a
  transposed positional construction no compile-time protection, and as a struct,
  `default(StepInputs)` reads as the *dangerous* value (unsuppressed, uninhibited,
  idle) and is reachable through zero-init paths (`Array.zeroCreate`, a dictionary
  miss, `Unchecked.defaultof`) that no call-site discipline can guard. As a reference
  type, every such path yields `null` and fails loud with an `NullReferenceException`
  on first field access instead of reading as plausible truth. The shell constructs
  `StepInputs` in exactly one helper (`Program.BuildStepInputs`), always via named
  arguments, used by both `Advance`'s input assembly and the startup `Policy.start`
  call; a `PresenceLock.Tests` case constructs it with each bool flipped
  independently and asserts the field mapping, so a future field reordering can never
  silently transpose a positional call.
- **`Reconcile` — the fifth event.** The `Event` DU no longer has session/pause
  cases; `Sample` carries only an `Observation`. `Reconcile` is payload-free —
  "nothing happened, except the levels may have changed" — and the shell fires it
  synchronously whenever any mirror changes (pause toggle, session switch, WTS
  reconciliation correction, inhibitor-aggregate transition). Without it, the sample
  timer is stopped while suppressed, so no `step` call would run during a suppression
  window and `LastInputs`, `CachedStatus`, and the tray text would go stale until the
  next natural `Sample` — a stale `LastInputs` could swallow a suppression edge
  outright (both sides of the comparison equal, reset never fires). `Reconcile`
  performs edge derivation and status recomputation only; it never emits `Lock` — a
  lock decision needs a current observation, which `Reconcile` carries none of, so an
  inhibitor clearing produces the lock on the next `Sample` instead (bounded by one
  sample interval).
- **One suppression edge rule, structurally enforced.** Define
  `suppressed inputs = inputs.SessionLocked || inputs.Paused`. `step` is a thin
  wrapper: it derives the edge (`LastInputs` suppressed, current inputs not) before
  any event dispatch, applies the baseline reset (disarm, re-baseline grace/away,
  reset signal-health) on that edge, treats `Sample` as a no-op while currently
  suppressed, and only then calls a private `dispatch` holding the per-event match.
  `dispatch` is *fully* suppression-blind — it never reads suppression state and
  never touches `LastInputs`/`CachedStatus`. A single tail, common to every call,
  writes the current inputs into `LastInputs` and recomputes `CachedStatus`. This
  makes `step` the sole writer of `LastInputs` and the sole caller of status
  computation, by construction rather than by a per-arm convention every match arm
  would otherwise have to remember (the same defect shape, one level down, that let
  the 2026-08-17 incident's truth table go wrong in the first place). It is also what
  makes the edge rule sound: an edge can be delayed by at most one call, never lost,
  because both sides of the comparison are re-supplied fresh on every call. On the
  reverse (unsuppressed→suppressed) edge nothing special happens — armed/away/signal
  health survive, matching pause and lock semantics. Failure and restart events
  (`InitFailed`, `CaptureFailed`, `BetterCameraAvailable`) are level-immune, exactly
  as they were pause-immune before: the camera's death is a fact regardless of
  suppression.
- **`LastInputs` lives inside opaque `State`.** It is not a shell-held
  (previous, current) parameter pair, so it has exactly one writer — `step` — and can
  never desync from what `step` last saw. `Policy.start` takes the initial
  `StepInputs` (the shell samples truth before the first step; restart pause
  inheritance becomes ordinary input passing) and initializes both `LastInputs` and
  the initial `CachedStatus` from them verbatim — a restart-while-paused process
  reports `Paused` from its very first render, never a one-frame `AcquiringCamera`
  flash, and a restart-while-locked process correctly evaluates its first real unlock
  as an edge rather than silently defaulting to "unsuppressed."
- **`Status` keeps its six cases**, with rows 1–2 (`Paused`, `SessionLocked`) now
  reading from `LastInputs` instead of folded flags; the priority order and the
  remaining four rows (`Recovering`, `AcquiringCamera`, `NoSignal`, `Watching`) are
  unchanged. There is deliberately no seventh `Inhibited` row — see "Lock inhibitors."

## Lock inhibitors

`LockInhibited` is a **fire-time gate**, symmetric with the input-idle gate: the away
clock keeps measuring truth regardless of inhibition; the gate only vetoes the `Lock`
action at the moment it would fire. It never disarms, re-baselines, or touches signal
health. Consequences: an inhibited, past-threshold absence saturates the away clock
without locking; when the inhibitor clears with the user still absent, `Lock` fires on
the next `Sample` (never latched — re-derived per the usual lock rule), bounded by one
sample interval, and only if sampling stays unsuppressed through that interval (an
intervening session lock or pause instead defers the lock to the next unsuppressed
sample and re-baselines the away clock at that edge — benign, since the intervening
suppression is itself either protection or explicit intent). A shell-side veto — core
still emits `Action.Lock`, shell declines to execute it — was rejected: it would
recreate the same "silently cannot lock" pattern this design otherwise eliminates,
where the core's own state and log claim a lock fired that never did. The gate belongs
where the decision lives.

Inhibition is **not a `Status` row**. It can co-occur with any row (media can play
while the camera is dead), so a priority row would force a contentless ranking against
e.g. `NoSignal` and would hide inhibition whenever any higher row applied — exactly the
opacity this design exists to eliminate. Instead it is an orthogonal projection,
`Policy.lockInhibited : State -> bool` (a cached echo of `LastInputs.LockInhibited`),
consumed by the verification harness and diagnostic logging only. The **render path**
does not round-trip through the core projection: the shell's `StatusText` sources both
halves of the inhibition annotation — the active bool and the provider names — directly
from the inhibitor registry's own cached snapshot, so one annotation has one owner. The
annotation (`PolicyBridge.AppendInhibitionAnnotation`, e.g. "Watching · lock inhibited
(media-playing)") is appended whenever the registry reports active, regardless of which
`Status` row is showing — an annotation visible only while the user is away would be
one nobody ever sees, and the recurring lesson of this project is that a silent
cannot-lock breeds "why didn't it lock" incidents.

The registry (`LockInhibitor` / `LockInhibitorRegistry` / `LockInhibitorAggregation` in
`PolicyBridge.cs`) is the extension point for future gates (presentation mode, quiet
hours, on-battery profiles): each provider is one named, cached `bool` behind a
`Func<bool> query`. `Refresh()` runs **only** from the watchdog tick and is fail-open by
contract — a throwing query (WinRT/COM reads can die against a zombie session) is
caught, logged rate-limited, and reported inactive; "assume uninhibited" is the safe
direction, since "assume active forever" would be the silent-cannot-lock trap in new
clothes, and an unguarded exception would take down the whole watchdog tick (WTS
reconciliation and the sampling watchdog with it). `Advance` reads the registry's cached
`.Active`/`.ActiveNames` at any cadence — cheap, no I/O. The aggregation/change
detection (`LockInhibitorAggregation.Compute`) is a pure function, independently unit
tested with plain tuples, no fakes: `Active` iff any provider is active, `ActiveNames`
the active subset, and a `Changed` flag the watchdog tick uses to log the transition
("lock inhibitors active: media-playing" / "cleared") and fire `Advance(Reconcile)`.

The first (and currently only) provider is media playback via
`GlobalSystemMediaTransportControlsSessionManager` (WinRT, user-mode, no elevation):
active iff any media session reports `PlaybackStatus == Playing`, covering
browser-hosted YouTube/Netflix as well as Spotify and native players. The manager is
acquired once, asynchronously, off the tick path; `Refresh()` performs only synchronous
property reads over the cached session objects, never a blocking wait on a WinRT async
call from the synchronous watchdog tick (a classic STA sync-over-async deadlock). A
file-only kill switch, `MediaInhibitorEnabled` (default `true`), exists because this
feature deliberately weakens the lock. Known, accepted limitations: Teams/Zoom calls do
not surface in SMTC (a future `presentation` provider would cover that class); muted or
background autoplay still reports `Playing`, a false positive with no volume signal to
correct it; the cached level can be up to one watchdog interval stale in either
direction, and a zombie SMTC session that lingers after playback ends would stick the
level active (the transition log is the designated detection mechanism for that
failure). **Unverified live**: the media inhibitor has full unit/harness coverage but
has not yet been exercised against a live media session pausing a real lock — that
observation is part of the deferred slice-6 live smoke, not yet performed.

## Invariants

- **Two clock domains, made unforgeable by the type system.** Timing uses monotonic
  milliseconds (`Environment.TickCount64`) for grace/idle/away windows, and wall-clock
  epoch milliseconds only for restart-cooldown gates that must survive a reboot. They
  are distinct CLR struct types (`MonotonicMs` / `WallClockMs`), so passing one where
  the other is expected is a compile error in both F# and C#, not a latent unit bug
  — the two values are otherwise adjacent, swappable `int64`s constructed back-to-back
  on every event.
- **Fail open on bad signal.** Dim light, a blocked lens, or dropped frames report
  `NoSignal` and *never* lock — a false lock-out is worse than a missed lock, and the
  separate input-idle gate already prevents locking an actively working user. If the
  monotonic clock ever goes backwards, every elapsed-time comparison fails safe
  (never lock, never restart) rather than throwing or wrapping.
- **Presence is debounced in time and space, never in sample count.** Face detectors
  flicker false positives on empty scenes (threshold-marginal luminance patterns,
  aggravated by auto-framing camera pipelines). A raw detection only counts as presence
  once an unbroken run of spatially coherent detections (`PresenceFilter`, pure F#,
  property-tested) has spanned a minimum *duration* of monotonic time — real faces
  produce spatially coherent boxes over time; phantoms wander. Coupling stability to a
  sample count instead of a duration was tried and found to reproduce the same class of
  bug the two clock domains above exist to prevent: its real-world meaning would drift
  with `SampleIntervalMs`, and a single dropped detection under fast sampling could cost
  disproportionately more re-stabilization time than the away clock — measured in real
  time — gives back, risking a false lock while the user never left. A Schmitt-trigger
  hysteresis (a looser IoU bar once a run has already reached stability, a stricter one
  to acquire it) keeps an established run sticky against ordinary IoU jitter without
  loosening acquisition. Absence needs no such filter — the away threshold already
  integrates it over seconds.
- **A frozen frame is treated as no frame.** `TryAcquireLatestFrame` can re-serve the
  same cached frame indefinitely (a known WinRT quirk) if the underlying pipe has
  stalled. `FrameFreshness` (pure F#, time-based for the identical reason as
  `PresenceFilter` above) tracks whether the frame source's own timestamp is still
  advancing; once it has been frozen past a threshold, the sample is classified exactly
  as if no frame had been acquired at all. This costs no new recovery mechanism — the
  existing fail-open `NoSignal` accounting already resets the away baseline and, if the
  staleness persists, drives the same re-evaluation restart that recovers any other dead
  signal.
- **Initialize the camera once per process; never re-initialize in-process.** Tearing
  down and re-initializing the capture pipeline wedges the Windows Camera Frame Server
  service machine-wide (`E_HANDLE`, persistent until an elevated service restart). This
  invariant is *structurally enforced*: the in-process re-init paths are gone, and any
  recovery that needs a fresh pipeline is an `Action.Restart` decided by the core and
  executed as a graceful process self-restart by the shell. Do not add a teardown/retry
  loop, however tempting — first-init in a fresh process is the only reliable
  re-initialization on affected hardware.
- **Resume always restarts sampling.** Every transition that leaves suppression
  (`Paused` or `SessionLocked`) restarts the sample timer unconditionally, whether or
  not a camera is currently attached. This closes an incident (2026-08-12) where a
  resume gated the restart on `reader is not null`: a camera that died — inside the
  restart cooldown, while the process was paused — left the reader null and the
  sample timer stopped with no path back. The dead-camera branch of `SampleAsync`
  exists precisely for this state: with `reader` null it still emits `NoFrame`, which
  drives the ordinary no-signal → re-evaluation → restart recovery — resuming
  sampling is what lets that branch run at all. This is now one rule instead of one
  per call site: `Advance` compares the `Status` immediately before and after every
  `step` call (`PolicyBridge.SuppressionTransitionStep`, pure and independently
  tested) and, on any suppressed→unsuppressed pair, resets `presenceFilterState` and
  `frameFreshnessState` to `.initial` (the 2026-07-28 burn-in fix — spatial coherence
  measured against a previous watching episode must never carry into a new one),
  restarts the sample timer, and runs `KickAcquisitionIfNeeded`; on the reverse pair
  it stops the sample timer. Generalizing this into `Advance` itself (rather than
  hand-copying it at `TogglePause`'s resume branch and `OnSessionSwitch`'s unlock
  branch, as before) is what makes the WTS-reconciliation correction path in "Shell
  environment mirrors" below get the same behavior automatically: a mirror correction
  that healed the session-lock fact but left the sample timer stopped or the coherence
  baseline stale would be the same silent-drift incident class through a codepath no
  one watches manually.
- **A sampling watchdog guards against sample-timer starvation more generally.** Belt-
  and-suspenders for the rest of the starvation class beyond the specific incident above
  — a wiring hole in some other resume/init path, or a `SampleAsync` pass hung inside
  `await detector.DetectFacesAsync` with its reentrancy guard stuck true. A timer,
  independent of `sampleTimer` and always running, checks on a fixed interval whether a
  sample pass has completed recently while `PolicyBridge.ExpectsSampling` says one
  should have (`Watching`/`NoSignal` only — paused/locked/pre-acquisition states are
  never starvation, by construction). The check and its two-strike escalation
  (`PolicyBridge.SamplingWatchdogStep`) are pure and unit-tested: the first starved
  check performs a cheap self-heal (restart the sample timer, idempotent if it is
  already running); only a second consecutive starved check — meaning the self-heal
  did not clear it — is treated as a capture failure and fed through the normal
  `Advance(Event.CaptureFailed)` chokepoint. That routes through the core's existing,
  cooldown-gated recovery restart (`RecoveryCooldownMs`), so a genuinely wedged
  pipeline converges to one restart per cooldown window rather than a storm, and the
  watchdog introduces no restart authority of its own.
- **Camera switching is a process handoff.** Each process initializes exactly one
  camera, exactly once. Losing the camera (undocking) surfaces as a capture failure
  and restarts onto the best remaining device; a *better* camera arriving (docking)
  is noticed by a device watcher, debounced, and triggers a cooldown-gated upgrade
  restart. Restart cooldowns are persisted (wall clock) so rate limits survive the
  process boundary and reboots. The camera deliberately stays live (LED on) across
  a session lock.
- **Sleep/hibernate correctness.** `TickCount64` includes time spent suspended and
  shares its tick base with `GetLastInputInfo`, so idle and away windows elapse
  consistently across a suspend without special-casing wake.

## Shell environment mirrors

The shell owns two `bool` mirrors — `sessionLocked`, `paused` — that feed
`StepInputs`. `paused` is a plain flip: `TogglePause` sets it and calls
`Advance(Reconcile)` synchronously, so the pause takes effect and renders with the
click. Durability is **restart-scoped only**: `RestartProcess()` persists the mirror's
current value to disk, and startup consumes and clears that file into the mirror
before the first `Policy.start`; an ordinary toggle never touches disk, and a tray
Exit or an unplanned reboot never inherits a stale pause. For a lock-enforcement app,
a pause silently surviving a reboot and leaving the machine unguarded indefinitely is
judged the worse failure — this is an intentional, retained asymmetry, not an
oversight. **Unverified live**: automated tests exercise the persist/consume
mechanism directly; that a tray Exit → relaunch cycle on the real machine does not
inherit a pause is part of the deferred slice-6 smoke.

`sessionLocked` is the harder case, because the OS notification it mirrors
(`SessionSwitch`) is exactly the kind of edge event this whole design moved away
from trusting blindly. It is updated by `SessionSwitch` as before, but is now also
**reconciled against ground truth on every watchdog tick** via
`WTSQuerySessionInformation` (`WTSSessionInfoEx` → `SessionFlags`), so a missed
`SessionSwitch` edge self-heals in bounded time instead of persisting forever. A
fold-free design — querying WTS fresh on every `Advance`, matching `InputIdleMs`'s
treatment — was rejected: unlike `GetLastInputInfo`, `WTSQuerySessionInformation` is
an RPC-backed call on the UI thread at sample cadence, can transiently fail, and (see
hysteresis below) can lag a real transition, so it would need the same staleness
defenses anyway while paying the RPC cost on every sample. Fold-plus-reconciliation
keeps the prompt path event-driven and only bounds divergence at the watchdog cadence.

The reconciliation decision is extracted as a pure, independently tested function,
`PolicyBridge.ReconciliationStep` (mirroring `SamplingWatchdogStep`'s precedent),
composed as:

1. **Skip window.** Within one watchdog interval of a `SessionSwitch`-driven update
   that actually *changed* the mirror, reconciliation is skipped entirely — the
   window restarts only on a mirror-value-changing update, never on an idempotent
   duplicate delivery (Windows demonstrably double-fires `SessionSwitch`), so
   unrelated session traffic can never push a stuck mirror's heal out indefinitely.
2. **Fail-open.** A failed WTS query (RPC error, invalid handle, or an
   uninterpretable `SessionFlags` value) leaves the mirror at its current value,
   holds the consecutive-disagreement counter (neither counts toward it nor resets
   it), and logs rate-limited. A failed query never *sets* `sessionLocked = true` —
   defaulting broken data to "locked" would silently suppress protection, the
   fail-closed dim-light mistake in new clothes — and holding rather than resetting
   the counter means an intermittent-failure pattern costs at most one extra tick per
   failure rather than starving the heal forever.
3. **Two-tick hysteresis.** A correction is applied only once the query has
   disagreed with the mirror for two *consecutive* ticks; agreement resets the streak
   to zero. `SessionFlags` can lag a real transition, so acting on the first
   disagreement would let a stale read "correct" a correct mirror, and each spurious
   correction would itself re-baseline the away clock — a new silently-wrong-forever
   trap through the very mechanism built to close the last one.

Worst-case heal time is therefore **≈3 watchdog intervals** (~15 s at the 5 s
default: one skip-window tick plus two hysteresis ticks), stated as a bound rather
than the looser "within one interval" the original incident's own spec-vs-mechanism
gap would have been an instance of — and this bound is conditional on **bounded
consecutive query failures**, each failure extending it by one tick. A reconciliation
correction runs the identical path a live `SessionSwitch` does — mirror update, then
`Advance(Reconcile)` — so it gets the suppression-transition rule (filter resets,
timer restart, `KickAcquisitionIfNeeded`) automatically rather than by a hand-copied
chore at the correction call site, and every correction logs a warning (a correction
is itself a signal worth seeing). **Unverified live**: the reconciliation state
machine has six dedicated `ReconciliationStep` unit-test cases (fail-open,
exactly-two-tick correction, a failure interleaved between two disagreeing ticks,
duplicate-`SessionSwitch` not restarting the skip window, skip-window suppression,
the compounding worst case) plus the harness's exploration of the suppression edge
itself, but a live lock/unlock cycle with the reconciliation log inspected — normal
use producing no spurious corrections, and a forced missed edge healing within the
pinned three-interval bound — has not yet been run; that is part of the deferred
slice-6 smoke. So is the fast-user-switch/RDP observation against
0001-core-brain.md's known-limitation note (`WTSSessionInfoEx` often marks
disconnected sessions locked, a second-session behavior that needs Corey at the
machine to exercise).

A **spike** confirmed the `SessionFlags` semantics live before any of this fed real
decisions (2026-08-31, Windows 11 26200): the documented Windows 8+ meaning holds —
`WTS_SESSIONSTATE_LOCK = 0`, `WTS_SESSIONSTATE_UNLOCK = 1` — with no Windows-7-style
inversion. One marshaling trap surfaced and is now pinned in code:
`WTSINFOEX`'s union holds `LARGE_INTEGER`s, so `Data` is 8-byte aligned —
`SessionFlags` sits at offset 16, not offset 4 as a naive `SessionState`-sized layout
would suggest, and a wrong offset reads a different, plausible-looking field instead
of failing visibly.

Every mirror mutation, registry access, and `Advance` call must execute on the UI
thread — the existing `ui.Post` discipline is a rule, not just a pattern, because the
single-writer soundness of the mirrors, the inhibitor registry snapshot, and `State`
itself all rest on it. `Advance` opens with `Debug.Assert(SynchronizationContext.Current
== ui, ...)` as the fail-loud backstop; any future async continuation that touches a
mirror, the registry, or calls `Advance` must resume on the UI `SynchronizationContext`
— never `ConfigureAwait(false)`, never a raw thread-pool callback, even when guarding
against an unrelated STA deadlock (the SMTC acquisition continuation is the case most
likely to tempt this).

Within one watchdog tick, WTS reconciliation runs first, then inhibitor
`Refresh()`/aggregation (each firing its own `Advance(Reconcile)` on a change), and
only then does the sampling watchdog read `Policy.status` — so its starvation verdict
always reflects that tick's corrected truth. Core correctness does not depend on this
order (every `Advance` re-samples fresh inputs regardless), but the watchdog's
shell-side verdict is order-sensitive for one tick, hence the pinned composition
(`Program.WatchdogTickStep`).

## Verification harness

`PresenceLock.Core.Tests` carries a model-based harness (`HarnessModel.fs`,
`HarnessTests.fs`) purpose-built for the class of bug the 2026-08-17 incident was: an
absorbing "silently cannot lock" state reachable through some interleaving a
hand-written scenario table didn't think to try. It is a **test-side environment
model** — ground truth (`OsLocked`, `Paused`, `CameraAlive`, `MediaPlaying`,
`FacePresent`, `InputIdle`) plus a legal-action alphabet (`OsLock`/`OsUnlock`
alternate; a pause toggle requires an unlocked session, matching the tray being
unreachable through a lock screen; `Tick` advances time from a boundary-value delta
set and fires `Sample` exactly when the modeled shell would) — built to the same
fidelity discipline the real shell follows: every level-changing action calls `step`
with `Reconcile` synchronously at the transition, so the model can neither hide nor
invent staleness the real shell doesn't have. The explorer holds the recovery axes
fixed (already succeeded once, zero failure streak, all restart stamps unset, every
cooldown placed beyond the modeled horizon) — recovery is already covered by
0001-core-brain.md's own properties, so "every reachable state" here means reachable
under this alphabet with those axes fixed, a precise scope rather than an overclaim.

Four checks run at every node of an exhaustive BFS over the reachable
(environment × abstracted-core-state) graph, clock values quotiented into
zero/mid/past-threshold buckets against each field's single governing threshold and
deduplicated so the graph stays finite:

- **Property 13 — status/level agreement**: the full six-row `Status` priority table
  holds against the current levels and state, and `Policy.lockInhibited` agrees with
  `StepInputs.LockInhibited` — real regression coverage for the `LastInputs`
  invariant, since a broken invariant is exactly what would make this projection
  drift.
- **Property 14 — protection liveness**: from *every* reachable state, forcing the
  environment to (unlocked, unpaused, uninhibited, camera alive, face seen to arm,
  then absent and idle past every threshold) produces `Action.Lock` within bounded
  ticks — the app's entire purpose, stated as a property so any future absorbing
  cannot-lock trap becomes a counterexample instead of an incident. The explorer also
  asserts the edge-reset postcondition (disarmed, both baselines at now,
  `BadSignalSince` clear) directly at every suppressed→unsuppressed transition it
  takes, so a broken reset is caught at the edge itself rather than only inferred
  through liveness.
- **Inhibitor semantics**: absent, idle, past-threshold, and inhibited never locks;
  the identical state with the inhibitor cleared locks on the next sample, never on
  the `Reconcile` that announces the clearing.
- A standalone regression **replays** the original incident sequence
  (pause → lock → unlock → resume) with a concrete time schedule and asserts the
  resulting state is a member of the explorer's visited set and locks under the
  property-14 drive — the incident-sequence claim pinned to a concrete, cheap check
  rather than relying on full path-provenance tracking inside the BFS, which would
  fight the deduplication that keeps the graph finite.

Two mutations are committed permanently as negative fixtures (never a
production edit-and-revert) to prove the checks above can actually fail: the explorer
is generic over an auxiliary per-implementation state carried alongside the real
(opaque) `State`, because the folded-flag mutant's defining state — the very
`IsPaused`/`IsSessionLocked` booleans this design deleted — no longer exists to
parameterize over. A **folded-flag** variant reconstructs the pre-RFC architecture by
feeding the real `Policy.step`/`Policy.start` a folded view derived from consecutive
`StepInputs`, reproducing 0001-core-brain.md's truth table (including its exact
"unlock while paused is a no-op" defect) through input translation in front of an
otherwise-untouched core — the explorer catches it by reproducing the incident shape
in property 14. A **stale-`LastInputs`** variant freezes what it feeds `step` while
the true environment is suppressed, reproducing "the mirror healed but the edge never
re-fired" as a mechanism a levels design could introduce fresh — the explorer catches
it via property 13's agreement clause.

The static cadences both the shell and the model depend on — the watchdog interval
(also the inhibitor poll cadence, since `Refresh()` rides the watchdog tick) and the
default sample interval — live in one place, `PresenceLock.Core/Cadence.fs`,
deliberately separate from `PolicyConfig`: `step`/`start` take no dependency on it,
and its only purpose is giving `Program.cs` and the model one shared source of truth
so drift between the shell's real cadence and the model's assumed cadence is
impossible by construction for everything static. `SampleIntervalMs`'s *runtime*
tunability (a user can retune it in Settings) is the one accepted, irreducible gap:
the model explores at the shared default, and a retuned interval is outside the
modeled cadence.
