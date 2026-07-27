# RFC: PresenceLock.Core — full-brain policy extraction to F#

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

- `now: int64` — monotonic milliseconds, shell-supplied via `Environment.TickCount64`. Drives
  grace/away/idle/streak timing within `step`. `TickCount64` is **boot-relative, not
  process-relative**: it is continuous across the plain process restarts this design performs
  routinely (recovery, camera re-evaluation), so `now` before and after a `Action.Restart` are
  directly comparable. It resets near zero only on an actual OS reboot.
- `nowWallMs` / `RestartStamps` — wall-clock milliseconds since the Unix epoch
  (`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`), used **exclusively** for the restart
  cooldown gates (`RecoveryCooldownMs` / `ReevaluateCooldownMs`), because those gates alone must
  remain meaningful across a reboot — a genuinely wedged/broken camera doesn't get better after
  a reboot, and the whole point of the cooldown is to survive exactly that. The stamps (one per
  `RestartReason`) are persisted by the shell (JSON, wall-clock epoch ms) and passed into
  `Policy.start`.

Sleep/hibernate: the core is deliberately **gap-oblivious**. A large jump in `now` between two
`Sample` calls (e.g. an 8-hour sleep) is treated identically to the same delta spread over many
small samples — no special-cased re-grace on wake. Consequences, made explicit so they're a
decision and not an accident: a `NoFace` sample arriving right after a long sleep, with idle and
away thresholds already satisfied by the elapsed wall/tick time, locks on that very sample; a
`NoFrame`/`DarkFrame` sample in the same spot does not, because those observations reset the
away-baseline (see "Notes" below) exactly as `FaceSeen` does. `step` never assumes `now` is
non-decreasing between calls beyond: if it ever goes backward, every elapsed-time comparison
must fail closed (never lock, never restart) rather than throw or wrap.

### Types (the contract with the shell)

```fsharp
type PolicyConfig =
    { AwayThresholdMs: int64
      InputIdleRequiredMs: int64
      GraceMs: int64
      NoSignalReportAfterMs: int64       // continuous bad signal before surfacing no-signal status
      ReevaluateAfterMs: int64           // continuous bad signal before requesting camera re-evaluation
      ReevaluateCooldownMs: int64        // minimum gap between CameraReevaluation restarts (wall-clock)
      RecoveryFailureThreshold: int      // consecutive pre-success init failures before requesting recovery
      RecoveryCooldownMs: int64 }        // minimum gap between CameraWedged restarts (wall-clock)

type Observation = FaceSeen | NoFace | NoFrame | DarkFrame

[<RequireQualifiedAccess>]
type Event =
    | Sample of Observation * inputIdleMs: int64
    | InitSucceeded
    /// Startup/retry failure, before any InitSucceeded this process. Streak-gated by
    /// RecoveryFailureThreshold — see "Notes: recovery boundary" below.
    | InitFailed of handleInvalid: bool
    /// A previously-live capture died mid-session (shell's MediaCapture.Failed). Distinct
    /// from InitFailed: see "Notes: recovery boundary."
    | CaptureFailed of handleInvalid: bool
    | SessionLocked
    | SessionUnlocked
    | Paused
    | Resumed

[<RequireQualifiedAccess>]
type Status =                             // semantic; shell renders strings (pulled, never pushed)
    | Watching | NoSignal | SessionLocked | Paused
    | AcquiringCamera | Recovering

type RestartReason = CameraWedged | CameraReevaluation

/// Exactly one Action per step — cardinality is enforced by the type, not by convention.
[<RequireQualifiedAccess>]
type Action =
    | NoAction
    | Lock
    | Restart of reason: RestartReason   // process restart: recovery OR camera re-evaluation

/// Persisted wall-clock (Unix-epoch ms) restart stamps, one per RestartReason, carried across
/// process restarts by the shell. Nullable (not option) deliberately: this type is constructed
/// at the C# boundary. Never compared against the monotonic `now`.
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
      LastWedgeRestartAt: int64 option
      LastReevalRestartAt: int64 option }

module Policy =
    /// Called exactly once per process, in WatcherContext's constructor, before the first
    /// camera-acquisition attempt. All later re-baselining goes through events into the same
    /// State — never a second start call.
    val start    : PolicyConfig -> now:int64 -> RestartStamps -> State
    val step     : PolicyConfig -> State -> now:int64 -> nowWallMs:int64 -> Event -> State * Action
    val status   : State -> Status
    val snapshot : now:int64 -> State -> Snapshot
```

