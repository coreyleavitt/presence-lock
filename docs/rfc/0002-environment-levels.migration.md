# RFC 0002-environment-levels — slice 1 test-migration mapping table

Deliverable named in `docs/rfc/0002-environment-levels.md`, "Slices" §1: an old-test →
new-test mapping table covering both test projects, at **one-row-per-test-function grain**
(53 rows for `PresenceLock.Core.Tests/Tests.fs`, not the ~170 `Policy.step`/`Policy.start`
call sites the RFC explicitly names as the wrong unit — the seven fold-based property tests
each wrap one textual call site exercised unboundedly per FsCheck run).

Scope: the table below records the MIGRATE phase as it happened — `PresenceLock.Core.Tests/
Tests.fs` and `Generators.fs` called exclusively `Policy.startLevels`/`Policy.stepLevels` at
that point, with zero references to `Policy.start`, `Policy.step`, `Event.SessionLocked`,
`Event.SessionUnlocked`, `Event.Paused`, `Event.Resumed`, `.IsPaused`, or `.IsSessionLocked`
remaining in test code (some appear in prose comments documenting the old→new mapping at
hand-rewritten call sites — never in executable code); `LevelsTests.fs` and all production
code (`PresenceLock.Core`, the C# shell/tests) were untouched at that point. The CONTRACT
phase (below, "PresenceLock.Tests (C#)") has since landed: `Policy.startLevels`/
`Policy.stepLevels` are renamed to `Policy.start`/`Policy.step` (the old event-folded shape
deleted outright — `Tests.fs`/`Generators.fs`/`LevelsTests.fs` all now call the canonical
names), and the C# shell (`Program.cs`/`PolicyBridge.cs`) is cut over onto `StepInputs`/
`StepContext`. The mapping table's "Policy.startLevels"/"Policy.stepLevels" references below
describe the MIGRATE-phase state at the time and are left as the historical record; they are
not stale claims about the current tree.

Final tally: 80/80 green, across four files in `PresenceLock.Core.Tests`:
`FrameFreshnessTests.fs` (8, untouched — no session/pause/legacy-API references),
`PresenceFilterTests.fs` (12, untouched, same reason), `LevelsTests.fs` (11, landed in the
EXPAND phase, unchanged here), and `Tests.fs` (49, migrated this phase: 36 `[<Fact>]` + 13
`[<Property>]`). Pre-migration baseline was 84/84 (8 + 12 + 11 + 53); `Tests.fs` alone had 53
test functions before this phase — the RFC's own pinned count ("53 test functions in
Tests.fs, not 170 call-site rows"; the earlier "73 legacy" figure in the slice handoff
aggregates all three pre-existing Core.Tests files, not `Tests.fs` alone). 4 of those 53 are
deleted this phase (1 pinned-wrong-behavior test, 3 superseded idempotency tests) — see rows
23–26 below. `53 - 4 = 49` remain in `Tests.fs`; `84 - 4 = 80` overall, so the suite's
assertions survive with their meaning intact, net of the deletions the RFC itself names.

## `PresenceLock.Core.Tests/Tests.fs` (53 rows)

