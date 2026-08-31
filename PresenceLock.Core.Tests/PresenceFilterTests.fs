module PresenceLock.Core.Tests.PresenceFilterTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open PresenceLock.Core
open PresenceLock.Core.Tests.Generators

/// 0001-core-brain.handoff.md, "Burn-in incident 2026-07-28": IoU is the sole geometry
/// primitive `PresenceFilter.step`'s spatial-coherence gate is built on, so its own math is
/// verified independently before any time-based stability behavior is tested against it.
[<Fact>]
let ``a box compared with itself has IoU 1`` () =
    let box = { X = 0.2; Y = 0.3; W = 0.25; H = 0.4 }
    Assert.Equal(1.0, PresenceFilter.iou (box, box), 10)

[<Fact>]
let ``disjoint boxes have IoU 0`` () =
    let a = { X = 0.0; Y = 0.0; W = 0.2; H = 0.2 }
    let b = { X = 0.5; Y = 0.5; W = 0.2; H = 0.2 }
    Assert.Equal(0.0, PresenceFilter.iou (a, b), 10)

let private ms (n: int64) = MonotonicMs n

/// 0001-core-brain.md bug-fix note (false-lock window): the pinned redesign — presence is
/// stable once one unbroken coherent run has spanned `MinCoherentMs` of monotonic time,
/// independent of how many samples arrived along the way.
[<Fact>]
let ``presence is not stable until a coherent run has spanned MinCoherentMs`` () =
    let box = { X = 0.4; Y = 0.3; W = 0.2; H = 0.3 }
    let r0 = PresenceFilter.step (FilterConfig.Default, PresenceFilter.initial, ms 0L, Nullable(box))
    let r1 = PresenceFilter.step (FilterConfig.Default, r0.State, ms 400L, Nullable(box))
    let r2 = PresenceFilter.step (FilterConfig.Default, r1.State, ms 999L, Nullable(box))
    let r3 = PresenceFilter.step (FilterConfig.Default, r2.State, ms 1000L, Nullable(box))
    Assert.False(r0.StablePresence)
    Assert.False(r1.StablePresence)
    Assert.False(r2.StablePresence)
    Assert.True(r3.StablePresence)

/// The same wall-clock scenario sampled at a much finer cadence reaches the identical
/// stability outcome at the identical wall-clock instant — proof the redesign fixed the
/// frame-count coupling (many more samples arrive by t=1000ms, but stability still flips
/// at exactly t=1000ms, not earlier).
[<Fact>]
let ``stabilization time is independent of sample cadence`` () =
    let box = { X = 0.4; Y = 0.3; W = 0.2; H = 0.3 }
    let runAt (times: int64 list) =
        times
        |> List.fold
            (fun state t -> (PresenceFilter.step (FilterConfig.Default, state, ms t, Nullable(box))).State)
            PresenceFilter.initial
        |> fun finalState -> PresenceFilter.step (FilterConfig.Default, finalState, ms 1000L, Nullable(box))

    // Coarse cadence (500ms) vs fine cadence (100ms), both landing exactly on t=1000ms.
    let coarse = runAt [ 0L; 500L ]
    let fine = runAt [ 0L; 100L; 200L; 300L; 400L; 500L; 600L; 700L; 800L; 900L ]
    Assert.True(coarse.StablePresence)
    Assert.True(fine.StablePresence)

/// After a gap (no detection), the run is fully broken — a fresh detection starts an
/// entirely new run, and stability requires `MinCoherentMs` to re-elapse from that fresh
/// start, never inheriting elapsed time from before the gap.
[<Fact>]
let ``a gap resets the run and stability requires MinCoherentMs to re-elapse from the fresh start`` () =
    let box = { X = 0.4; Y = 0.3; W = 0.2; H = 0.3 }
    let r0 = PresenceFilter.step (FilterConfig.Default, PresenceFilter.initial, ms 0L, Nullable(box))
    let r1 = PresenceFilter.step (FilterConfig.Default, r0.State, ms 1000L, Nullable(box))
    Assert.True(r1.StablePresence)

    let gap = PresenceFilter.step (FilterConfig.Default, r1.State, ms 1200L, Nullable())
    Assert.False(gap.StablePresence)

    // Fresh detections after the gap: not stable again until another full MinCoherentMs
    // has elapsed from the post-gap restart (t=1200), not from t=0.
    let a1 = PresenceFilter.step (FilterConfig.Default, gap.State, ms 1300L, Nullable(box))
    let a2 = PresenceFilter.step (FilterConfig.Default, a1.State, ms 2199L, Nullable(box))
    Assert.False(a1.StablePresence)
    Assert.False(a2.StablePresence)

    let a3 = PresenceFilter.step (FilterConfig.Default, a2.State, ms 2300L, Nullable(box))
    Assert.True(a3.StablePresence)