Notes:
- `step` returns exactly one `Action` (usually `Action.NoAction`) — cardinality by type, not by
  documented convention. Status is **pulled, never pushed**: the shell calls `Policy.status`
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
- Input-idle gating is core-owned (idle supplied as data).
- **Session/pause precedence:** `SessionUnlocked` while an internal `paused` flag is set is a
  no-op — it does not re-baseline, arm, or change `Status` away from `Paused` — matching
  `Program.cs`'s `if (paused) return;` guard in `OnSessionSwitch`. Pause takes precedence over
  an unlock-driven resume; only an explicit `Resumed` event clears it. Truth table:

  | Event            | while Paused        | while not Paused                    |
  |-------------------|---------------------|--------------------------------------|
  | `SessionLocked`   | stays Paused         | → `Status.SessionLocked`, sampling stops       |
  | `SessionUnlocked` | no-op (stays Paused) | re-baseline, disarm, re-grace, resume |
  | `Paused`          | no-op                | → `Paused`, sampling stops            |
  | `Resumed`         | re-baseline, disarm, re-grace, resume | no-op (already resumed) |

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
  camera event was a failure) and returns `Action.NoAction` — the shell's independent, core-unmodeled retry timer
  continues unchanged, gated by `Policy.status state` per the Motivation's scope note. Mirrors
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
  - `CaptureFailed` (a previously-live capture just died): **not** streak-gated. Once
    `InitSucceeded` has occurred in this process, *any* subsequent `CaptureFailed handleInvalid:
    true` requests `Action.Restart CameraWedged` on its very first occurrence, subject only to
    `RecoveryCooldownMs` — never to `RecoveryFailureThreshold`. Any in-process reinit attempt
    after a live capture dies is exactly the wedge trigger this RFC exists to eliminate; gating
    it behind a failure count (as a naive collapse of `InitFailed`/`CaptureFailed` into one
    constructor would do) would silently retry in-process up to `RecoveryFailureThreshold - 1`
    times first, each one a real chance to wedge the FrameServer.
  - The shell's `handleInvalid` classifier itself (HResult `0x80070006` OR a message-substring
    match, because the WinRT projection doesn't reliably preserve the HResult) remains
    shell-side, fragile, and outside Core's test suite by construction — it is sensing (facts
    about the exception), not policy. Because it is exactly the class of "expected behavior
    existed only as code" defect the Motivation cites, it must be extracted into its own named,
    unit-tested function (e.g. `static bool IsHandleInvalid(Exception ex)`) with dedicated
    xunit tests for both the HResult path and the message-substring fallback, even though it
    lives outside `PresenceLock.Core`.
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
- Out-of-range or unparseable values: log and fall back to built-in defaults for the whole
  file, never a partially-defaulted mix — a zero-filled cooldown would otherwise make the next
  camera hiccup an instant restart loop.