| # | Old test | Disposition | New test / reason |
|---|----------|-------------|--------------------|
| 1 | `baseline status immediately after start is AcquiringCamera` | Mechanical | Same name; `Policy.start` → `Policy.startLevels(now, stamps, defaultInputs)`. |
| 2 | `a FaceSeen sample arms the state` | Mechanical | Same name; append `defaultInputs` via `levelsOf`/`levelsOfIdle`. |
| 3 | `look-away locks once armed, past grace, idle-satisfied, and the away threshold has elapsed` | Mechanical | Same name; idle payload routed through `levelsOfIdle`. |
| 4 | `a NoFace sample does not lock while inputIdleMs is below InputIdleRequiredMs, even past grace and away threshold` | Mechanical | Same name; idle payload routed through `levelsOfIdle`. |
| 5 | `a NoFace sample does not lock before GraceMs elapses since the baseline event, even though armed/idle/away are satisfied` | Mechanical | Same name. |
| 6 | `InitSucceeded is a baseline event: it disarms, re-baselines grace/away, and transitions status to Watching` | Mechanical | Same name. |
| 7 | `slow InitSucceeded does not consume grace measured from start's now` | Mechanical | Same name. |
| 8 | `never-seen must not lock, even when away/idle/grace are all satisfied` | Mechanical | Same name. |
| 9 | `a NoFrame blip resets, not merely pauses, the away clock` | Mechanical | Same name; shared helper `assertBadSignalResetsAwayClockWithoutArming` migrated once. |
| 10 | `a DarkFrame blip resets, not merely pauses, the away clock` | Mechanical | Same name; shares the migrated helper with row 9. |
| 11 | `status becomes NoSignal after continuous bad signal reaches NoSignalReportAfterMs` | Mechanical | Same name. |
| 12 | `continuous bad signal reaching ReevaluateAfterMs requests Action.Restart CameraReevaluation when no cooldown applies` | Mechanical | Same name. |
| 13 | `a cooldown-suppressed CameraReevaluation restart stays saturated and fires immediately once the cooldown clears` | Mechanical | Same name. |
| 14 | `a baseline event resets the signal-health clock, preventing an immediate NoSignal on the next bad sample` | Mechanical | Same name. |
| 15 | `dim-light while typing must not lock (regression: fail-closed dim-light lock incident)` | Mechanical | Same name. |
| 16 | `CaptureFailed before any InitSucceeded is a no-op (not a shell-reachable state, but step must stay total)` | Mechanical | Same name. |
| 17 | `SessionLocked transitions status to SessionLocked and suppresses further Sample-driven locking` | Hand-rewritten | Same name; old `Event.SessionLocked` → `Event.Reconcile` with `StepInputs.SessionLocked = true` (RFC semantics-translation guide). |
| 18 | `SessionUnlocked re-baselines grace/away/signal-health and disarms, resuming sampling` | Hand-rewritten | Same name; old `Event.SessionUnlocked` → `Event.Reconcile` with `StepInputs.SessionLocked = false`, exercising the suppressed→unsuppressed edge reset. |
| 19 | `a Sample event is a complete no-op while SessionLocked: no arming, no re-baseline, no Lock` | Hand-rewritten | Same name; `StepInputs.SessionLocked = true` carried on the `Sample` context; asserts the wrapper's while-suppressed no-op, not a per-arm guard. |
| 20 | `Paused transitions status to Paused, stops sampling, but does not disarm or touch the away baseline` | Hand-rewritten | Same name; old `Event.Paused` → `Event.Reconcile` with `StepInputs.Paused = true`. |
| 21 | `Resumed re-baselines grace/away/signal-health and disarms, resuming sampling` | Hand-rewritten | Same name; old `Event.Resumed` → `Event.Reconcile` with `StepInputs.Paused = false`, exercising the suppressed→unsuppressed edge reset. |
| 22 | `InitFailed transitions status to Recovering on the very first pre-success failure, of either classification, without yet requesting a restart` | Mechanical | Same name. |
| 23 | `pause takes precedence over an unlock-driven resume: SessionUnlocked while Paused is a no-op` | **Deleted** | This is the incident's pinned blind spot (0001-core-brain.md's truth table, Motivation's root cause, `Tests.fs:450` per the handoff). It asserted the OLD WRONG behavior on purpose. Superseded by `LevelsTests.fs`'s `` `tracer: pause, session-lock, unlock, resume as pure StepInputs transitions still locks (incident fixed point)` ``, which proves the opposite, correct behavior: the sequence reaches `Watching`, never a permanently suppressed state. Not migrated — deleted outright, per the RFC's explicit instruction to retire this test rather than port its assertion. |
| 24 | `session-event idempotency: SessionLocked delivered twice while already locked is a no-op` | **Deleted — superseded** | Superseded by `LevelsTests.fs`'s `` `identical consecutive StepInputs across a Reconcile trigger no edge and no baseline reset` ``, which pins the same claim precisely as the RFC scopes the level-idempotence property (no edge, no baseline reset — not "no repeated decision"). |
| 25 | `session-event idempotency: Paused delivered twice while already paused is a no-op` | **Deleted — superseded** | Same replacement as row 24: `LevelsTests.fs`'s level-idempotence test covers repeated-identical-`StepInputs` idempotence generically, independent of which flag repeats. |
| 26 | `session-event idempotency: Resumed while already running (not paused) is a no-op` | **Deleted — superseded** | Same replacement as row 24. |
| 27 | `config changed mid-grace applies immediately, without discarding accumulated baseline state` | Mechanical | Same name. |
| 28 | `an 8-hour tick gap followed by NoFace (idle satisfied) locks on the very next sample` | Mechanical | Same name. |
| 29 | `the same 8-hour tick gap followed by NoFrame does not lock` | Mechanical | Same name. |
| 30 | `InitFailed requests Action.Restart CameraWedged once RecoveryFailureThreshold consecutive handle-invalid failures are reached and no cooldown applies` | Mechanical | Same name. |
| 31 | `a cooldown-suppressed InitFailed wedge restart stays saturated and fires immediately once the cooldown clears` | Mechanical | Same name. |
| 32 | `a long run of handleInvalid:false InitFailed events never requests a restart, even once RecoveryFailureThreshold and RecoveryCooldownMs are both long since satisfied (unplugged/absent camera)` | Mechanical | Same name. |
| 33 | `CaptureFailed after InitSucceeded requests Action.Restart CameraWedged unconditionally on its first occurrence, subject only to cooldown` | Mechanical | Same name. |
| 34 | `CaptureFailed while Paused still requests Action.Restart, and the restart preserves the pause` | Hand-rewritten | Same name; old `Event.Paused` → `StepInputs.Paused = true` carried through the `Reconcile` then the `CaptureFailed` call, proving the restart-preserves-pause claim under levels. |
| 35 | `a cooldown-suppressed CaptureFailed returns Action.NoAction (the shell falls back to the NoFrame-to-reevaluation backstop)` | Mechanical | Same name. |
| 36 | `scenario: a cooldown-suppressed CaptureFailed recovers via the NoFrame-to-reevaluation backstop, with no new mechanism` | Mechanical | Same name. |
| 37 | `BetterCameraAvailable before any InitSucceeded is a no-op` | Mechanical | Same name. |
| 38 | `BetterCameraAvailable after InitSucceeded requests Action.Restart CameraUpgrade unconditionally on first occurrence, subject only to cooldown` | Mechanical | Same name. |
| 39 | `a cooldown-suppressed BetterCameraAvailable returns Action.NoAction and leaves UpgradeAt unchanged` | Mechanical | Same name. |
| 40 | `BetterCameraAvailable while Paused still requests Action.Restart CameraUpgrade, and the restart preserves the pause` | Hand-rewritten | Same name; old `Event.Paused` → `StepInputs.Paused = true`, same pattern as row 34. |
| 41 | `baseline snapshot is unarmed, in grace, and all counters are zero` | Mechanical | Same name (property). |
| 42 | `baseline snapshot surfaces exactly the restart stamps passed to start` | Mechanical | Same name (property). |
| 43 | `grace is live, not latched: it expires exactly at GraceMs since start with no intervening events` | Mechanical | Same name (property). |
| 44 | `property 1: no Action.Lock unless an independent armed model says a FaceSeen occurred since the last baseline event` | Mechanical | Same name; shared `runTicks` helper migrated once (idle synced via `ctxForEvent`). |
| 45 | `property 2: no Action.Lock while inputIdleMs is below InputIdleRequiredMs` | Mechanical | Same name; shares migrated `runTicks`. |
| 46 | `property 3: no Action.Lock before GraceMs elapses after the most recent baseline event` | Mechanical | Same name; shares migrated `runTicks`. |
| 47 | `property 7: a continuous run of NoFace shorter than AwayThresholdMs never locks, regardless of armed/grace/idle state` | Mechanical | Same name. |
| 48 | `property 4: NoFrame/DarkFrame sequences alone never produce Action.Lock` | Mechanical | Same name. |
| 49 | `property 6: at most one Action.Restart CameraReevaluation per ReevaluateCooldownMs window` | Mechanical | Same name; `wallTicksGen`/`eventGen` no longer produce the four session/pause events (see Generators.fs row below) — `Event.Reconcile` fills the "mostly no-op" noise role they used to play. |
| 50 | `property 8: Sample events while paused or session-locked never change the armed/away/signal-health baseline and never emit Action.Lock` | **Hand-rewritten** | Renamed `` `property 8: Sample events while suppressed (paused or session-locked) never change the armed/away/signal-health baseline and never emit Action.Lock` ``. The named hotspot: `applyPauseLockModel`, the hand-rolled (isPaused, isSessionLocked) fold over the four legacy events — including the precedence sub-model — is deleted outright and replaced by a `LevelsTick` generator (`SetSessionLocked`/`SetPaused`/`SetLockInhibited`/`SampleTick`) that fires `Event.Reconcile` synchronously at each level transition (mirroring the shell's `Advance(Reconcile)` contract, the RFC's model-fidelity rule) and tracks `StepInputs` directly. The precedence sub-model has no levels-path counterpart: `suppressed inputs = inputs.SessionLocked \|\| inputs.Paused` is a plain, order-independent OR (RFC: "There is no pause-precedence interaction to specify"), so the incident-class precedence bug this property's old form could never have caught is structurally inexpressible in the new model. Per the RFC, this property is further superseded by property 13 once slice 2's verification harness lands — not yet built, so this rewrite is this phase's replacement, not a stand-in for that future one. |
| 51 | `property 5: at most one Action.Restart CameraWedged per RecoveryCooldownMs window` | Mechanical | Same name. |
| 52 | `property 9: a Lock-producing Sample re-emits Action.Lock when immediately repeated with no intervening SessionLocked (no internal latch)` | Mechanical (name/comment updated) | Renamed `` `property 9: a Lock-producing Sample re-emits Action.Lock when immediately repeated with no intervening suppression change (no internal latch)` ``; assertion logic unchanged — property 9 stays intact per the RFC ("no-op" from the level-idempotence property never means "no second `Action.Lock`"); only the no-longer-meaningful "SessionLocked" wording in the name/comment was updated to "suppression change." |
| 53 | `property 12: at most one Action.Restart CameraUpgrade per UpgradeCooldownMs window` | Mechanical | Same name. |

