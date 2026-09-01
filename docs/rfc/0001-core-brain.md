# RFC: PresenceLock.Core — full-brain policy extraction to F#

Status: Implemented (all code slices shipped through 1.0.8.4; stage-4 code review pending, to run
over this scope together with 0002-environment-levels.md once that RFC's slices land)

## Motivation

Every shipped defect in PresenceLock's first day except the FrameServer wedge itself was a
*decision* bug living in I/O-tangled code: the fail-closed dim-light lock, the restart-marker
dead end that blocked recovery, a correct lock misdiagnosed as false because expected behavior
existed only as code. The decision surface is currently interleaved with camera I/O in
`WatcherContext` (`Program.cs`) and is untestable without locking the developer's real session.

Additionally, one latent correctness bug remains: the dark-feed camera re-evaluation and the
capture-failure path still perform in-process teardown→re-init, which violates the validated
invariant that in-process re-initialization wedges the Camera Frame Server service (E_HANDLE,
machine-wide, until elevated service restart). This RFC folds that fix in: all re-init becomes
process restart, decided by the core, executed by the shell.

**Scope note (round 1 review):** "all decision logic" in this RFC means *sampling, arming,
lock, signal-health, and recovery* decisions. The startup retry-timer's scheduling (`Program.cs`
`retryTimer`) remains a shell-owned timer — see "Shell changes" — but its gating conditions
must read from the core's `Policy.status`/`Policy.snapshot`, not from shell-local booleans, so
there is exactly one source of truth for "are we paused / locked."

## Design

### New project: `PresenceLock.Core` (F#)

- TFM `net10.0` — **no Windows dependency**. Tests for it run inside the build container.
- Referenced by `PresenceLock.csproj`. Mixed-language solution (separate project; required).
- Pure: no clocks, no I/O, no mutation visible to callers. Time is always a parameter.
  `PresenceLock.Core` is an **internal-only library** — no external compatibility contract,
  no independent versioning; it is consumed solely by `PresenceLock.csproj` in this solution.
  If a second consumer or a published package ever appears, this stance is revisited then.

### Time semantics (new section — round 1 review)

Two clocks cross the `Policy` boundary, and they are **never compared to each other**:

- `now: MonotonicMs` — monotonic milliseconds, shell-supplied via `Environment.TickCount64`.
  Drives grace/away/idle/streak timing within `step`. `TickCount64` is **boot-relative, not
  process-relative**: it is continuous across the plain process restarts this design performs
  routinely (recovery, camera re-evaluation), so `now` before and after a `Action.Restart` are
  directly comparable. It resets near zero only on an actual OS reboot.
- `nowWall: WallClockMs` / `RestartStamps` — wall-clock milliseconds since the Unix epoch
  (`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`), used **exclusively** for the restart
  cooldown gates (`RecoveryCooldownMs` / `ReevaluateCooldownMs`), because those gates alone must
  remain meaningful across a reboot — a genuinely wedged/broken camera doesn't get better after
  a reboot, and the whole point of the cooldown is to survive exactly that. The stamps (one per
  `RestartReason`) are persisted by the shell (JSON, wall-clock epoch ms) and passed into
  `Policy.start`.

The two domains are **distinct CLR struct types** (`MonotonicMs`/`WallClockMs` — see Types):
transposing them at a call site is a compile error in both languages rather than a latent unit
bug. The hazard is concrete — `Advance` constructs both values back to back on every event,
and the pair would otherwise be two adjacent, silently swappable `int64`s.

Sleep/hibernate: the core is deliberately **gap-oblivious**. A large jump in `now` between two
`Sample` calls (e.g. an 8-hour sleep) is treated identically to the same delta spread over many
small samples — no special-cased re-grace on wake. Consequences, made explicit so they're a
decision and not an accident: a `NoFace` sample arriving right after a long sleep, with idle and
away thresholds already satisfied by the elapsed wall/tick time, locks on that very sample; a
`NoFrame`/`DarkFrame` sample in the same spot does not, because those observations reset the
away-baseline (see "Notes" below) exactly as `FaceSeen` does. `step` never assumes `now` is
non-decreasing between calls beyond: if it ever goes backward, every elapsed-time comparison
must fail closed (never lock, never restart) rather than throw or wrap.

The "large jump in `now`" premise is documented behavior, not assumption: `GetTickCount64`
(which `Environment.TickCount64` wraps) **includes time the system spends in sleep or
hibernation**, and `GetLastInputInfo`'s `dwTime` shares the same tick base, so away and idle
elapse consistently across a suspend. Slice 9's sleep step still verifies this on the actual
hardware (Modern Standby): log `TickCount64` immediately before sleep and after wake and
confirm the delta tracks wall-clock elapsed time.

### Types (the contract with the shell)