- **Restart stamps:** `last-restart.txt` is superseded by a small JSON stamps file (wall-clock
  epoch ms, one stamp per `RestartReason`, plus the persisted paused flag — see "Shell
  changes"). Any read/parse error yields empty stamps — parse failure must never block
  recovery. The shell writes only the stamp matching the `RestartReason` it is executing, so
  routine re-evaluation restarts can never poison the wedge-recovery cooldown. The stamp
  read/write shim, the config mapping function, and the `IsHandleInvalid` classifier all get
  dedicated shell-side xunit tests (see Notes).

### Shell changes (`Program.cs`)

- `WatcherContext` gains a single `Advance(Event)` method — the **only** place `state` is
  reassigned. It computes `now`/`nowWallMs`, calls `Policy.step`, executes the returned
  `Action`, logs `Policy.snapshot` alongside every `Lock`/`Restart` action (preserving today's
  diagnostic-rich log lines), and re-renders `Policy.status`. Every call site (`SampleAsync`,
  init success/failure, `OnSessionSwitch`, `TogglePause`, `OnCaptureFailed`) goes through it,
  so action handling and status refresh can never drift per call site.
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
  gated by `Policy.status state` rather than shell-local `paused`/`sessionLocked` booleans
  (first-init retries are empirically safe and are not re-initialization).
- `OnCaptureFailed` emits `Event.CaptureFailed handleInvalid` (not `Event.InitFailed`) — see
  "Notes: recovery boundary." Its current unconditional in-process retry loop is deleted.
- `SystemEvents.SessionSwitch` reasons other than `SessionLock`/`SessionUnlock` are intentionally
  filtered out and never reach `step`, matching current behavior. **Known limitation, stated
  rather than silent:** fast user switching (`ConsoleConnect`/`ConsoleDisconnect`) is out of
  scope for this RFC; the watcher may continue sampling a camera it no longer has access to
  during a switched-away session, producing `NoFrame`/`InitFailed` noise but never a spurious
  lock (fail-open holds regardless).
- The Settings dialog's modal loop does **not** pause sampling — unchanged from today,
  deliberately, not by omission. (Tray "Lock now" continues to call `LockWorkStation()` directly,
  bypassing `step` — safe by construction, since the subsequent OS `SessionLock` callback still
  produces `Event.SessionLocked` and re-baselines identically to an automatic lock.)
- `InputIdleMs()`'s unsigned-wraparound arithmetic against 32-bit `GetLastInputInfo`/`dwTime` is
  preserved verbatim — it is unrelated to the `TickCount64`-based core clock and must not be
  "simplified" during the `SampleAsync` rewrite.
- `RestartProcess()`'s mutex-release-then-spawn ordering is audited during slice 8: confirm
  `sampleTimer`/`retryTimer` are stopped **before** the mutex is released and the new process is
  spawned (today `ExitThreadCore` stops them, but it runs after `Process.Start` — reorder so
  timers stop first, closing the handoff window where both processes could theoretically be
  live simultaneously).
- Status strings map 1:1 from `Status` (now `Status.Watching`, `Status.Paused`, etc. under
  `RequireQualifiedAccess` — this also removes the `PausedStatus`/`Event.Paused` naming
  collision workaround).
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
    only to cooldown, not the failure-count threshold) — slice 6
  - reboot-then-recover (restart stamps survive a real reboot) — slice 9 live smoke only;
    no automated container test can exercise an actual reboot.
- **Classification-coverage caveat:** the dim-light scenario test constructs `Observation.DarkFrame`
  directly — it exercises the decision-given-classification half only. The shell's actual luma
  classification (`MeanLuma`/`DarkFrameMeanThreshold`) is untouched and untested by this change.
  If the historical "fail-closed dim-light lock" incident's root cause was in classification
  rather than decision, this suite does not regress-test it; confirm against incident logs, and
  if unconfirmed, add shell-side unit tests for `MeanLuma` as a follow-up, not implied coverage.
- FsCheck invariants (generators/`Arbitrary` for `PolicyConfig`/`Event` introduced in slice 2,
  reused by every slice from 3 onward, not front-loaded into slice 7). The former properties
  about effect-list cardinality and `SetStatus` emission are gone — the single-`Action` return
  and pulled status make them unrepresentable rather than testable:
  1. No `Action.Lock` unless armed.
  2. No `Action.Lock` while `inputIdleMs < InputIdleRequiredMs`.
  3. No `Action.Lock` before `GraceMs` elapses after a baseline event.
  4. `NoFrame`/`DarkFrame` sequences alone never produce `Action.Lock`.
  5. At most one `Action.Restart CameraWedged` per `RecoveryCooldownMs` window.
  6. At most one `Action.Restart CameraReevaluation` per `ReevaluateCooldownMs` window.
  7. A continuous run of `NoFace` observations shorter than `AwayThresholdMs` never locks,
     regardless of armed/grace/idle state (continuity, not just the four gates individually).
  8. `Sample` events while `Status` is `SessionLocked` or `Paused` never change the armed/grace
     baseline and never emit `Action.Lock`.
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

## Slices