## `PresenceLock.Core.Tests/Generators.fs`

| Old | Disposition | New / reason |
|---|---|---|
| `eventGen` | Updated | Removed `Event.SessionLocked`/`Event.SessionUnlocked`/`Event.Paused`/`Event.Resumed` from the generated alphabet (transitional-only in the levels path; deleted from the `Event` DU in the contract phase). Added `Event.Reconcile` in their place, preserving the "mostly no-op noise case" role the four events used to play for the generic properties that reuse `eventGen` (rows 49, 51, 52, 53 above). |

## `PresenceLock.Tests` (C#) — DONE, contract phase

| Old test | Disposition | Reason |
|---|---|---|
| `ClassifySampleTests.Constructs_a_Sample_event_carrying_the_classified_observation_and_idle_time` (`PolicyBridgeTests.cs:320`) | **Deleted** | Asserted the `Event.Sample` idle payload (`sample.inputIdleMs`) deleted once `InputIdleMs` moved fully out of `Sample` and into `StepInputs` (RFC "Core contract changes": "`InputIdleMs` moves out of the `Sample` payload and into `StepInputs`"). Named in the RFC's slice-1 paragraph as the C# suite's exactly-one casualty. `PolicyBridge.ClassifySample` lost its `inputIdleMs` parameter to match (`ClassifySample(haveFrame, dark, present)`); `Program.cs`'s `SampleAsync` stops passing it — idle now flows only through `Advance`'s input assembly. |

