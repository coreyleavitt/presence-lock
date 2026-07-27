namespace PresenceLock.Core

/// Scaffold placeholder module (rfc-core-brain.md slice 1): proves the F#/xunit/FsCheck
/// toolchain restores and runs inside the build container before any real Policy logic is
/// written. Replaced by the real Policy module in slice 2.
module Library =

    let reverseTwice (xs: int list) : int list =
        xs |> List.rev |> List.rev