```fsharp
/// Clock-domain wrappers — real CLR struct types on BOTH sides of the boundary, so a
/// transposed monotonic/wall-clock argument is a compile error in F# and C# alike
/// ([<Measure>] units would erase to bare int64 exactly at the C# boundary, where the
/// transposition risk actually lives: Advance constructs both values back to back).
/// C# constructs them via the generated factories: MonotonicMs.NewMonotonicMs(…) /
/// WallClockMs.NewWallClockMs(…).
[<Struct>] type MonotonicMs = MonotonicMs of int64
[<Struct>] type WallClockMs = WallClockMs of int64

type PolicyConfig =
    { AwayThresholdMs: int64
      InputIdleRequiredMs: int64
      GraceMs: int64
      NoSignalReportAfterMs: int64       // continuous bad signal before surfacing no-signal status
      ReevaluateAfterMs: int64           // continuous bad signal before requesting camera re-evaluation
      ReevaluateCooldownMs: int64        // minimum gap between CameraReevaluation restarts (wall-clock)
      RecoveryFailureThreshold: int      // consecutive pre-success init failures before requesting recovery
      RecoveryCooldownMs: int64 }        // minimum gap between CameraWedged restarts (wall-clock)

[<RequireQualifiedAccess>]
type Observation = FaceSeen | NoFace | NoFrame | DarkFrame

[<RequireQualifiedAccess>]
type Event =
    | Sample of Observation * inputIdleMs: int64
    | InitSucceeded
    /// Startup/retry failure, before any InitSucceeded this process. Streak-gated by
    /// RecoveryFailureThreshold — see "Notes: recovery boundary" below.
    | InitFailed of handleInvalid: bool
    /// A previously-live capture died mid-session (shell's MediaCapture.Failed). Carries no
    /// payload: post-success, the restart decision no longer depends on the failure's
    /// classification — see "Notes: recovery boundary."
    | CaptureFailed
    // SUPERSEDED by 0002-environment-levels.md (StepInputs/Reconcile): these four
    // session/pause cases are removed from the Event DU. Session-lock and pause are no
    // longer edge events folded into State — they are sampled levels (StepInputs.SessionLocked
    // / .Paused) read fresh on every step, with a single Reconcile event replacing all four
    // for "the levels may have changed, re-evaluate."
    | SessionLocked
    | SessionUnlocked
    | Paused
    | Resumed

[<RequireQualifiedAccess>]
type Status =                             // semantic; shell renders strings (pulled, never pushed)
    | Watching | NoSignal | SessionLocked | Paused
    | AcquiringCamera | Recovering

[<RequireQualifiedAccess>]
type RestartReason = CameraWedged | CameraReevaluation

/// Exactly one Action per step — cardinality is enforced by the type, not by convention.
[<RequireQualifiedAccess>]
type Action =
    | NoAction
    | Lock
    | Restart of reason: RestartReason   // process restart: recovery OR camera re-evaluation

/// Persisted wall-clock (Unix-epoch ms) restart stamps, one per RestartReason, carried across
/// process restarts by the shell. Nullable (not option) deliberately: this type is constructed
/// at the C# boundary. Never compared against the monotonic clock. (A Map<RestartReason,int64>
/// shape indexed by the DU was considered and deferred: with two reasons and the third known
/// candidate explicitly out of scope, hand-named fields are the simpler C#-boundary optimum;
/// revisit only if RestartReason grows.)
type RestartStamps =
    { WedgeAt: System.Nullable<int64>
      ReevalAt: System.Nullable<int64> }

type State                                 // opaque; private record internally

/// Test/diagnostic projection — never used by the shell for control flow. Exists because the
/// test plan (baseline-is-unarmed-and-in-grace; "no Lock unless armed") requires observing
/// facts an opaque State can't otherwise expose, and because the shell logs it alongside every
/// Lock/Restart action so today's diagnostic-rich log lines survive the extraction.
type Snapshot =
    { Armed: bool
      InGrace: bool
      AwayForMs: int64                     // now minus the last away-baseline reset
      NoSignalForMs: int64                 // 0 while signal is healthy
      InitFailStreak: int
      // Nullable, not option — same C#-boundary rationale as RestartStamps: the shell's
      // logging code consumes Snapshot directly and must never touch FSharpOption.
      LastWedgeRestartAt: System.Nullable<int64>
      LastReevalRestartAt: System.Nullable<int64> }

/// Named struct rather than a positional State * Action tuple: no per-sample tuple allocation
/// on the hot path, and C# reads .State/.Action instead of .Item1/.Item2.
[<Struct>] type StepResult = { State: State; Action: Action }

module Policy =
    // All functions take TUPLED parameters, not curried — each compiles to one ordinary
    // multi-parameter static method regardless of how C# references it (including capturing
    // one as a delegate, which must never surface an FSharpFunc chain). Uniform argument
    // order across the module: config, then state, then time, then event — each parameter
    // present only where causally needed.
    /// Called exactly once per process, in WatcherContext's constructor, before the first
    /// camera-acquisition attempt. All later re-baselining goes through events into the same
    /// State — never a second start call. Takes no PolicyConfig: start only stamps the
    /// initial baseline and imports the persisted stamps; every threshold is compared live at
    /// step time (see Notes), so a config parameter here would be dead — and ambiguous
    /// against step's.
    val start    : now: MonotonicMs * stamps: RestartStamps -> State
    val step     : PolicyConfig * State * now: MonotonicMs * nowWall: WallClockMs * Event -> StepResult
    /// Computed during step and cached in State — reflects the world as of the most recent
    /// event, at most one sample interval stale under normal sampling; the shell reads it
    /// only inside Advance, immediately after a step.
    val status   : State -> Status
    /// Takes PolicyConfig because InGrace/AwayForMs are derived live against thresholds at
    /// projection time, consistent with the live-config rule — grace is never latched.
    val snapshot : PolicyConfig * State * now: MonotonicMs -> Snapshot
```

Notes:
- `step` returns a `StepResult` — the new `State` plus exactly one `Action` (usually
  `Action.NoAction`) — cardinality by type, not by documented convention. Status is **pulled,
  never pushed**: the shell calls `Policy.status`
  after every `step` (inside its single `Advance` chokepoint — see "Shell changes") and
  re-renders only when the rendered string differs. This deletes an entire contract surface
  (emission/dedup/ordering of a `SetStatus` effect) by making it unrepresentable instead of
  specifying and property-testing it.
- **Session-event idempotency:** re-delivery of an already-current session fact
  (`Event.SessionLocked` while already session-locked, `Event.Resumed` while running, and so
  on) is a no-op — Windows is known to double-fire `SessionSwitch`. Slice 5 tests this.
- **Live config:** `PolicyConfig` is passed on every `step` and read live. Baselines stored in
  `State` are timestamps; thresholds are compared at step time — so a threshold changed in
  Settings mid-grace or mid-away-countdown applies on the very next sample, without discarding
  accumulated state. (Scenario test: config-changed-mid-grace, slice 5.)
- `Policy.step` is **pure and total**: for any valid `Config`/`State`/`Event` it terminates and
  returns without throwing. The shell's outer catch-all around `SampleAsync` is retained
  regardless — as defense against unrelated I/O failures (frame classification, Win32 calls),
  not against `step` itself.
- `Action.Lock` is a **stateless, re-derived request**: `step` does not latch any internal
  "locked" state on emitting it. The core only learns a lock actually took hold via a
  subsequent `Event.SessionLocked`, sourced by the shell from the real OS notification — never
  inferred from having emitted the effect. If the shell's Win32 `LockWorkStation()` call fails,
  it takes no core-side action at all; the next qualifying `Sample` independently re-evaluates
  and re-emits `Action.Lock` if conditions still hold (no separate `LockFailed` event needed).
- The armed rule is core-owned: no `Action.Lock` unless a `FaceSeen` occurred since the
  last baseline event — `start`, `InitSucceeded`, `SessionUnlocked` (**unless** currently
  paused — see below), or `Resumed`. `InitSucceeded` is a baseline event specifically so a slow
  camera acquisition never consumes grace measured from `start`'s `now` (mirrors `Program.cs`'s
  "baseline the grace window from successful acquisition, not from when this attempt began").
- Fail-open rule, stated explicitly: `NoFrame` and `DarkFrame` observations reset the
  away-baseline **identically to `FaceSeen`** (both represent "not a valid continuous away
  observation") but, unlike `FaceSeen`, do **not** set `armed`. A brief camera glitch (dropped
  frame, blocked lens for one sample) must reset the away clock, not merely pause it — otherwise
  intermittent frame drops could accumulate toward the away threshold instead of failing open.