New tests added this phase (not migrations — new coverage for the contract cutover):

| New test class | Reason |
|---|---|
| `StepInputsConstructionTests` (`PolicyBridgeTests.cs`) | RFC "Construction discipline": constructs `Core.StepInputs` via named arguments with each bool flipped independently off a known baseline, asserting every field of the result — the pinned safety net for the one-helper/named-arguments rule, landed *before* the mechanical shell cutover per the RFC's ordering. |
| `IsSuppressedStatusTests` (`PolicyBridgeTests.cs`) | Covers the new `PolicyBridge.IsSuppressedStatus` predicate (`Paused`/`SessionLocked`) that `Advance`'s suppression-transition rule uses to detect the before/after status pair's edge — extracted to `PolicyBridge` to match the existing `IsAcquiringOrRecovering`/`ExpectsSampling` precedent (pure `Status` predicates, independently tested) rather than left as a private helper in `Program.cs`. |

`Program.cs`/`PolicyBridge.cs` are cut over onto the renamed `Policy.start`/`Policy.step`
and `StepInputs`/`StepContext`: new `sessionLocked`/`paused` shell-owned mirror fields; one
`BuildStepInputs()` construction site (named arguments, `LockInhibited` hard-coded `false`
until slice 5) used by both `Advance` and the startup `Policy.start` call; `Advance` opens
with a `Debug.Assert` on the UI `SynchronizationContext` and implements the Advance-internal
suppression-transition rule (filter/freshness resets + unconditional sample-timer restart +
`KickAcquisitionIfNeeded` on a suppressed→unsuppressed transition; sample-timer stop on the
reverse); the five session/pause `Advance(Event…)` call sites collapse to mirror-update +
`Advance(Event.Reconcile)`; the constructor's pause consume-and-clear now feeds `Policy.start`
directly (the `Event.Paused` refeed injection is deleted); `RestartProcess` reads the `paused`
mirror instead of round-tripping through `Policy.status`.

