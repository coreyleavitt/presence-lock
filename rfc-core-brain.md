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

## Design

### New project: `PresenceLock.Core` (F#)

- TFM `net10.0` — **no Windows dependency**. Tests for it run inside the build container.
- Referenced by `PresenceLock.csproj`. Mixed-language solution (separate project; required).
- Pure: no clocks, no I/O, no mutation visible to callers. Time is always a parameter
  (`now: int64` milliseconds, monotonic — shell supplies `Environment.TickCount64`).

### Types (the contract with the shell)

```fsharp
type Config =
    { AwayThresholdMs: int64
      InputIdleRequiredMs: int64
      GraceMs: int64
      DarkFrameMeanThreshold: float      // used by shell classification; lives here so config is one record
      NoSignalReportAfter: int           // samples before surfacing no-signal status
      ReevaluateAfter: int               // samples of no-signal before requesting camera re-evaluation
      RecoveryFailureThreshold: int      // consecutive init failures before requesting recovery
      RecoveryCooldownMs: int64 }

type Observation = FaceSeen | NoFace | NoFrame | DarkFrame

type Event =
    | Sample of Observation * inputIdleMs: int64
    | InitSucceeded
    | InitFailed of handleInvalid: bool
    | SessionLocked
    | SessionUnlocked
    | Paused
    | Resumed

type Status =                             // semantic; shell renders strings
    | Watching | ArmedWatching | NoSignal | SessionIdle | PausedStatus
    | AcquiringCamera | Recovering

type Effect =
    | LockWorkstation
    | RequestRestart of reason: RestartReason   // process restart: recovery OR camera re-evaluation
    | SetStatus of Status
and RestartReason = CameraWedged | CameraReevaluation

type State                                 // opaque; private record internally

module Policy =
    /// lastRecoveryAt: persisted across restarts by the shell (timestamp file), None if never.
    val start : Config -> now:int64 -> lastRecoveryAt:int64 option -> State
    val step  : Config -> State -> now:int64 -> Event -> State * Effect list
```

Notes:
- `Effect list` (usually 0–1 effects, occasionally status + action) keeps `step` total and
  composition-friendly; the shell folds over it.
- The armed rule is core-owned: no `LockWorkstation` unless a `FaceSeen` occurred since the
  last `start`/`SessionUnlocked`/`Resumed` baseline. Fail-open rules (NoFrame/DarkFrame never
  advance toward lock) are core-owned. Input-idle gating is core-owned (idle supplied as data).
- Recovery decision (streak of handle-invalid `InitFailed` ≥ threshold AND cooldown elapsed)
  is core-owned; the shell persists `lastRecoveryAt` and executes `RequestRestart`.
- Camera *selection* (external-first ranking) and frame *classification* (luma → DarkFrame)
  stay in the shell: they are sensing, not policy. The dark threshold value lives in `Config`
  so configuration remains one record.

### Shell changes (`Program.cs`)

- `SampleAsync` classifies the frame → `Observation`, calls `Policy.step`, executes effects.
  All decision conditionals (`armed`, grace, streaks, cooldown file logic) are deleted.
- `RequestRestart` (either reason) → existing `RestartProcess()`; the in-process
  re-init paths (`RestartWatchingAsync` re-init after teardown, capture-failed re-init,
  dark-feed re-evaluation) are deleted. Startup retry for a never-acquired camera remains
  (first-init retries are empirically safe and are not re-initialization).
- Status strings map 1:1 from `Status`.

### Testing

- `PresenceLock.Core.Tests` (F#, xunit + FsCheck): table-driven scenario tests replaying the
  documented incidents (dim-light + typing must not lock; never-seen must not lock; look-away
  locks at threshold; grace after unlock; recovery cooldown), plus FsCheck invariants:
  1. No `LockWorkstation` unless armed.
  2. No `LockWorkstation` while `inputIdleMs < InputIdleRequiredMs`.
  3. No `LockWorkstation` before `GraceMs` elapses after a baseline event.
  4. `NoFrame`/`DarkFrame` sequences alone never produce `LockWorkstation`.
  5. At most one `RequestRestart CameraWedged` per `RecoveryCooldownMs` window.
- `pack.ps1` gains a container `dotnet test` gate before publish; a failing core test fails
  the build.

### Out of scope

- IR frame-source support (pending hardware decision).
- Changing any current *intended* behavior — this is an extraction with parity, verified by
  the scenario tests, plus the one deliberate behavior change: re-evaluation/recovery become
  process restarts (candidate 1 fix).

## Slices

1. **Scaffold**: `PresenceLock.Core.fsproj` + `PresenceLock.Core.Tests` + solution file;
   container `dotnet test` gate in `pack.ps1`; one trivial passing test proves the pipeline.
2. **Types + baseline**: `Config`/`Observation`/`Event`/`Status`/`Effect`/`State`,
   `Policy.start`; tests: baseline state is unarmed, in grace.
3. **Lock rules**: `step` for `Sample` events — armed transition, away threshold, input-idle
   gate, grace; table tests replaying the incident scenarios.
4. **Signal accounting**: NoFrame/DarkFrame streaks, `SetStatus NoSignal`,
   `RequestRestart CameraReevaluation` after `ReevaluateAfter`; fail-open tests.
5. **Session/pause events**: `SessionLocked`/`SessionUnlocked`/`Paused`/`Resumed`
   re-baselining; test that unlock disarms and re-graces.
6. **Recovery policy**: `InitFailed` streaks, handle-invalid discrimination, cooldown from
   `lastRecoveryAt`; `RequestRestart CameraWedged`; property test 5.
7. **FsCheck invariant suite**: properties 1–4 over random event sequences.
8. **Shell integration**: wire `WatcherContext` to `Policy`; delete superseded C# decision
   code and in-process re-init paths; bump MSIX version; container build green.
9. **Live smoke**: install, 3 lock/unlock cycles + dim-light-while-typing + pause/resume;
   log review.

Each slice is independently testable; 1–7 run entirely in the container.
