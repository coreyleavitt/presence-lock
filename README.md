# PresenceLock

A face-presence auto-lock utility for Windows. A webcam watches for your face; when you step away and the machine goes idle, it locks the session — a camera-based take on Windows Dynamic Lock, built so the lock decision is small, pure, and fully testable.

## Architecture

PresenceLock is a **functional core / imperative shell** design split across two languages:

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
  classifies each observation into an `Event`, hands it to the core, and executes
  whatever `Action` comes back.

The shell decides *nothing*; the core touches *nothing*. That boundary is what makes
the safety-critical logic — when to lock, when not to, when to recover — exhaustively
unit- and property-testable.

## Design decisions worth calling out

- **Two clock domains, made unforgeable by the type system.** Timing uses monotonic
  milliseconds (`Environment.TickCount64`) for grace/idle/away windows, and wall-clock
  epoch milliseconds only for restart-cooldown gates that must survive a reboot. They
  are distinct CLR struct types (`MonotonicMs` / `WallClockMs`), so passing one where
  the other is expected is a **compile error in both F# and C#**, not a latent unit bug
  — the two values are otherwise adjacent, swappable `int64`s constructed back-to-back
  on every event.
- **Fail-closed by default.** Dim light, blocked lens, or dropped frames report
  `NoSignal` and *never* lock — a false lock is worse than a missed one. If the
  monotonic clock ever goes backwards, every elapsed-time comparison fails closed
  (never lock, never restart) rather than throwing or wrapping.
- **Initialize the camera once per process; never re-initialize in-process.** Tearing
  down and re-initializing the capture pipeline wedges the Windows Camera Frame Server
  service machine-wide (`E_HANDLE`, persistent until an elevated service restart). This
  invariant is *structurally enforced*: the in-process re-init paths are gone, and any
  recovery that needs a fresh pipeline is an `Action.Restart` decided by the core and
  executed as a graceful process self-restart by the shell. The camera deliberately
  stays live (LED on) across a session lock.
- **Sleep/hibernate correctness.** `TickCount64` includes time spent suspended and
  shares its tick base with `GetLastInputInfo`, so idle and away windows elapse
  consistently across a suspend without special-casing wake.

## Project layout

| Project | Language | Purpose |
|---|---|---|
| `PresenceLock.Core` | F# (`net10.0`) | Pure decision core: policy, state, presence filter, types |
| `PresenceLock.Core.Tests` | F# (xunit + FsCheck) | Scenario and property tests for the core |
| `PresenceLock` | C# (WinExe) | WinForms tray shell: camera, face detection, session lock, packaging |
| `PresenceLock.Tests` | C# (xunit) | Shell-side tests for the pure helpers in `PolicyBridge.cs` |

Design notes live in [`rfc-core-brain.md`](rfc-core-brain.md).

## Build & test

The host needs no .NET SDK; all `dotnet` and packaging commands run inside the .NET SDK
Windows container. The core test suite is a hard gate on packaging.

```powershell
# Compile, test, and pack a signed MSIX (see pack.ps1 for the container pattern)
./pack.ps1

# Core tests only (run in the SDK Windows container)
dotnet test PresenceLock.Core.Tests/PresenceLock.Core.Tests.fsproj -c Release
```

`pack.ps1` compiles and tests in `mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022`,
then runs `makeappx`/`signtool` on the host (Server Core containers lack the AppX COM surface).

## License

Apache 2.0 — see [LICENSE](LICENSE).
