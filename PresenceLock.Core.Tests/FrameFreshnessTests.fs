module PresenceLock.Core.Tests.FrameFreshnessTests

open Xunit
open FsCheck
open FsCheck.Xunit
open PresenceLock.Core
open PresenceLock.Core.Tests.Generators

/// Frozen-frame staleness detection (bug fix: `TryAcquireLatestFrame` can re-serve a cached
/// frame forever -- a known WinRT quirk -- so a non-advancing `SystemRelativeTime` must be
/// classified as no-frame, time-based rather than count-based for the same R1-27 reason as
/// `PresenceFilter`).
let private ms (n: int64) = MonotonicMs n

[<Fact>]
let ``the very first observed timestamp is fresh`` () =
    let r = FrameFreshness.step (FreshnessConfig.Default, FrameFreshness.initial, ms 0L, 100L)
    Assert.True(r.IsFresh)

[<Fact>]
let ``an advancing timestamp stays fresh no matter how much monotonic time has passed`` () =
    let r0 = FrameFreshness.step (FreshnessConfig.Default, FrameFreshness.initial, ms 0L, 100L)
    // A huge monotonic gap, but the timestamp advanced -- still fresh.
    let r1 = FrameFreshness.step (FreshnessConfig.Default, r0.State, ms 60_000L, 200L)
    Assert.True(r1.IsFresh)

[<Fact>]
let ``an identical timestamp stays fresh until StaleAfterMs has elapsed since it last advanced`` () =
    let r0 = FrameFreshness.step (FreshnessConfig.Default, FrameFreshness.initial, ms 0L, 100L)
    let r1 = FrameFreshness.step (FreshnessConfig.Default, r0.State, ms 4999L, 100L)
    Assert.True(r1.IsFresh)

/// Definite boundary semantic (item 5's "pick and test a definite semantic"): fresh iff
/// elapsed strictly less than StaleAfterMs -- consistent with `Policy`'s own convention of
/// `>=` marking the threshold-crossed ("bad") side (e.g. `NoSignalReportAfterMs`).
[<Fact>]
let ``exactly StaleAfterMs since the last advance is already stale`` () =
    let r0 = FrameFreshness.step (FreshnessConfig.Default, FrameFreshness.initial, ms 0L, 100L)
    let r1 = FrameFreshness.step (FreshnessConfig.Default, r0.State, ms FreshnessConfig.Default.StaleAfterMs, 100L)
    Assert.False(r1.IsFresh)

[<Fact>]
let ``one millisecond short of StaleAfterMs is still fresh`` () =
    let r0 = FrameFreshness.step (FreshnessConfig.Default, FrameFreshness.initial, ms 0L, 100L)
    let r1 = FrameFreshness.step (FreshnessConfig.Default, r0.State, ms (FreshnessConfig.Default.StaleAfterMs - 1L), 100L)
    Assert.True(r1.IsFresh)

[<Fact>]
let ``a timestamp that advances again after going stale immediately recovers freshness`` () =
    let r0 = FrameFreshness.step (FreshnessConfig.Default, FrameFreshness.initial, ms 0L, 100L)
    let stale = FrameFreshness.step (FreshnessConfig.Default, r0.State, ms FreshnessConfig.Default.StaleAfterMs, 100L)
    Assert.False(stale.IsFresh)

    // The pipe recovers: a new, distinct timestamp arrives -- fresh immediately, no matter
    // how long the prior stale run lasted.
    let recovered = FrameFreshness.step (FreshnessConfig.Default, stale.State, ms (FreshnessConfig.Default.StaleAfterMs + 10L), 101L)
    Assert.True(recovered.IsFresh)

    // And a subsequent repeat of the new timestamp stays fresh until its own StaleAfterMs
    // window elapses, measured from the recovery, not from the original stale run.
    let stillFresh =
        FrameFreshness.step (
            FreshnessConfig.Default, recovered.State,
            ms (FreshnessConfig.Default.StaleAfterMs + 10L + FreshnessConfig.Default.StaleAfterMs - 1L), 101L)
    Assert.True(stillFresh.IsFresh)

[<Properties(Arbitrary = [| typeof<Generators> |])>]
module PropertyTests =

    /// property: a strictly-advancing sequence of distinct timestamps stays fresh forever,
    /// regardless of the actual timestamp values or how much monotonic time separates them --
    /// only "did the timestamp change" matters for freshness while advancing.
    let private advancingRunGen: Gen<(int64 * int64) list> =
        gen {
            let! n = Gen.choose (1, 15)
            let! monotonicDeltas = Gen.listOfLength n (Gen.choose (0, 10_000)) |> Gen.map (List.map int64)
            let! timestamps =
                Gen.listOfLength n (Gen.choose (0, 1_000_000))
                |> Gen.map (List.map int64 >> List.distinct)
            let times = List.scan (+) 0L monotonicDeltas |> List.tail
            // `List.distinct` above can shrink `timestamps` below `n` on duplicate draws --
            // zip only over the common prefix so this generator never throws on length mismatch.
            let len = min times.Length timestamps.Length
            return List.zip (List.truncate len times) (List.truncate len timestamps)
        }

    [<Property>]
    let ``a run of distinct advancing timestamps is always fresh`` () =
        Prop.forAll (Arb.fromGen advancingRunGen) (fun steps ->
            let mutable state = FrameFreshness.initial
            let mutable allFresh = true
            let mutable t = 0L
            for (nowMs, ts) in steps do
                t <- t + nowMs
                let r = FrameFreshness.step (FreshnessConfig.Default, state, ms t, ts)
                state <- r.State
                allFresh <- allFresh && r.IsFresh
            allFresh)

    /// property: once a timestamp stops advancing, freshness is exactly the elapsed-time
    /// predicate against the moment it last advanced -- independent of how many repeated
    /// samples of the same value arrived in between (the count-vs-time distinction this
    /// module exists to get right).
    [<Property>]
    let ``freshness while a timestamp is frozen equals whether StaleAfterMs has elapsed since it last advanced`` (frozenTs: int) (checkAt: NonNegativeInt) =
        let r0 = FrameFreshness.step (FreshnessConfig.Default, FrameFreshness.initial, ms 0L, int64 frozenTs)
        let checkAtMs = int64 checkAt.Get
        let r1 = FrameFreshness.step (FreshnessConfig.Default, r0.State, ms checkAtMs, int64 frozenTs)
        r1.IsFresh = (checkAtMs < FreshnessConfig.Default.StaleAfterMs)
