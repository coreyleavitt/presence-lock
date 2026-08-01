module PresenceLock.Core.Tests.Generators

open System
open FsCheck
open PresenceLock.Core

/// Real `Environment.TickCount64` is always >= 0 (it wraps a `ulong` tick count as a signed
/// 64-bit value and is only ever read forward from process/boot start), so the generator stays
/// non-negative — there's no realistic call site that could hand `step` a negative `MonotonicMs`.
let monotonicMsGen : Gen<MonotonicMs> =
    Gen.choose (0, Int32.MaxValue) |> Gen.map (fun ms -> MonotonicMs(int64 ms))

/// Real `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` is likewise always non-negative for
/// any date after 1970.
let wallClockMsGen : Gen<WallClockMs> =
    Gen.choose (0, Int32.MaxValue) |> Gen.map (fun ms -> WallClockMs(int64 ms))

/// A `PolicyConfig` drawn from the space the shell's load-time validation actually allows
/// through to `step`/`snapshot` (rfc-core-brain.md, "Config: file schema, mapping,
/// validation"): every `*Ms` field positive, `RecoveryFailureThreshold >= 1`, and
/// `ReevaluateAfterMs >= NoSignalReportAfterMs` so the no-signal status is always reachable
/// before a re-evaluation restart. Out-of-range/invalid configs are a shell-side validation
/// concern (defaulted as a unit before `Policy.start` is ever called), not something Core's
/// properties need to tolerate.
let policyConfigGen : Gen<PolicyConfig> =
    gen {
        let! awayThresholdMs = Gen.choose (1, 60_000)
        let! inputIdleRequiredMs = Gen.choose (1, 60_000)
        let! graceMs = Gen.choose (1, 60_000)
        let! noSignalReportAfterMs = Gen.choose (1, 60_000)
        let! reevaluateExtraMs = Gen.choose (0, 60_000)
        let! reevaluateCooldownMs = Gen.choose (1, 3_600_000)
        let! recoveryFailureThreshold = Gen.choose (1, 10)
        let! recoveryCooldownMs = Gen.choose (1, 3_600_000)
        let! upgradeCooldownMs = Gen.choose (1, 3_600_000)
        return
            { AwayThresholdMs = int64 awayThresholdMs
              InputIdleRequiredMs = int64 inputIdleRequiredMs
              GraceMs = int64 graceMs
              NoSignalReportAfterMs = int64 noSignalReportAfterMs
              ReevaluateAfterMs = int64 noSignalReportAfterMs + int64 reevaluateExtraMs
              ReevaluateCooldownMs = int64 reevaluateCooldownMs
              RecoveryFailureThreshold = recoveryFailureThreshold
              RecoveryCooldownMs = int64 recoveryCooldownMs
              UpgradeCooldownMs = int64 upgradeCooldownMs }
    }

let observationGen : Gen<Observation> =
    Gen.elements
        [ Observation.FaceSeen
          Observation.NoFace
          Observation.NoFrame
          Observation.DarkFrame ]

let eventGen : Gen<Event> =
    Gen.oneof
        [ gen {
            let! observation = observationGen
            let! inputIdleMs = Gen.choose (0, 3_600_000)
            return Event.Sample(observation, int64 inputIdleMs)
          }
          Gen.constant Event.InitSucceeded
          Gen.map Event.InitFailed Arb.generate<bool>
          Gen.constant Event.CaptureFailed
          Gen.constant Event.SessionLocked
          Gen.constant Event.SessionUnlocked
          Gen.constant Event.Paused
          Gen.constant Event.Resumed
          Gen.constant Event.BetterCameraAvailable ]

/// A normalized face box within the unit frame (rfc-core-brain.handoff.md, "Burn-in incident
/// 2026-07-28"): width/height are strictly positive — a real detector never reports a
/// zero-area box — and the box is fully contained in the unit square, matching how the shell
/// normalizes a detection by the gray frame's `PixelWidth`/`PixelHeight` before calling
/// `PresenceFilter.step`.
let faceBoxGen: Gen<FaceBox> =
    gen {
        let! w = Gen.choose (1, 100) |> Gen.map (fun i -> float i / 100.0)
        let! h = Gen.choose (1, 100) |> Gen.map (fun i -> float i / 100.0)
        let! x = Gen.choose (0, 100) |> Gen.map (fun i -> float i / 100.0 * (1.0 - w))
        let! y = Gen.choose (0, 100) |> Gen.map (fun i -> float i / 100.0 * (1.0 - h))
        return { X = x; Y = y; W = w; H = h }
    }

/// A random wall-clock stamp, or none — the two cases every real `RestartStamps` field takes:
/// never restarted for this reason (null) vs. restarted at some point in the epoch-ms past.
let private nullableWallClockGen : Gen<Nullable<int64>> =
    Gen.frequency
        [ 1, Gen.constant (Nullable())
          3, Gen.choose (0, Int32.MaxValue) |> Gen.map (fun ms -> Nullable(int64 ms)) ]

/// Randomized initial `RestartStamps` — including "a restart just happened moments ago" and
/// "no restart of this reason has ever happened," independently per field — the shape later
/// cooldown properties (5 and 6, landing in slices 4 and 6) quantify over. Cross-restart
/// cooldown continuity is the entire point of the wall-clock stamp design, so slice 2 seeds
/// this generator even though nothing yet consumes it beyond pass-through (rfc-core-brain.md
/// finding R2-19).
let restartStampsGen : Gen<RestartStamps> =
    gen {
        let! wedgeAt = nullableWallClockGen
        let! reevalAt = nullableWallClockGen
        let! upgradeAt = nullableWallClockGen
        return { WedgeAt = wedgeAt; ReevalAt = reevalAt; UpgradeAt = upgradeAt }
    }

/// Registered with FsCheck.Xunit via `[<Properties(Arbitrary = [| typeof<Generators> |])>]` on
/// the property-test module below — reused by every later slice's `[<Property>]`s.
type Generators =
    static member MonotonicMs() = Arb.fromGen monotonicMsGen
    static member WallClockMs() = Arb.fromGen wallClockMsGen
    static member PolicyConfig() = Arb.fromGen policyConfigGen
    static member Event() = Arb.fromGen eventGen
    static member RestartStamps() = Arb.fromGen restartStampsGen
    static member FaceBox() = Arb.fromGen faceBoxGen