1. **Scaffold**: `PresenceLock.Core.fsproj` + `PresenceLock.Core.Tests.fsproj` — no solution
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
   per session, gives fast re-test cycles without per-run container cold starts. One trivial
   xunit `[<Fact>]` **and** one trivial FsCheck `[<Property>]` (e.g. list-reverse involution)
   prove the full xunit+FsCheck+net10.0 toolchain in the container and confirm the new
   packages restore into the shared NuGet volume. Version-bump rule, stated once: every
   installed build increments the MSIX revision (1.0.7.0 → 1.0.8.0 at slice 8c).
2. **Types + baseline**: `PolicyConfig`/`Observation`/`Event`/`Status`/`Action`/
   `RestartStamps`/`State`/`Snapshot`, `Policy.start`, `Policy.status`, `Policy.snapshot`;
   FsCheck `Arbitrary` generators for `PolicyConfig`/`Event` introduced here (reused by every
   later slice); tests: baseline state is unarmed, in grace, per `Policy.snapshot`.
3. **Lock rules (Sample-only scenarios)**: `step` for `Sample`/`InitSucceeded` events — armed
   transition, away threshold, input-idle gate, grace baselined from `InitSucceeded`; table
   tests restricted to what's expressible without `SessionUnlocked`/`Resumed`/recovery events
   (those scenarios move to slices 5/6, not forward-referenced here).
4. **Signal accounting**: `NoFrame`/`DarkFrame` streaks reset the away-baseline;
   `Status.NoSignal` after `NoSignalReportAfterMs`; `Action.Restart CameraReevaluation` after
   `ReevaluateAfterMs`, gated by `ReevaluateCooldownMs`; fail-open tests; property test 6.
5. **Session/pause events**: `SessionLocked`/`SessionUnlocked`/`Paused`/`Resumed` re-baselining;
   pause-precedes-unlock-resume test; grace-after-unlock scenario; session-event idempotency;
   config-changed-mid-grace; `Sample`-is-no-op-while-`Status.SessionLocked`/`Paused` test
   (property 8); 8-hour tick-gap scenarios.
6. **Recovery policy**: `InitFailed` (pre-success, streak-gated) vs. `CaptureFailed`
   (post-success, unconditional-subject-to-cooldown) discrimination; cooldowns from
   `RestartStamps` using `nowWallMs`, never compared to monotonic `now`;
   `Action.Restart CameraWedged`; property tests 5 and 9. **Refactor note:** extract the streak-counting idiom
   introduced in slice 4 (bump/reset/crossed-threshold) into one shared internal helper before
   or while adding the recovery streak, rather than re-deriving it independently.
7. **FsCheck invariant suite**: properties 1–4 and 7 over random event sequences, using the
   generators already built in slice 2.
8a. **Shell shadow-mode wiring**: call `Policy.step` alongside the still-live legacy
   conditionals; log any divergence between the legacy decision and the core's decision; burn
   in on a real session before proceeding to cutover.
8b. **Cutover**: once shadow mode shows no divergence, delete the superseded C# decision
   conditionals; produce the `Status`/`Action` → log-line mapping table (slice 9's checklist).
8c. **Delete in-process re-init paths** (`RestartWatchingAsync` teardown/reinit, the
   `OnCaptureFailed` in-process retry, dark-feed re-evaluation), replaced by `Action.Restart` —
   isolated as its own step so a slice-9 regression is attributable to this specific,
   higher-risk change rather than conflated with the parity-preserving cutover; bump MSIX
   version; container build green.
9. **Live smoke**, checkpointed — each step log-verified before the next, with an elevated
   PowerShell window pre-staged (`Restart-Service FrameServer`) so a wedge doesn't cost a
   mid-test UAC negotiation: (1) install and idle in Watching for several minutes; (2) exactly
   one lock/unlock cycle, confirming the camera stayed alive; (3) two more cycles
   back-to-back; (4) dim-light-while-typing; (5) pause/resume, including pause surviving a
   camera-filter-change restart; (6) sleep/resume; (7) fast-user-switch no-op check;
   (8) reboot-then-recover (validates restart stamps survive a real reboot — the one scenario
   no container test can exercise). Log review checked against slice 8b's mapping table, not
   open-ended.

Each slice is independently testable; 1–7 run entirely in the container. Slices 8a–8c touch the
Windows shell and cannot run in the container; slice 9 is the only slice that touches a real
camera, a real session, and a real reboot.