## Verification

MIGRATE phase:

- `PresenceLock.Core.Tests` suite: 80/80 passing (`dotnet test PresenceLock.Core.Tests/PresenceLock.Core.Tests.fsproj -c Release`, Linux SDK container).
- Zero code-level references remain in `Tests.fs`/`Generators.fs` to `Policy.step(`, `Policy.start(`, `Event.SessionLocked`, `Event.SessionUnlocked`, `Event.Paused` (as an event), `Event.Resumed`, `.IsPaused`, `.IsSessionLocked` — the only textual hits are inside `//`/`///` comments documenting the old→new mapping at the hand-rewritten call sites (rows 17–21, 34, 40, 50 above).

CONTRACT phase (this update):

- `PresenceLock.Core.Tests` suite (post-rename, legacy shape deleted): 80/80 passing, unchanged tally — the rename/deletion touched call sites and helpers, not test count.
- `PresenceLock.csproj` (shell) cross-compile check, Linux SDK container: build succeeded, 0 warnings, 0 errors.
- `PresenceLock.Tests` suite (Windows SDK container, `dotnet test PresenceLock.Tests\PresenceLock.Tests.csproj -c Release`): 80/80 passing (1 deleted — `ClassifySampleTests`; 6 added — `StepInputsConstructionTests` ×4, `IsSuppressedStatusTests` ×2 — net +5 over the pre-phase count of 75).
- Zero code-level references remain anywhere in `PresenceLock.Core`, `PresenceLock.Core.Tests`, `Program.cs`, `PolicyBridge.cs`, or `PresenceLock.Tests` to `Event.SessionLocked`/`Event.SessionUnlocked`/`Event.Paused`/`Event.Resumed` (as constructors), `State.IsPaused`/`.IsSessionLocked`, or `Policy.stepLevels`/`Policy.startLevels` — the only textual hits left anywhere are inside comments documenting the old→new mapping (this file's historical MIGRATE-phase table, and a handful of "old `Event.X` → levels `StepInputs.Y`" translation notes in `Tests.fs`).
