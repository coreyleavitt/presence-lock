# Architecture

PresenceLock is a **functional core / imperative shell** design split across two
languages. This document describes the current state of the design and the invariants
that must hold; the original design process, including the full decision record, is
preserved in [`rfc-core-brain.md`](rfc-core-brain.md).

## The core/shell boundary

- **`PresenceLock.Core` (F#)** — the entire decision surface. A pure function
  `step : PolicyConfig * State * time * Event -> State * Action` maps the current
  state and an observation to the next state and at most one action (`Lock` or
  `Restart`). No clocks, no I/O, no hidden mutation — every notion of "now" is a
  caller-supplied parameter. The core targets plain `net10.0` with no Windows
  dependency, so its tests run anywhere, including inside a build container, without
  a camera or a real session to lock.
- **The C# shell** (`Program.cs`, `SettingsForm.cs`, `PolicyBridge.cs`) — a WinForms
  tray app that owns everything impure: the camera (`MediaCapture`), face detection,
  the Win32 session lock, process lifecycle, and persistence. It samples the camera,
  classifies each observation into an `Event`, hands it to the core through a single
  `Advance(Event)` chokepoint, and executes whatever `Action` comes back.

The shell decides *nothing*; the core touches *nothing*. That boundary is what makes
the safety-critical logic — when to lock, when not to, when to recover — exhaustively
unit- and property-testable.

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
- **Resume always restarts sampling.** Every path that leaves `Paused`/`SessionLocked`
  (`TogglePause`'s resume branch, `OnSessionSwitch`'s unlock branch) restarts the sample
  timer unconditionally, whether or not a camera is currently attached. This closes an
  incident (2026-08-12) where a resume gated the restart on `reader is not null`: a
  camera that died — inside the restart cooldown, while the process was paused — left
  the reader null and the sample timer stopped with no path back. The dead-camera
  branch of `SampleAsync` exists precisely for this state: with `reader` null it still
  emits `NoFrame`, which drives the ordinary no-signal → re-evaluation → restart
  recovery — resuming sampling is what lets that branch run at all.
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
