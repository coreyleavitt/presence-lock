# PresenceLock

A face-presence auto-lock utility for Windows. A webcam watches for your face; when you step away and the machine goes idle, it locks the session — a camera-based take on Windows Dynamic Lock, built so the lock decision is small, pure, and fully testable.

## Architecture

PresenceLock is a **functional core / imperative shell** design: a pure F# core
(`PresenceLock.Core`) makes every decision — when to lock, when not to, when to
recover — as a function of explicit state, config, time, and events, and a C# WinForms
shell owns everything impure (camera, face detection, session lock, process
lifecycle) and executes what the core decides. The core has no Windows dependency,
so the safety-critical logic is exhaustively unit- and property-testable in any
container.

The design details and the invariants that must hold — clock-domain types, fail-open
behavior, presence debouncing, and the camera-lifecycle rules — live in
[`ARCHITECTURE.md`](ARCHITECTURE.md).

## Project layout

| Project | Language | Purpose |
|---|---|---|
| `PresenceLock.Core` | F# (`net10.0`) | Pure decision core: policy, state, presence filter, types |
| `PresenceLock.Core.Tests` | F# (xunit + FsCheck) | Scenario and property tests for the core |
| `PresenceLock` | C# (WinExe) | WinForms tray shell: camera, face detection, session lock, packaging |
| `PresenceLock.Tests` | C# (xunit) | Shell-side tests for the pure helpers in `PolicyBridge.cs` |

The design reference is [`ARCHITECTURE.md`](ARCHITECTURE.md); the original RFC and
decision record are preserved in [`rfc-core-brain.md`](rfc-core-brain.md).

## Build & test

The host needs no .NET SDK; all `dotnet` and packaging commands run inside the .NET SDK
Windows container. The core test suite is a hard gate on packaging.

```powershell
# Compile, test, and pack a signed MSIX (see pack.ps1 for the container pattern)
./pack.ps1

# Fast inner loop: core test suite in a Linux SDK container via podman (seconds)
./test.ps1
```

The core targets plain `net10.0`, so its tests run natively in a Linux container;
the Windows-targeted projects cross-compile there too (`EnableWindowsTargeting`),
but the shell test suite still executes in the Windows container as part of the
`pack.ps1` release gate.

`pack.ps1` compiles and tests in `mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022`,
then runs `makeappx`/`signtool` on the host (Server Core containers lack the AppX COM surface).

To upgrade an installed build: stop the running instance, then
`Add-AppxPackage out\PresenceLock.msix`. Configuration is read from
`%LOCALAPPDATA%\PresenceLock\presencelock.json`; the log lives beside it.

## License

Apache 2.0 — see [LICENSE](LICENSE).
