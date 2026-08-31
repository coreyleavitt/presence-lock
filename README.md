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
decision record are preserved in [`0001-core-brain.md`](0001-core-brain.md).

## Build & test

The host needs no .NET SDK; every build step runs in the least-privileged place
that can run it:

| Step | Where | Why |
|---|---|---|
| Core tests, publish | Linux SDK container (podman) | fast; the WinExe cross-compiles via `EnableWindowsTargeting` |
| Shell tests | Windows SDK container (docker) | `net10.0-windows` tests cannot execute on Linux |
| MSIX pack | `makemsix` in a Linux container | Microsoft's open-source cross-platform packer, built from a pinned commit by `build-makemsix.ps1` |
| Signing | host `signtool` only | the private key never enters any container |

```powershell
# One-time: build makemsix into tools/linux (pinned commit, containerized build)
./build-makemsix.ps1

# Compile, test, pack, and sign the MSIX
./pack.ps1

# Fast inner loop: core test suite in a Linux SDK container via podman (seconds)
./test.ps1
```

Both test suites are hard gates on packaging. `obj/` and `bin/` are partitioned
per build OS (`Directory.Build.props`) so the Linux and Windows toolchains can
share the source tree without clobbering each other's intermediates.

To upgrade an installed build: stop the running instance, then
`Add-AppxPackage out\PresenceLock.msix`. Configuration is read from
`%LOCALAPPDATA%\PresenceLock\presencelock.json`; the log lives beside it.

## License

Apache 2.0 — see [LICENSE](LICENSE).
