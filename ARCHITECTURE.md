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
- **Presence is debounced in time and space.** Face detectors flicker false positives
  on empty scenes (threshold-marginal luminance patterns, aggravated by auto-framing
  camera pipelines). A raw detection only counts as presence after several consecutive
  frames whose bounding boxes overlap (`PresenceFilter`, pure F#, property-tested):
  real faces produce spatially coherent boxes; phantoms wander. Absence needs no such
  filter — the away threshold already integrates it over seconds.
- **Initialize the camera once per process; never re-initialize in-process.** Tearing
  down and re-initializing the capture pipeline wedges the Windows Camera Frame Server
  service machine-wide (`E_HANDLE`, persistent until an elevated service restart). This
  invariant is *structurally enforced*: the in-process re-init paths are gone, and any
  recovery that needs a fresh pipeline is an `Action.Restart` decided by the core and
  executed as a graceful process self-restart by the shell. Do not add a teardown/retry
  loop, however tempting — first-init in a fresh process is the only reliable
  re-initialization on affected hardware.
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