- Every baseline event (`start`, `InitSucceeded`, `SessionUnlocked` unless paused, `Resumed`)
  also **resets the signal-health clock** (`NoSignalForMs` = 0) — parity with
  `ResumeSampling()`'s `noSignalStreak = 0`. Without it, a bad-signal run already in progress
  before a lock or pause could satisfy `ReevaluateAfterMs` on the first post-resume sample and
  fire an immediate re-evaluation restart.
- Input-idle gating is core-owned (idle supplied as data).
- **Session/pause precedence:** `SessionUnlocked` while an internal `paused` flag is set is a
  no-op — it does not re-baseline, arm, or change `Status` away from `Paused` — matching
  `Program.cs`'s `if (paused) return;` guard in `OnSessionSwitch`. Pause takes precedence over
  an unlock-driven resume; only an explicit `Resumed` event clears it. Truth table:

  > **SUPERSEDED by 0002-environment-levels.md (StepInputs/Reconcile).** This entire truth
  > table — including the "`SessionUnlocked` while paused is a no-op" row that follows —
  > is the root cause the later RFC's Motivation names: a folded belief (`IsPaused`/
  > `IsSessionLocked`) has no way to re-synchronize with truth once an edge is swallowed,
  > and this table's precedence rule was the specified, tested behavior that produced the
  > 2026-08-17 incident. It no longer describes current behavior: session-lock and pause
  > are sampled levels, `suppressed inputs = inputs.SessionLocked || inputs.Paused` is a
  > plain order-independent OR, and there is no precedence sub-case to specify.

  | Event            | while Paused        | while not Paused                    |
  |-------------------|---------------------|--------------------------------------|
  | `SessionLocked`   | stays Paused         | → `Status.SessionLocked`, sampling stops       |
  | `SessionUnlocked` | no-op (stays Paused) | re-baseline, disarm, re-grace, reset signal-health, resume |
  | `Paused`          | no-op                | → `Paused`, sampling stops            |
  | `Resumed`         | re-baseline, disarm, re-grace, reset signal-health, resume | no-op (already resumed) |

- **`Status` is total and priority-ordered** — the complete `State → Status` function, given
  the same rigor as the pause table (computed during `step`, cached — see the module
  signature):

  | # | Status            | condition                                                  |
  |---|-------------------|------------------------------------------------------------|
  | 1 | `Paused`          | paused flag set                                            |
  | 2 | `SessionLocked`   | OS session locked                                          |
  | 3 | `Recovering`      | no `InitSucceeded` yet this process; ≥1 `InitFailed` seen  |
  | 4 | `AcquiringCamera` | no `InitSucceeded` yet this process; no failure yet        |
  | 5 | `NoSignal`        | acquired; continuous bad signal ≥ `NoSignalReportAfterMs`  |
  | 6 | `Watching`        | otherwise                                                  |

  `Recovering`/`AcquiringCamera` are **pre-first-success only**: a post-success dead camera
  surfaces as `NoSignal` via the cooldown-suppressed-`CaptureFailed` backstop (see "recovery
  boundary" below), never as `Recovering`. That gives the shell's retry timer a clean,
  complete gate: it may tick only while `Policy.status` is `AcquiringCamera` or `Recovering`
  — i.e. first-init retries only, which are empirically safe — making it structurally
  incapable of a post-success in-process re-init. `Paused`/`SessionLocked` outrank both, so
  the timer also stays quiet while paused or locked, matching today's
  `!paused && !sessionLocked` guard.
- **Sample events outside the watching window (defense in depth):** `step` treats `Sample` as a
  no-op — unchanged `State`, no effects — whenever the current `Status` is `Status.SessionLocked` or
  `Paused`. This exists because the shell cannot fully guarantee ordering: `SampleAsync` awaits
  an async face-detection call, and a `SessionSwitch`/pause transition marshaled onto the same
  UI thread can complete during that await, so a `Sample` computed from a frame captured before
  the transition may still reach `step` after it. The shell should still discard a `Sample` it
  knows is stale (re-check status after any `await`, before calling `step`), but the core does
  not rely on shell discipline alone for this — see FsCheck property 10.
- **`SessionUnlocked`/`Resumed` while the camera has never been (successfully) acquired:** this
  leaves `Policy.status` at `Status.AcquiringCamera` (or `Status.Recovering`, if the most recent
  camera event was a failure) and returns `Action.NoAction`. The retry timer alone does **not**
  cover resumption here — today it self-stops on every tick and is re-armed only from the
  failure paths, so during a lock/pause it sits idle with nothing pending; recovery after
  unlock/resume is actually driven by *direct* `StartWatchingAsync()` calls in
  `OnSessionSwitch`/`TogglePause`. The extraction preserves that kick explicitly as
  `KickAcquisitionIfNeeded()` — see "Shell changes" — invoked after these two events. Mirrors
  `Program.cs`'s `OnSessionSwitch` unlock branch falling through to `RestartWatchingAsync` when
  `reader is null`; that call is only safe today because a null capture/reader makes teardown a
  no-op — it must **not** be read as license to route this case through `Action.Restart`.
- **Recovery boundary — `InitFailed` vs `CaptureFailed`:** these are deliberately separate
  events, not one `handleInvalid` flag with shell-side pre-triage, because they have different
  restart policies:
  - `InitFailed` (never yet succeeded this process): streak-gated. Only after
    `RecoveryFailureThreshold` **consecutive** handle-invalid `InitFailed` events, and
    `RecoveryCooldownMs` elapsed since `RestartStamps.WedgeAt` (wall-clock), does `step` emit
    `Action.Restart CameraWedged`. Below threshold, retrying in-process is empirically safe —
    this is the "startup retry" path.
  - `CaptureFailed` (a previously-live capture just died): **not** streak-gated and **not**
    classification-gated. Once `InitSucceeded` has occurred this process, *any* `CaptureFailed`
    requests `Action.Restart CameraWedged` on its very first occurrence, subject only to
    `RecoveryCooldownMs` — never to `RecoveryFailureThreshold`. Post-success, every recovery
    is an in-process re-init — exactly the wedge trigger this RFC exists to eliminate — so the
    failure's classification cannot change the decision; and gating the restart on
    handle-invalid would leave ordinary transient capture failures with **no recovery path at
    all** once the in-process retry loop is deleted (strictly worse than today's infinite
    retry). The event therefore carries no payload; the shell still classifies the failure for
    its own log line (see the classifier note below).
  - **Cooldown-suppressed `CaptureFailed` — the backstop:** when the wedge cooldown has not
    elapsed, `step` returns `Action.NoAction`. The shell tears down the dead capture (reader
    null) but keeps the sample timer running; `SampleAsync` with a null reader emits
    `Sample(Observation.NoFrame, …)`. The ordinary signal-health machinery then surfaces
    `Status.NoSignal` and, when `ReevaluateCooldownMs` permits, requests
    `Action.Restart CameraReevaluation` — bounded eventual recovery with **no new mechanism**.
    Worst case under repeated failures: one restart per reason per its cooldown window (two
    per ~10 min at defaults). Scenario test in slice 6.
  - **Failure events are not gated by `Paused`/`SessionLocked`:** only `Sample` no-ops there.
    A `CaptureFailed` while paused still requests its restart — the camera is deliberately
    kept alive while paused, so its death is a real fact requiring recovery, and R1-30's
    persisted pause flag exists precisely so such a restart preserves the pause. Deliberate
    asymmetry, stated: sample-driven decisions are pause-immune; failure-driven restarts are
    not. Scenario test ("`CaptureFailed` while `Paused` still requests `Action.Restart`") in
    slice 6.
  - **Camera unplug/replug, analyzed:** unplugging the live camera → `CaptureFailed` → at most
    one `CameraWedged` restart (cooldown permitting) → the fresh process finds no camera →
    `InitFailed handleInvalid: false` retry loop at 5 s under `Status.Recovering` — no further
    restarts, because the pre-success streak gate requires handle-invalid failures. Replug →
    the next first-init retry succeeds. Bounded: at most one process restart per unplug event.
    Slice 9 smokes this.
  - The shell's handle-invalid classifier: the real logic is
    `static bool IsHandleInvalid(int hresult, string message)` — HResult `0x80070006` OR a
    message-substring match, because the WinRT projection doesn't reliably preserve the
    HResult — with an `IsHandleInvalid(Exception ex)` convenience overload for the init path.
    The two-parameter form exists because `OnCaptureFailed` receives
    `MediaCaptureFailedEventArgs` (only `Code` + `Message`, no `Exception`), which a
    single-`Exception` signature cannot serve. Post-success it feeds logging only (the
    `CaptureFailed` event is flagless); pre-success it feeds `InitFailed handleInvalid`. It
    remains shell-side — sensing, not policy — but is exactly the "expected behavior existed
    only as code" defect class the Motivation cites, so it must be a named, unit-tested
    function with dedicated xunit tests for the HResult path, the message-substring fallback,
    and the args-based form.
- Camera *selection* (external-first ranking) and frame *classification* (luma → `DarkFrame`)
  stay in the shell: they are sensing, not policy. `DarkFrameMeanThreshold`, `SampleIntervalMs`,
  and `CameraNameContains` stay in the shell's existing flat `Config` (the on-disk
  `presencelock.json` schema, unchanged) and never cross into `PolicyConfig` — Core never
  receives values it has no causal use for. `Status.NoSignal` does not distinguish
  dark vs. no-frame: the shell already computed that distinction itself one line before
  constructing the `Observation`, so it can log/render the finer-grained tray text
  ("dark/blocked" vs. "no frames") from its own local knowledge without Core echoing it back.