/// Hysteresis (Schmitt trigger): once a run is stable, an IoU that falls in the "grey
/// band" between `MaintainIoU` and `AcquireIoU` still counts as coherent and keeps the run
/// alive — established presence is sticky against IoU jitter (a shifting auto-framing crop,
/// a slight head turn) that would have failed the stricter acquisition bar.
[<Fact>]
let ``a grey-band IoU keeps an already-stable run alive`` () =
    // boxA/boxB are both 0.4x0.4, offset in X so IoU(boxA, boxB) = 0.2 exactly --
    // strictly between MaintainIoU (0.15) and AcquireIoU (0.3).
    let boxA = { X = 0.0; Y = 0.0; W = 0.4; H = 0.4 }
    let boxB = { X = 0.26667; Y = 0.0; W = 0.4; H = 0.4 }
    let greyBandIoU = PresenceFilter.iou (boxA, boxB)
    Assert.True(greyBandIoU > FilterConfig.Default.MaintainIoU && greyBandIoU < FilterConfig.Default.AcquireIoU)

    let r0 = PresenceFilter.step (FilterConfig.Default, PresenceFilter.initial, ms 0L, Nullable(boxA))
    let r1 = PresenceFilter.step (FilterConfig.Default, r0.State, ms 1000L, Nullable(boxA))
    Assert.True(r1.StablePresence) // now stable; WasStable = true carries into the next step

    let r2 = PresenceFilter.step (FilterConfig.Default, r1.State, ms 1500L, Nullable(boxB))
    Assert.True(r2.StablePresence) // grey-band IoU against a stable run stays coherent

/// The same grey-band IoU never lets an *unstable* run start or build toward stability --
/// the stricter `AcquireIoU` bar applies until the run has actually achieved stability once.
[<Fact>]
let ``a grey-band IoU never builds an unstable run toward stability`` () =
    let boxA = { X = 0.0; Y = 0.0; W = 0.4; H = 0.4 }
    let boxB = { X = 0.26667; Y = 0.0; W = 0.4; H = 0.4 }

    let step state t box = PresenceFilter.step (FilterConfig.Default, state, ms t, Nullable(box))
    let r0 = step PresenceFilter.initial 0L boxA
    let r1 = step r0.State 500L boxB
    let r2 = step r1.State 1000L boxA
    let r3 = step r2.State 1500L boxB
    // Total elapsed span is 1500ms (> MinCoherentMs), but every consecutive pair is
    // grey-band-only coherence against a never-yet-stable run, so it must never stabilize.
    Assert.False(r0.StablePresence)
    Assert.False(r1.StablePresence)
    Assert.False(r2.StablePresence)
    Assert.False(r3.StablePresence)

/// 0001-core-brain.handoff.md, "Burn-in incident 2026-07-28": the exact shape of the original
/// diagnosed failure -- FaceSeen/NoFace alternating every sample -- reproduced against the
/// time-based filter across a realistic 500ms sampling cadence.
[<Fact>]
let ``regression: burn-in incident's FaceSeen-NoFace flicker pattern never reports stable presence`` () =
    let box = { X = 0.35; Y = 0.25; W = 0.3; H = 0.35 } // consistent framing; the flicker is temporal only
    let mutable state = PresenceFilter.initial
    let mutable everStable = false
    for i in 0 .. 39 do
        let detection = if i % 2 = 0 then Nullable(box) else Nullable()
        let r = PresenceFilter.step (FilterConfig.Default, state, ms (int64 i * 500L), detection)
        state <- r.State
        everStable <- everStable || r.StablePresence
    Assert.False(everStable)

