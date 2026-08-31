namespace PresenceLock.Core

/// Static cadence constants (RFC 0002-environment-levels, "Verification harness"), shared
/// verbatim between the shell (`Program.cs`) and the test-side environment model, so drift
/// between "what the shell actually ticks at" and "what the model explores" becomes impossible
/// by construction for everything static. Deliberately its OWN file, not beside `PolicyConfig`
/// in `Types.fs` (RFC round 3): `PolicyConfig` feeds live decisions inside `step`; `Cadence`
/// exists only so `Program.cs`'s timer construction and the model's `Tick` delta set read one
/// shared truth, and adjacency to `PolicyConfig` would invite conflating "decision threshold"
/// (tunable, read live every `step`) with "wiring cadence" (static, read once at construction) —
/// two different roles. `Policy.step`/`Policy.start` take NO dependency on this module; nothing
/// here ever flows into a decision.
module Cadence =

    /// Watchdog timer interval (ms) -- also the inhibitor poll cadence from slice 5 onward,
    /// since `LockInhibitorRegistry.Refresh()` rides the watchdog tick. `Program.cs` reads this
    /// constant at the `watchdogTimer` construction instead of a bare `5000` literal. Does NOT
    /// govern `retryTimer` (acquisition-retry cadence, a different concern that happens to share
    /// the same numeric value today) or `deviceSettleTimer` -- neither is a modeled cadence.
    [<Literal>]
    let WatchdogIntervalMs = 5000

    /// Default `SampleIntervalMs` -- the config default `Program.cs`'s `Config` record falls
    /// back to, and the single representative interval the environment model explores at.
    /// Runtime retuning via Settings is real and intentionally NOT modeled (RFC: "the genuinely
    /// irreducible remainder is `SampleIntervalMs`'s runtime tunability... accepted, and now the
    /// only accepted cost here").
    [<Literal>]
    let DefaultSampleIntervalMs = 500
