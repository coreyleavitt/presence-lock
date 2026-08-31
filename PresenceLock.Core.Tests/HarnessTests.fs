module PresenceLock.Core.Tests.HarnessTests

open Xunit
open FsCheck
open FsCheck.Xunit
open PresenceLock.Core
open PresenceLock.Core.Tests.HarnessModel

/// RFC 0002-environment-levels, "Verification harness". Slice 2 landed properties 13 (minus the
/// `lockInhibited` clause) and 14, the edge-reset postcondition, the incident-sequence replay,
/// and the exhaustive explorer over `StepUnderTest<'aux>` with both committed mutation fixtures.
/// Slice 3 activates the `MediaPlaying`/`LockInhibited` axis (`ToggleMediaPlaying` joins the
/// action alphabet), adds property 13's `lockInhibited` agreement clause, and adds harness
/// check 3 (inhibitor semantics) -- all inside `HarnessModel.fs`'s `explore`/`assertStatusAgreement`,
/// exercised transparently by the tests below without needing their own dedicated cases.

/// Depth bound shared by every exploration in this file. Chosen empirically, not arbitrarily:
/// the real core's reachable graph under this alphabet/config closed (BFS's queue empties
/// naturally, `MaxDepthReached` < this bound) at depth 11 with 1132 nodes in ~40ms before slice
/// 3 activated the `MediaPlaying`/`LockInhibited` axis (`ToggleMediaPlaying` joining the action
/// alphabet); re-probed after that change, closure now lands at depth 12 with 2272 nodes in
/// ~120ms -- roughly double the node count, consistent with an independent boolean axis
/// multiplying the reachable (env x core-state) product, and one deeper because reaching some
/// inhibited/uninhibited combinations costs one extra toggle. 16 still gives slack above the
/// re-probed fixed point while staying two orders of magnitude under the ~30s budget -- raising
/// it further cannot find more nodes for the real core, since the graph is already fully closed;
/// it only matters for a mutant whose own reachable graph might be larger (neither committed
/// mutant needs it -- both are caught well inside the fixed point too).
let private maxDepth = 16

// --------------------------------------------------------------------------------------------
// Randomized (FsCheck) coverage of properties 13/14 against the real core, ahead of building
// the exhaustive explorer -- the TDD-adapted order the slice's implementation plan pins.
// --------------------------------------------------------------------------------------------

let private envActionGen: Gen<EnvAction> =
    Gen.oneof
        [ Gen.constant OsLock
          Gen.constant OsUnlock
          Gen.constant TogglePause
          Gen.constant ToggleMediaPlaying
          Gen.constant ToggleCameraAlive
          Gen.constant ToggleFacePresent
          Gen.constant ToggleInputIdle
          Gen.elements (tickDeltas harnessConfig) |> Gen.map Tick ]

let private actionsGen: Gen<EnvAction list> =
    Gen.listOfLength 20 envActionGen

/// Applies `actions` from the harness's initial world in order, skipping any that are illegal
/// from the current truth (mirrors `explore`'s own use of `apply`) and asserting property 13
/// after every legal step. Returns the final world for further assertions (e.g. property 14).
let private runLegalSequence (sut: StepUnderTest<'aux>) (actions: EnvAction list) : World<'aux> =
    let mutable world = initialWorld sut harnessConfig
    let mutable trace: EnvAction list = []
    for action in actions do
        match apply sut harnessConfig world action with
        | Some(world', _) ->
            trace <- action :: trace
            assertStatusAgreement harnessConfig world' trace
            world <- world'
        | None -> ()
    world

[<Property>]
let ``property 13: status/level agreement holds after every legal action from any reachable world (real core)`` () =
    Prop.forAll (Arb.fromGen actionsGen) (fun actions ->
        runLegalSequence realStepUnderTest actions |> ignore
        true)

[<Property>]
let ``property 14: protection liveness holds from any world reached by a legal action sequence (real core)`` () =
    Prop.forAll (Arb.fromGen actionsGen) (fun actions ->
        let world = runLegalSequence realStepUnderTest actions
        assertProtectionLiveness realStepUnderTest harnessConfig world []
        true)

// --------------------------------------------------------------------------------------------
// Exhaustive explorer (real core): properties 13 + 14 plus the edge-reset postcondition,
// asserted at every node/edge of the reachable graph, not merely at FsCheck-sampled ones.
// --------------------------------------------------------------------------------------------

[<Fact>]
let ``exhaustive explorer: the real core satisfies properties 13 and 14 and every taken suppressed edge resets cleanly`` () =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let result = explore realStepUnderTest harnessConfig maxDepth
    sw.Stop()
    printfn
        "explorer (real core): %d nodes, max depth %d, %dms"
        result.NodesExplored
        result.MaxDepthReached
        sw.ElapsedMilliseconds
    Assert.True(result.NodesExplored > 0, "expected a non-trivial reachable graph")
    Assert.True(result.MaxDepthReached > 0, "expected exploration to proceed past the root")

// --------------------------------------------------------------------------------------------
// Incident-sequence replay: pause -> lock -> unlock -> resume with a concrete time schedule.
// --------------------------------------------------------------------------------------------

[<Fact>]
let ``incident-sequence replay: pause, lock, unlock, resume lands only on states the explorer already visited, and the final state locks`` () =
    let explored = explore realStepUnderTest harnessConfig maxDepth
    let schedule =
        [ TogglePause // pause (user toggles the tray while unlocked)
          Tick 100L
          OsLock // session lock arrives on top of the pause
          Tick 100L
          OsUnlock // session unlock while still paused -- the incident's exact swallowed edge
          Tick 100L
          TogglePause // resume
          Tick 100L ]
    let mutable world = initialWorld realStepUnderTest harnessConfig
    Assert.Contains(keyOf harnessConfig world, explored.Visited)
    for action in schedule do
        match apply realStepUnderTest harnessConfig world action with
        | Some(world', _) ->
            world <- world'
            Assert.Contains(keyOf harnessConfig world, explored.Visited)
        | None -> failwithf "incident replay: illegal action %A from truth %A" action world.Env.Truth
    let lockAction, _ = driveToLock realStepUnderTest harnessConfig world
    Assert.Equal(Action.Lock, lockAction)

// --------------------------------------------------------------------------------------------
// Committed mutation fixtures (RFC: "each mutant is a small test-local alternate
// implementation, never a production edit-and-revert" -- permanent negative fixtures proving
// the harness bites). Both delegate to the real, unmodified Policy.step/start (State's
// representation is opaque outside PresenceLock.Core) and diverge only in what StepInputs they
// feed it -- see HarnessModel.fs's construction notes on each.
// --------------------------------------------------------------------------------------------

let private messageOf (ex: HarnessViolation) : string = ex.Data0

[<Fact>]
let ``mutant: the folded-flag variant is caught by the explorer, reproducing the historical incident`` () =
    let ex = Assert.Throws<HarnessViolation>(fun () -> explore foldedFlagStepUnderTest harnessConfig maxDepth |> ignore)
    printfn "folded-flag mutant counterexample:\n%s" (messageOf ex)
    Assert.Contains("Trace:", messageOf ex)

[<Fact>]
let ``mutant: the stale-LastInputs variant is caught by the explorer, reproducing the swallowed suppression edge`` () =
    let ex =
        Assert.Throws<HarnessViolation>(fun () -> explore staleLastInputsStepUnderTest harnessConfig maxDepth |> ignore)
    printfn "stale-LastInputs mutant counterexample:\n%s" (messageOf ex)
    Assert.Contains("Trace:", messageOf ex)