### Config: file schema, mapping, validation (revised — round 1 review)

The on-disk `presencelock.json` schema is the existing flat C# `Config` class, **unchanged and
backward-compatible** — no nested rewrite, no migration, existing files keep working and keep
their tuned values. Five new optional fields gain built-in defaults when missing (exactly how
the existing loader already treats absent fields). The shell builds `PolicyConfig` from it via
one named, unit-tested mapping function. Pinned mapping (values = today's shipped behavior):

| shell field / constant (old)             | `PolicyConfig` field (new) | default |
|------------------------------------------|----------------------------|---------|
| `AwayThresholdSeconds = 5.0` (s)         | `AwayThresholdMs`          | 5000    |
| `InputIdleSeconds = 10.0` (s)            | `InputIdleRequiredMs`      | 10000   |
| `GraceSeconds = 10.0` (s)                | `GraceMs`                  | 10000   |
| `NoFrameReportThreshold = 20` (samples)  | `NoSignalReportAfterMs`    | 10000   |
| `CameraReinitSamples = 40` (samples)     | `ReevaluateAfterMs`        | 20000   |
| new                                      | `ReevaluateCooldownMs`     | 600000  |
| `initFailStreak >= 3` (literal)          | `RecoveryFailureThreshold` | 3       |
| 10-minute file cooldown (literal)        | `RecoveryCooldownMs`       | 600000  |

`NoSignalReportAfterMs`/`ReevaluateAfterMs` are **durations**, not sample counts — the old
counts were silently coupled to `SampleIntervalMs` (user-tunable in Settings), so changing the
sample rate changed their real-world meaning by side effect. 20/40 samples at the default
500 ms interval = 10 s/20 s, preserved above. `SampleIntervalMs`, `CameraNameContains`, and
`DarkFrameMeanThreshold` remain shell-only with no `PolicyConfig` counterpart.

Validation, in the shell at load time, before ever calling `Policy.start`:
- All `*Ms` fields must be `> 0`; `RecoveryFailureThreshold >= 1`.
- `ReevaluateAfterMs >= NoSignalReportAfterMs`, so the no-signal tray status is always visible
  before a re-evaluation restart fires.
- Out-of-range or unparseable values among the `PolicyConfig`-mapped fields: log and fall back
  to built-in defaults for **all eight `PolicyConfig` fields as a unit** — never a
  partially-defaulted mix (a zero-filled cooldown would make the next camera hiccup an instant
  restart loop). The sensing fields (`CameraNameContains`, `DarkFrameMeanThreshold`,
  `SampleIntervalMs`) load independently and are **not** reset by a policy-field failure — a
  fat-fingered duration must not silently discard the user's tuned camera selection and hand
  the watcher back to the lid camera.
- The Settings dialog's commit path (`OpenSettings`) routes the committed `Config` through the
  **same** shared validate-and-map function as file load — three dialog-editable fields map
  into `PolicyConfig`, and with live config an invalid committed value would otherwise reach
  `step` unvalidated on the very next sample.
- **Restart stamps:** `last-restart.txt` is superseded by a small JSON stamps file (wall-clock
  epoch ms, one stamp per `RestartReason`, plus the persisted paused flag); the orphaned
  `last-restart.txt` is deleted the first time the stamps file is written. Any read/parse
  error yields empty stamps — parse failure must never block recovery. The shell writes only
  the stamp matching the `RestartReason` it is executing, so routine re-evaluation restarts
  can never poison the wedge-recovery cooldown.
- **Paused-flag lifecycle (pinned):** the flag is written **only** in the `RestartProcess()`
  path, immediately before spawn, and is **consumed-and-cleared** by the fresh process at
  startup (read → if set, feed `Event.Paused` right after `Policy.start` → immediately rewrite
  with the flag cleared). Consequences, stated: pause survives every self-restart (the R1-30
  intent); a tray Exit or any normal launch never inherits a stale pause; a reboot inherits it
  only in the narrow crash window between write and consume (accepted — for a lock-enforcement
  app, the alternative of pause silently surviving a Windows-Update reboot and leaving the
  machine unguarded indefinitely is the worse failure).
- The stamp read/write shim (including consume-and-clear), the config mapping function, and
  the `IsHandleInvalid` classifier all get dedicated shell-side xunit tests (see Notes).

### Shell changes (`Program.cs`)

- `WatcherContext` gains a single `Advance(Event)` method — the **only** place `state` is
  reassigned. It computes `now`/`nowWall` (constructing `MonotonicMs`/`WallClockMs` back to
  back), calls `Policy.step`, executes the returned `Action`, logs `Policy.snapshot` alongside
  every `Lock`/`Restart` action (preserving today's diagnostic-rich log lines), and re-renders
  `Policy.status`. Every call site (`SampleAsync`, init success/failure, `OnSessionSwitch`,
  `TogglePause`, `OnCaptureFailed`) goes through it, so action handling and status refresh can
  never drift per call site. The `Action.Lock` branch stops `sampleTimer` synchronously
  **before** calling `LockWorkStation()`, restarting it only if the Win32 call fails —
  preserving today's guard against a second timer tick re-deriving `Lock` in the window before
  the OS `SessionLock` notification arrives. (Core statelessness — property 9 — is about
  re-derivation after a *failed* lock, not license to fire duplicate Win32 calls.)
- `KickAcquisitionIfNeeded()` — `if (reader is null && Policy.status(state) is
  Status.AcquiringCamera or Status.Recovering) _ = StartWatchingAsync();` — called after
  `Advance(Event.SessionUnlocked)` and after `Advance(Event.Resumed)`, and the
  `retryTimer.Tick` guard is rewritten to this same status gate (replacing
  `!paused && !sessionLocked && reader is null`). This names the mechanism today's code
  implements as direct `StartWatchingAsync()` calls at those two call sites — the
  self-stopping retry timer would otherwise sit idle after a lock/pause and the app would
  never reacquire. All three call sites are on the 8b cutover checklist.
- Every C# `switch` over `Action`, `Status`, or any other Core DU ends in
  `default: throw new UnreachableException(...)` — the C# compiler does not check F# DU
  exhaustiveness across the assembly boundary, so fail-loud is the only safety net when a case
  is added later.
- **Pause survives self-restarts:** the shell persists the paused flag (in the stamps file)
  before any `RestartProcess()`; on startup, if set, it feeds `Event.Paused` immediately after
  `Policy.start` — fixing the pre-existing silent-unpause across the camera-filter-change
  restart (and any future restart while paused).
- `SampleAsync` classifies the frame → `Observation` and calls `Advance`. All decision
  conditionals (`armed`, grace, streaks, cooldown file logic) are deleted.
- `OpenSettings`'s restart-on-camera-filter-change stays a shell-only mechanical decision
  (trivial string comparison, not policy): it is not routed through `Policy`, and
  `RestartReason` is not extended for it. Process exit (`ExitThreadCore`) is likewise
  shell-only. `RestartReason` changes only the log line, never the restart mechanism.
- `Action.Restart` (either reason) → existing `RestartProcess()`; the in-process
  re-init paths (`RestartWatchingAsync` re-init after teardown, capture-failed re-init,
  dark-feed re-evaluation) are deleted. Startup retry for a never-acquired camera remains,
  gated by `Policy.status(state)` being `Status.AcquiringCamera` or `Status.Recovering` — the
  complete gate, per the Status priority table — rather than shell-local
  `paused`/`sessionLocked` booleans (first-init retries are empirically safe and are not
  re-initialization).
- `OnCaptureFailed` emits the flagless `Event.CaptureFailed` (not `Event.InitFailed`) — see
  "Notes: recovery boundary"; it classifies via `IsHandleInvalid(args.Code, args.Message)` for
  its own log line only. Its current unconditional in-process retry loop is deleted; when the
  requested restart is cooldown-suppressed, the shell tears down the dead capture but keeps
  the sample timer running so the `NoFrame`→re-evaluation backstop engages.
- `SystemEvents.SessionSwitch` reasons other than `SessionLock`/`SessionUnlock` are intentionally
  filtered out and never reach `step`, matching current behavior. **Known limitation, stated
  rather than silent:** fast user switching (`ConsoleConnect`/`ConsoleDisconnect`) is out of
  scope for this RFC; the watcher may continue sampling a camera it no longer has access to
  during a switched-away session, producing `NoFrame` noise and possibly a `CaptureFailed`
  (the capture was live, so post-success failure noise is by definition `CaptureFailed`, never
  `InitFailed`). Consequence, stated precisely: never a spurious lock (fail-open holds), but a
  `CaptureFailed` while switched away can trigger at most one process self-restart per
  `RecoveryCooldownMs` — bounded, and invisible from the other user's console session.
  Suppressing it would require modeling console-switch events, which stays out of scope.
- The Settings dialog's modal loop does **not** pause sampling — unchanged from today,
  deliberately, not by omission. (Tray "Lock now" continues to call `LockWorkStation()` directly,
  bypassing `step` — safe by construction, since the subsequent OS `SessionLock` callback still
  produces `Event.SessionLocked` and re-baselines identically to an automatic lock.)
- `InputIdleMs()`'s unsigned-wraparound arithmetic against 32-bit `GetLastInputInfo`/`dwTime` is
  preserved verbatim — it is unrelated to the `TickCount64`-based core clock and must not be
  "simplified" during the `SampleAsync` rewrite.
- `RestartProcess()`'s ordering is audited during slice 8: confirm `sampleTimer`/`retryTimer`
  are stopped **before** the mutex is released and the new process is spawned (today
  `ExitThreadCore` stops them, but it runs after `Process.Start` — reorder so timers stop
  first, closing the handoff window where both processes could theoretically be live
  simultaneously), and confirm the stamps-file write (restart stamp + paused flag)
  **completes before** `Process.Start()` — the child's `Policy.start` correctness depends on
  what is on disk at spawn time. Final order: stop timers → write stamps/paused flag →
  release mutex → spawn → `ExitThread()`.
- Status strings map 1:1 from `Status` (now `Status.Watching`, `Status.Paused`, etc. under
  `RequireQualifiedAccess` — this also removes the `PausedStatus`/`Event.Paused` naming
  collision workaround). `SetStatus`'s **second render target** is on the 8b checklist too:
  `pauseItem.Text` ("Pause"/"Resume") derives from `Policy.status(state) = Status.Paused`,
  not a shell-local bool — easy to drop silently because it isn't Lock/Restart-adjacent.
- `sampling`/`starting`/`settingsOpen` reentrancy guards and the 1 MB log-cap conditional are
  shell concurrency/hygiene mechanisms, not decision logic — classified here explicitly as
  deliberately unchanged.
- A `Status`/`Action` → log-line mapping table (which shell code logs what, for which `Status`
  transition or `Action`) is a **slice 8b deliverable**, not left implicit — see "Slices."

### Testing

- `PresenceLock.Core.Tests` (F#, xunit + FsCheck): table-driven scenario tests replaying the
  documented incidents, each tagged with the slice that introduces it:
  - never-seen must not lock — slice 3
  - look-away locks at threshold — slice 3
  - slow `InitSucceeded` does not consume grace measured from `start` — slice 3
  - dim-light + typing must not lock — slice 4 (see classification-coverage caveat below)
  - `NoFrame`/`DarkFrame` blip resets, not merely pauses, the away clock — slice 4
  - grace after unlock; pause takes precedence over an unlock-driven resume; session-event
    re-delivery is a no-op; config changed mid-grace applies immediately — slice 5
  - an 8-hour tick gap followed by `NoFace` (idle satisfied) locks on the next sample; the same
    gap followed by `NoFrame` does not — slice 5
  - recovery cooldown; `CaptureFailed` after `InitSucceeded` restarts unconditionally (subject
    only to cooldown — not the failure-count threshold, and regardless of classification);
    `CaptureFailed` while `Paused` still requests `Action.Restart`; a cooldown-suppressed
    `CaptureFailed` recovers via the `NoFrame`→re-evaluation backstop — slice 6
  - reboot-then-recover (restart stamps survive a real reboot) — slice 9 live smoke only;
    no automated container test can exercise an actual reboot.
- **Classification-coverage caveat:** the dim-light scenario test constructs `Observation.DarkFrame`
  directly — it exercises the decision-given-classification half only. The shell's actual luma
  classification (`MeanLuma`/`DarkFrameMeanThreshold`) is untouched and untested by this change.
  If the historical "fail-closed dim-light lock" incident's root cause was in classification
  rather than decision, this suite does not regress-test it; confirm against incident logs, and
  if unconfirmed, add shell-side unit tests for `MeanLuma` as a follow-up, not implied coverage.
- FsCheck invariants (generators/`Arbitrary` for `PolicyConfig`/`Event` introduced in slice 2,
  reused by every slice from 3 onward; each property lands in the slice that introduces the
  logic it constrains — see Slices). The former properties
  about effect-list cardinality and `SetStatus` emission are gone — the single-`Action` return
  and pulled status make them unrepresentable rather than testable:
  1. No `Action.Lock` unless armed — verified against an independent test-side model of armed
     ("a `FaceSeen` since the last baseline event"), never by reading `Snapshot.Armed` back as
     the oracle for the very flag it validates (property 4 remains the fully black-box
     variant).
  2. No `Action.Lock` while `inputIdleMs < InputIdleRequiredMs`.
  3. No `Action.Lock` before `GraceMs` elapses after a baseline event.
  4. `NoFrame`/`DarkFrame` sequences alone never produce `Action.Lock`.
  5. At most one `Action.Restart CameraWedged` per `RecoveryCooldownMs` window.
  6. At most one `Action.Restart CameraReevaluation` per `ReevaluateCooldownMs` window.
     (Properties 5 and 6 are quantified over a **randomized initial `RestartStamps`** passed
     to `start` — including "a restart just happened moments ago" — not only empty stamps;
     cross-restart cooldown continuity is the entire point of the wall-clock stamp design.)
  7. A continuous run of `NoFace` observations shorter than `AwayThresholdMs` never locks,
     regardless of armed/grace/idle state (continuity, not just the four gates individually).
  8. `Sample` events while `Status` is `SessionLocked` or `Paused` never change the armed/grace
     baseline and never emit `Action.Lock`. **SUPERSEDED by 0002-environment-levels.md
     (StepInputs/Reconcile):** this property is superseded by property 13 (status/level
     agreement over the full six-row priority table, plus `lockInhibited` agreement), which
     asserts the same claim over sampled `StepInputs` rather than folded `Status` flags, across
     an exhaustively explored state graph rather than this property's generated sequences.
  9. If `Action.Lock` is emitted for a `Sample` and no `SessionLocked` follows, an identical
     subsequent `Sample` re-emits `Action.Lock` (no internal latch).
- `pack.ps1` gains a container `dotnet test` gate before publish; a failing core test fails
  the build.

### Out of scope

- IR frame-source support (pending hardware decision).
- Changing any current *intended* behavior — this is an extraction with parity, verified by
  the scenario tests, plus **two** deliberate behavior changes (both narrower than "parity"
  once named explicitly, so calling them out here rather than letting either hide inside the
  extraction):
  1. Re-evaluation/recovery become process restarts (candidate 1 fix).
  2. A capture failure on an already-live camera (`CaptureFailed`) now requests a wedge-recovery
     restart unconditionally on first occurrence (subject to cooldown), where today it retries
     in-process forever with no gating at all — see "Notes: recovery boundary." This is a
     second, independent fix bundled into the same RFC because it closes the same class of bug.
- Testing the shell's effect-wiring beyond slice 8's shadow-mode burn-in and slice 9's live
  smoke — a fake action-executor/mock-camera integration harness for `WatcherContext` is a
  reasonable follow-up but is not scoped here.
- Settings-dialog UI for the five new `PolicyConfig` fields (`NoSignalReportAfterMs`,
  `ReevaluateAfterMs`, `ReevaluateCooldownMs`, `RecoveryFailureThreshold`,
  `RecoveryCooldownMs`) — they remain file-only tunables in this RFC; `SettingsForm.cs` is not
  extended.
- Camera-arrival preference upgrade: plugging in a preferred external camera while the internal
  one is delivering healthy frames does not trigger a switch — unchanged from today, recorded
  here as a known gap rather than an omission.
- `SystemEvents.SessionEnding` / abrupt termination (logoff, OS shutdown) with a live camera:
  not handled today and not added here — the wedge trigger is in-process re-initialization,
  not process exit; no observed wedge has ever followed an abrupt exit. Recorded as a
  considered non-issue rather than an omission.

## Slices

1. **Scaffold**: first, resolve and **pin** exact package versions — xunit + FsCheck +
   FsCheck.Xunit on net10.0 (start from the proven xunit 2.9.x / FsCheck 2.16.x pairing;
   "latest of both majors together" is the riskiest combination — FsCheck 3.x changed
   `Arbitrary` registration and namespaces, xunit v3 is a different runner model). Fallback if
   `FsCheck.Xunit`'s attribute integration fights the chosen combination:
   `Check.QuickThrowOnFailure` inside ordinary `[<Fact>]`s needs no attribute integration at
   all. Then `PresenceLock.Core.fsproj` + `PresenceLock.Core.Tests.fsproj` — no solution
   file; both projects are addressed by path (add a `.sln` later only as optional IDE
   convenience). Core must **not** declare `RuntimeIdentifier(s)`, `SelfContained`, or a
   Windows TFM — the win-x64 self-contained publish of the WinExe resolves the plain-`net10.0`
   reference as-is, and adding RID/TFM settings "defensively" would undermine the
   testable-anywhere goal. Do not add an explicit `FSharp.Core` PackageReference to
   `PresenceLock.csproj`; it flows transitively from the ProjectReference. Add `TestResults/`
   to `.gitignore`. Container test gate in `pack.ps1`, before the publish step:

   ```powershell
   docker run --rm `
       -v "${PSScriptRoot}:C:\src" -v "presencelock-nuget:C:\nuget" `
       -e NUGET_PACKAGES=C:\nuget -w C:\src $image `
       dotnet test PresenceLock.Core.Tests\PresenceLock.Core.Tests.fsproj -c Release
   if ($LASTEXITCODE) { throw 'core tests failed' }
   ```

   Inner dev loop (the host has no .NET SDK by design — tooling stays in Docker): a
   long-running container running `dotnet watch test` over the mounted source, started once
   per session, gives fast re-test cycles without per-run container cold starts. **Verify the
   watcher actually fires**: edit the trivial `[<Fact>]`'s assertion and confirm a re-run
   within a few seconds — file-change notification across a Hyper-V-isolated bind mount is a
   known weak spot, and the failure mode is silence (one initial run, then never again). If
   it doesn't re-run, add `-e DOTNET_USE_POLLING_FILE_WATCHER=1` to the `docker run` and
   re-verify. One trivial
   xunit `[<Fact>]` **and** one trivial FsCheck `[<Property>]` (e.g. list-reverse involution)
   prove the full xunit+FsCheck+net10.0 toolchain in the container and confirm the new
   packages restore into the shared NuGet volume. Version-bump rule, stated once: every
   installed build increments the MSIX revision (1.0.7.0 → 1.0.8.0 at slice 8c).
2. **Types + baseline**: `MonotonicMs`/`WallClockMs`/`PolicyConfig`/`Observation`/`Event`/
   `Status`/`Action`/`RestartStamps`/`State`/`Snapshot`/`StepResult`, `Policy.start`,
   `Policy.status`, `Policy.snapshot`; FsCheck `Arbitrary` generators for
   `PolicyConfig`/`Event` introduced here (reused by every later slice); tests: baseline
   state is unarmed, in grace, per `Policy.snapshot`. `State`'s private record sketches its
   **full final shape** (fields needed through slice 6 — signal/recovery baselines, pause
   flag, stamps) even though only the baseline subset is exercised here. **SUPERSEDED by
   0002-environment-levels.md (StepInputs/Reconcile):** the folded `pause`/session-locked
   booleans this sketch describes are gone from `State`'s actual current shape — replaced by
   `LastInputs: StepInputs`, the previous call's sampled levels, with exactly one writer
   (`step` itself).
3. **Lock rules (Sample-only scenarios)**: `step` for `Sample`/`InitSucceeded` events — armed
   transition, away threshold, input-idle gate, grace baselined from `InitSucceeded`; table
   tests restricted to what's expressible without `SessionUnlocked`/`Resumed`/recovery events
   (those scenarios move to slices 5/6, not forward-referenced here). Properties 1–3 and 7
   land here, beside the logic they constrain (property 1 via the independent test-side armed
   model — see Testing).
4. **Signal accounting**: `NoFrame`/`DarkFrame` streaks reset the away-baseline;
   `Status.NoSignal` after `NoSignalReportAfterMs`; `Action.Restart CameraReevaluation` after
   `ReevaluateAfterMs`, gated by `ReevaluateCooldownMs`; fail-open tests; properties 4 and 6
   land here. Pinned semantics: a threshold-crossed bad-signal duration stays **saturated**
   while its action is cooldown-suppressed — no reset on a suppressed attempt — so the
   restart fires immediately once the cooldown clears, not up to `ReevaluateAfterMs` later.
5. **Session/pause events**: `SessionLocked`/`SessionUnlocked`/`Paused`/`Resumed` re-baselining;
   pause-precedes-unlock-resume test; grace-after-unlock scenario; session-event idempotency;
   config-changed-mid-grace; `Sample`-is-no-op-while-`Status.SessionLocked`/`Paused` test
   (property 8); 8-hour tick-gap scenarios.
6. **Recovery policy**: `InitFailed` (pre-success, streak-gated) vs. `CaptureFailed`
   (post-success, flagless, unconditional-subject-to-cooldown) discrimination; cooldowns from
   `RestartStamps` using `nowWall: WallClockMs`, never compared to the monotonic clock;
   `Action.Restart CameraWedged`; property tests 5 and 9; the `CaptureFailed`-while-`Paused`
   and cooldown-suppressed-backstop scenarios. **Refactor note:** extract both shared idioms
   introduced in slice 4 — (a) streak counting (bump/reset/crossed-threshold) and (b) the
   wall-clock cooldown check (`nowWall` vs. a `RestartStamps` field vs. a cooldown duration,
   including the absent-stamp case) — into internal helpers before or while adding the
   recovery streak and wedge cooldown, rather than re-deriving either independently.
7. *(Folded into slices 3 and 4 — round 2. A pure-test slice running after unrelated slices 5
   and 6 would let a property failure bisect to any of four slices; each property now lands
   beside the logic it constrains. Numbering preserved so the handoff ledger's slice names
   stay stable.)*
8a. **Shell shadow-mode wiring** — the long pole of the shell work, not a warm-up for 8b:
   shadowing requires writing, for all five call sites, the permanent Event-construction
   code that 8b keeps. Concrete contract:
   - Factor each call site's event construction into a small named function (e.g.
     `ClassifySample(...): Event`) reused unchanged by 8a's `ShadowAdvance` and 8b's
     `Advance` — the 8b swap is then mechanical.
   - `ShadowAdvance(Event)`: its own `State` field (own `Policy.start` call, same
     `now`/stamps inputs), calls `Policy.step`, **never executes** the returned `Action`.
     The executing `Advance` exists only from 8b.
   - Divergence = the **would-lock boolean only**: shadow `Action.Lock` vs. legacy's
     about-to-call-`LockWorkStation` moment. Restart-shaped decisions have **no legacy
     analog by design** — they are this RFC's two named behavior changes — so they are
     logged tagged `SHADOW-EXPECTED`, never counted as divergences; otherwise burn-in could
     never show clean.
   - Repeat-suppression: the first occurrence of each distinct divergence kind per process
     run is logged in full, count-only thereafter — a cascading state divergence must not
     blow the 1 MB log cap and delete its own most diagnostic first occurrence.
   - Exit criterion: zero lock-divergences across ≥2 real lock/unlock cycles plus ≥1 full
     working day of ordinary use.
8b. **Cutover**: once shadow mode meets the exit criterion, rename `ShadowAdvance` to
   `Advance`, wire `Action` execution, and delete the superseded C# decision conditionals.
   Checklist beyond the deletions: the three `KickAcquisitionIfNeeded`/retry-gate call
   sites; `pauseItem.Text` rendered from `Policy.status`; the `Action.Lock` timer-stop
   discipline; and the `Status`/`Action` → log-line mapping table (slice 9's checklist),
   which includes **transition** entries, not just statuses — e.g. the once-per-episode
   dark-vs-no-frame diagnostic line fires on the transition *into* `Status.NoSignal`, from
   the shell's own current-sample dark/no-frame knowledge.
8c. **Delete in-process re-init paths** (`RestartWatchingAsync` teardown/reinit, the
   `OnCaptureFailed` in-process retry, dark-feed re-evaluation), replaced by `Action.Restart` —
   isolated as its own step so a slice-9 regression is attributable to this specific,
   higher-risk change rather than conflated with the parity-preserving cutover. Also here:
   delete the superseded `last-restart.txt` on first stamps-file write; update `CLAUDE.md`'s
   build/test notes (Core + Tests projects, container test gate, watch-container dev loop);
   stop the running instance before `Add-AppxPackage` (existing practice, now written down);
   bump MSIX version; container build green.
9. **Live smoke**, checkpointed — each step log-verified before the next, with an elevated
   PowerShell window pre-staged (`Restart-Service FrameServer`) so a wedge doesn't cost a
   mid-test UAC negotiation: (1) install and idle in Watching for several minutes; (2) exactly
   one lock/unlock cycle, confirming the camera stayed alive; (3) two more cycles
   back-to-back; (4) dim-light-while-typing; (5) pause/resume, including pause surviving a
   camera-filter-change restart and Exit **not** inheriting a stale pause; (6) sleep/resume —
   also log `TickCount64` before sleep and after wake and confirm the delta tracks wall-clock
   elapsed time (hardware validation of the gap-oblivious premise); (7) fast-user-switch
   no-op check; (8) unplug the USB camera mid-watch — expect at most one restart, then a
   `Recovering` first-init retry loop — replug and confirm reacquisition;
   (9) reboot-then-recover (validates restart stamps survive a real reboot — the one scenario
   no container test can exercise); **re-open and re-elevate the recovery console immediately
   after logging back in** (the pre-staged window does not survive the reboot), and bound the
   wait: tray icon plus a `started` log line within ~2 minutes of logon, else the StartupTask
   didn't fire (check Settings → Apps → Startup — an install/packaging issue, not a Core
   bug). Log review checked against slice 8b's mapping table, not open-ended.

Each slice is independently testable; 1–6 (including the properties folded into 3 and 4) run
entirely in the container. Slices 8a–8c touch the Windows shell and cannot run in the
container; slice 9 is the only slice that touches a real camera, a real session, and a real
reboot.

## Addendum: camera-arrival upgrade (2026-08-01, post-cutover — slice 10)

Closes the R1-39 out-of-scope gap, promoted to in-scope by Corey after days of live 1.0.8.0
use: docking (LifeCam arrival) never switches away from a healthy internal camera, because
selection runs once at process start and restarts fire only on failure paths. Design follows
the reviewed restart patterns exactly; this is the "third reason" R2-40 anticipated (named
`Nullable` stamp fields remain the right shape at three).

**Core contract changes (the only post-cutover contract amendment):**
- `Event.BetterCameraAvailable` — fed by the shell after device-arrival settle + preference
  check. Semantics in `step`: if `HasSucceededOnce` and `UpgradeCooldownMs` has elapsed since
  `Stamps.UpgradeAt` (wall clock), emit `Action.Restart RestartReason.CameraUpgrade`;
  otherwise `NoAction`. Not gated by `Paused`/`SessionLocked` (consistent with failure
  events: pause persists across restarts, an elective restart while paused/locked is
  harmless, and one coherent rule beats three special cases). Pre-first-success it is a
  no-op: the acquisition retry loop re-runs selection anyway, so a restart would be waste.
- `RestartReason.CameraUpgrade`; `RestartStamps`/`Snapshot` gain `UpgradeAt` /
  `LastUpgradeRestartAt` (`Nullable<int64>`, wall clock); `PolicyConfig` gains
  `UpgradeCooldownMs` (default 600000) — the validation unit becomes nine fields, the
  flat-json schema stays backward compatible (absent key → default; stamps file: absent →
  null).
- FsCheck property 12: at most one `Restart CameraUpgrade` per `UpgradeCooldownMs` window,
  quantified over randomized initial stamps including just-restarted (R2-19 pattern).

**Shell changes:**
- `DeviceWatcher` over video-capture devices, events marshaled to the UI thread (same
  precedent as SessionSwitch). Ignore the initial-enumeration `Added` backfill — react only
  after `EnumerationCompleted`. On a genuine arrival, debounce with a settle timer (~5 s,
  restarted per event — docks enumerate several devices over seconds), then compare.
- Camera preference factored into a pure, unit-tested ranking function in `PolicyBridge`
  operating on plain (name, panel) data — the same ordering `StartWatchingAsync` uses (user
  filter match, else external over built-in front), used by both startup selection and the
  watcher. If the would-pick-now camera differs from the in-use one →
  `Advance(Event.BetterCameraAvailable)`; the core decides, `RestartProcess` writes the
  `UpgradeAt` stamp for the reason it executes (R1-29 rule unchanged).
- Removals need no handling here: device loss surfaces as `CaptureFailed` (existing path).

**Verification:** container tests (core + shell) green; live check = dock → within settle +
restart time the log shows the upgrade restart and the new process on the LifeCam; ships as
1.0.8.1 and folds into the combined slice-9 smoke.
