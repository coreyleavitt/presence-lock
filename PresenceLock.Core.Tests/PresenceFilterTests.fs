module PresenceLock.Core.Tests.PresenceFilterTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open PresenceLock.Core
open PresenceLock.Core.Tests.Generators

/// rfc-core-brain.handoff.md, "Burn-in incident 2026-07-28": IoU is the sole geometry
/// primitive `PresenceFilter.step`'s spatial-coherence gate is built on, so its own math is
/// verified independently before any run-length behavior is tested against it.
[<Fact>]
let ``a box compared with itself has IoU 1`` () =
    let box = { X = 0.2; Y = 0.3; W = 0.25; H = 0.4 }
    Assert.Equal(1.0, PresenceFilter.iou (box, box), 10)

[<Fact>]
let ``disjoint boxes have IoU 0`` () =
    let a = { X = 0.0; Y = 0.0; W = 0.2; H = 0.2 }
    let b = { X = 0.5; Y = 0.5; W = 0.2; H = 0.2 }
    Assert.Equal(0.0, PresenceFilter.iou (a, b), 10)

/// rfc-core-brain.handoff.md, "Burn-in incident 2026-07-28": the pinned fix — 3 consecutive
/// spatially-coherent detections before presence counts as stable.
[<Fact>]
let ``K consecutive identical-box detections report stable presence`` () =
    let box = { X = 0.4; Y = 0.3; W = 0.2; H = 0.3 }
    let r1 = PresenceFilter.step (FilterConfig.Default, PresenceFilter.initial, Nullable(box))
    let r2 = PresenceFilter.step (FilterConfig.Default, r1.State, Nullable(box))
    let r3 = PresenceFilter.step (FilterConfig.Default, r2.State, Nullable(box))
    Assert.False(r1.StablePresence)
    Assert.False(r2.StablePresence)
    Assert.True(r3.StablePresence)

[<Fact>]
let ``after a gap, only K new consecutive coherent detections restore stable presence`` () =
    let box = { X = 0.4; Y = 0.3; W = 0.2; H = 0.3 }
    let s0 = PresenceFilter.initial
    let r1 = PresenceFilter.step (FilterConfig.Default, s0, Nullable(box))
    let r2 = PresenceFilter.step (FilterConfig.Default, r1.State, Nullable(box))
    let r3 = PresenceFilter.step (FilterConfig.Default, r2.State, Nullable(box))
    Assert.True(r3.StablePresence)

    // A gap (no detection) reports absence instantly and forgets the prior run entirely.
    let gap = PresenceFilter.step (FilterConfig.Default, r3.State, Nullable())
    Assert.False(gap.StablePresence)

    // K-1 fresh detections after the gap are not yet enough...
    let a1 = PresenceFilter.step (FilterConfig.Default, gap.State, Nullable(box))
    let a2 = PresenceFilter.step (FilterConfig.Default, a1.State, Nullable(box))
    Assert.False(a1.StablePresence)
    Assert.False(a2.StablePresence)

    // ...but the Kth fresh detection restores stability, exactly as if starting cold.
    let a3 = PresenceFilter.step (FilterConfig.Default, a2.State, Nullable(box))
    Assert.True(a3.StablePresence)

/// rfc-core-brain.handoff.md, "Burn-in incident 2026-07-28": the exact shape of the diagnosed
/// failure — FaceSeen/NoFace alternating every sample, ~20s of the log's observed window —
/// reproduced directly against the filter rather than only against the general property below.
[<Fact>]
let ``regression: burn-in incident's FaceSeen-NoFace flicker pattern never reports stable presence`` () =
    let box = { X = 0.35; Y = 0.25; W = 0.3; H = 0.35 } // consistent framing; the flicker is temporal only
    let mutable state = PresenceFilter.initial
    let mutable everStable = false
    for i in 0 .. 39 do
        let detection = if i % 2 = 0 then Nullable(box) else Nullable()
        let r = PresenceFilter.step (FilterConfig.Default, state, detection)
        state <- r.State
        everStable <- everStable || r.StablePresence
    Assert.False(everStable)