/// A seated user: stabilizes once a coherent run has spanned MinCoherentMs, stays stable,
/// and a single detector hiccup (one missed frame at a realistic 500ms cadence, not the user
/// actually leaving) costs no more than another MinCoherentMs to re-stabilize -- no
/// cumulative or growing penalty across repeated dropouts.
[<Fact>]
let ``a seated user's coherent presence stabilizes within MinCoherentMs and recovers fast after a single-frame dropout`` () =
    let box = { X = 0.4; Y = 0.3; W = 0.22; H = 0.3 }
    let step state t detection = PresenceFilter.step (FilterConfig.Default, state, ms t, detection)

    let r1 = step PresenceFilter.initial 0L (Nullable(box))
    let r2 = step r1.State 500L (Nullable(box))
    let r3 = step r2.State 1000L (Nullable(box))
    Assert.False(r1.StablePresence)
    Assert.False(r2.StablePresence)
    Assert.True(r3.StablePresence)

    let r4 = step r3.State 1500L (Nullable(box))
    Assert.True(r4.StablePresence)

    let dropout = step r4.State 2000L (Nullable())
    Assert.False(dropout.StablePresence)

    let a1 = step dropout.State 2500L (Nullable(box))
    let a2 = step a1.State 3499L (Nullable(box))
    Assert.False(a1.StablePresence)
    Assert.False(a2.StablePresence)

    let a3 = step a2.State 3500L (Nullable(box))
    Assert.True(a3.StablePresence)

[<Properties(Arbitrary = [| typeof<Generators> |])>]
module PropertyTests =

    /// property: IoU is symmetric in its two arguments, for any pair of normalized boxes.
    [<Property>]
    let ``IoU is symmetric`` (a: FaceBox) (b: FaceBox) =
        PresenceFilter.iou (a, b) = PresenceFilter.iou (b, a)

    /// A strictly increasing sequence of sample times sharing one perfectly-coherent box
    /// (IoU 1 against every predecessor), with an arbitrary and irregular cadence between
    /// them -- exactly the "many samples of varying spacing" shape a real sampler produces.
    let private irregularCoherentRunGen: Gen<int64 list> =
        gen {
            let! start = Gen.choose (0, 100_000)
            let! deltaCount = Gen.choose (0, 20)
            let! deltas = Gen.listOfLength deltaCount (Gen.choose (1, 2000))
            return deltas |> List.scan (+) start |> List.map int64
        }

    /// property 1: stability is a pure function of the coherent run's time-span, never of
    /// how many samples arrived along the way -- for any strictly increasing, irregular
    /// sequence of sample times sharing one coherent box, `StablePresence` at every step
    /// equals `(t - runStart) >= MinCoherentMs`, regardless of the sequence's length or
    /// spacing (the exact frame-count coupling this redesign eliminates).
    [<Property>]
    let ``stability at any sample time equals whether MinCoherentMs has elapsed since the run started`` (box: FaceBox) =
        Prop.forAll (Arb.fromGen irregularCoherentRunGen) (fun times ->
            match times with
            | [] -> true
            | start :: _ ->
                times
                |> List.fold
                    (fun (state, results) t ->
                        let r = PresenceFilter.step (FilterConfig.Default, state, ms t, Nullable(box))
                        (r.State, (t, r.StablePresence) :: results))
                    (PresenceFilter.initial, [])
                |> snd
                |> List.forall (fun (t, stable) -> stable = (t - start >= FilterConfig.Default.MinCoherentMs)))

    /// property 2: a run broken by a gap is never stable before `MinCoherentMs` has
    /// re-elapsed from the fresh post-gap start -- for any gap time and any restart time at
    /// or after it, every observation strictly before `restart + MinCoherentMs` is unstable.
    [<Property>]
    let ``a run broken by a gap is never stable before MinCoherentMs has re-elapsed`` (box: FaceBox) (gapAt: NonNegativeInt) (restartDelay: NonNegativeInt) (checkDelay: NonNegativeInt) =
        let restartAt = int64 gapAt.Get + int64 restartDelay.Get
        let checkAt = restartAt + int64 checkDelay.Get
        (checkAt - restartAt < FilterConfig.Default.MinCoherentMs)
        ==> lazy
            (let preGap = PresenceFilter.step (FilterConfig.Default, PresenceFilter.initial, ms 0L, Nullable(box))
             let gap = PresenceFilter.step (FilterConfig.Default, preGap.State, ms (int64 gapAt.Get), Nullable())
             let restart = PresenceFilter.step (FilterConfig.Default, gap.State, ms restartAt, Nullable(box))
             let check =
                 if checkAt = restartAt then restart
                 else PresenceFilter.step (FilterConfig.Default, restart.State, ms checkAt, Nullable(box))
             not check.StablePresence)