/// A seated user: stabilizes within K samples of sitting down, stays stable, and a single
/// detector hiccup (one missed frame, not the user actually leaving) costs no more than another
/// K samples to re-stabilize — no cumulative or growing penalty across repeated dropouts.
[<Fact>]
let ``a seated user's coherent presence stabilizes quickly and recovers fast after a single-frame dropout`` () =
    let box = { X = 0.4; Y = 0.3; W = 0.22; H = 0.3 }
    let step state detection = PresenceFilter.step (FilterConfig.Default, state, detection)

    let r1 = step PresenceFilter.initial (Nullable(box))
    let r2 = step r1.State (Nullable(box))
    let r3 = step r2.State (Nullable(box))
    Assert.False(r1.StablePresence)
    Assert.False(r2.StablePresence)
    Assert.True(r3.StablePresence)

    let r4 = step r3.State (Nullable(box))
    let r5 = step r4.State (Nullable(box))
    Assert.True(r4.StablePresence)
    Assert.True(r5.StablePresence)

    let dropout = step r5.State (Nullable())
    Assert.False(dropout.StablePresence)

    let a1 = step dropout.State (Nullable(box))
    let a2 = step a1.State (Nullable(box))
    let a3 = step a2.State (Nullable(box))
    Assert.False(a1.StablePresence)
    Assert.False(a2.StablePresence)
    Assert.True(a3.StablePresence)

[<Properties(Arbitrary = [| typeof<Generators> |])>]
module PropertyTests =

    /// property: IoU is symmetric in its two arguments, for any pair of normalized boxes.
    [<Property>]
    let ``IoU is symmetric`` (a: FaceBox) (b: FaceBox) =
        PresenceFilter.iou (a, b) = PresenceFilter.iou (b, a)

    let private stepDefault (acc: FilterResult) (input: FaceBox option) : FilterResult =
        let nullable = match input with Some b -> Nullable(b) | None -> Nullable()
        PresenceFilter.step (FilterConfig.Default, acc.State, nullable)

    let private seed : FilterResult = { State = PresenceFilter.initial; StablePresence = false }

    /// property 1: an alternating detection/gap stream (detection, gap, detection, gap, ...)
    /// never reports stable presence, for any sequence of boxes — a single detection can never
    /// accumulate the required run length when every other sample is a gap.
    [<Property>]
    let ``an alternating detection-gap stream never reports stable presence`` (boxes: FaceBox list) =
        boxes
        |> List.collect (fun b -> [ Some b; None ])
        |> List.scan stepDefault seed
        |> List.tail
        |> List.forall (fun r -> not r.StablePresence)

    /// Two regions of the unit frame guaranteed disjoint in X regardless of Y or size — any box
    /// from region A has IoU 0 against any box from region B — so a sequence alternating
    /// between them can never contain a spatially-coherent consecutive pair.
    let private regionABoxGen: Gen<FaceBox> =
        gen {
            let! w = Gen.choose (5, 15) |> Gen.map (fun i -> float i / 100.0)
            let! h = Gen.choose (5, 15) |> Gen.map (fun i -> float i / 100.0)
            let! y = Gen.choose (0, 100) |> Gen.map (fun i -> float i / 100.0 * (1.0 - h))
            return { X = 0.0; Y = y; W = w; H = h }
        }

    let private regionBBoxGen: Gen<FaceBox> =
        gen {
            let! w = Gen.choose (5, 15) |> Gen.map (fun i -> float i / 100.0)
            let! h = Gen.choose (5, 15) |> Gen.map (fun i -> float i / 100.0)
            let! y = Gen.choose (0, 100) |> Gen.map (fun i -> float i / 100.0 * (1.0 - h))
            return { X = 0.65; Y = y; W = w; H = h }
        }

    let rec private alternatingBoxesGen (useA: bool) (n: int) : Gen<FaceBox list> =
        if n <= 0 then
            Gen.constant []
        else
            gen {
                let! box = if useA then regionABoxGen else regionBBoxGen
                let! rest = alternatingBoxesGen (not useA) (n - 1)
                return box :: rest
            }

    let private incoherentAlternatingSequenceGen: Gen<FaceBox list> =
        gen {
            let! len = Gen.choose (2, 25)
            let! startWithA = Arb.generate<bool>
            return! alternatingBoxesGen startWithA len
        }

    /// property 2: a run of detections whose consecutive-pair IoU always stays below
    /// `MinCoherenceIoU` never reports stable presence, no matter how long the run runs — a
    /// detection alone is never enough; it must also be spatially coherent with its predecessor.
    [<Property>]
    let ``consecutive detections with below-threshold IoU never report stable presence`` () =
        Prop.forAll (Arb.fromGen incoherentAlternatingSequenceGen) (fun boxes ->
            boxes
            |> List.map Some
            |> List.scan stepDefault seed
            |> List.tail
            |> List.forall (fun r -> not r.StablePresence))

    /// property 3: K or more consecutive spatially-coherent detections always report stable
    /// presence, and it persists for as long as the coherent run continues.
    [<Property>]
    let ``stability persists for as long as a coherent run of at least K continues`` (box: FaceBox) (extra: NonNegativeInt) =
        let k = FilterConfig.Default.RequiredConsecutive
        let total = k + min extra.Get 10
        List.replicate total (Some box)
        |> List.scan stepDefault seed
        |> List.tail
        |> List.skip (k - 1)
        |> List.forall (fun r -> r.StablePresence)
