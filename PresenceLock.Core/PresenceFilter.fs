namespace PresenceLock.Core

open System

/// Presence-stabilization filter (rfc-core-brain.handoff.md, "Burn-in incident 2026-07-28"):
/// Windows' `FaceDetector` produces threshold-flicker false positives on an empty scene under a
/// hunting auto-framing crop — a single stray `FaceSeen` reset `Policy`'s away clock, so the app
/// never locked. Absence already integrates over `AwayThresholdMs`; presence previously flipped
/// on one noisy sample. This module restores evidential symmetry: presence counts only once an
/// unbroken run of spatially coherent detections (consecutive-pair IoU past the acquisition/
/// maintenance threshold — see `FilterConfig`) has spanned `MinCoherentMs` of monotonic time,
/// using the `FaceBox` signal `FaceDetector` already returns but the shell previously discarded
/// outright. Deliberately time-based, not sample-count-based (a bug found and fixed after the
/// original frame-count design shipped): coupling stability to a sample count ties its real-
/// world meaning to `SampleIntervalMs`, and lets one dropped detection under fast sampling cost
/// disproportionately more re-stabilization time than the away clock, measured in real time,
/// gives back — exactly the class of bug R1-27 fixed for every other threshold in this codebase.
/// A pure sibling of `Policy` — not inside it — because it filters *sensing*, not decision: it
/// never touches `Policy`'s pinned contract, and both legacy and the shadow core consume its
/// single stabilized `bool` upstream of any decision logic, preserving burn-in parity between
/// them.

/// A single detected face's bounding box, normalized to [0,1] by the frame's pixel dimensions
/// (the shell divides by the gray-converted `SoftwareBitmap`'s `PixelWidth`/`PixelHeight` before
/// calling `PresenceFilter.step`). Normalization makes spatial coherence independent of camera
/// resolution and of Windows Studio Effects' auto-framing crop changing the pixel frame size
/// mid-session — the same physical face motion produces the same IoU regardless of which crop
/// is currently active.
[<Struct>]
type FaceBox =
    { X: float
      Y: float
      W: float
      H: float }

/// Tunables for `PresenceFilter.step`. `Default` is the pinned false-lock-window fix: a
/// coherent run must span at least `MinCoherentMs` of monotonic time (not a sample count —
/// coupling stability to sample count reproduced the exact class of bug R1-27 fixed for
/// every other threshold: a dropped detection under fast sampling resets progress while the
/// away clock, measured in real time, keeps running). `AcquireIoU` gates a fresh/broken run;
/// `MaintainIoU` — deliberately looser — gates a run that has already reached stability once
/// (Schmitt-trigger hysteresis: established presence is sticky against IoU jitter from a
/// hunting auto-framing crop or a slight head turn, without loosening the initial acquisition
/// bar). No on-disk schema for these yet (rfc-core-brain.handoff.md, burn-in incident note:
/// "no json/schema changes — defaults only for now") — `Default` is the only value the shell
/// wires in.
type FilterConfig =
    { MinCoherentMs: int64
      AcquireIoU: float
      MaintainIoU: float }

    static member Default: FilterConfig =
        { MinCoherentMs = 1000L
          AcquireIoU = 0.3
          MaintainIoU = 0.15 }

/// Opaque filter state, carried by the shell exactly like `Policy.State`: constructed only by
/// `PresenceFilter.initial`, threaded through `step`, and never pattern-matched or hand-rolled
/// outside this assembly — `internal` fields, same rationale as `State`'s in Types.fs (which
/// likewise uses `MonotonicMs option`, not `Nullable`, for internal-only timestamp fields —
/// `Nullable` is reserved for values a C# caller actually constructs or reads, and no shell
/// code ever touches these).
type FilterState =
    internal
        { /// The most recent detection's box, or `None` immediately after a gap (a `step` call
          /// with no detection) — a gap always clears this, so a fresh run after a gap starts
          /// with no predecessor to compare against (no memory across gaps). Always `Some` in
          /// lockstep with `RunStartAt`: both are set together on every detection and cleared
          /// together on every gap.
          PreviousBox: FaceBox option
          /// The monotonic time the current unbroken coherent run began. `None` exactly when
          /// `PreviousBox` is `None`.
          RunStartAt: MonotonicMs option
          /// Whether the most recent `step` reported `StablePresence = true` — the Schmitt-
          /// trigger memory that selects `MaintainIoU` (once stable) vs. `AcquireIoU`
          /// (otherwise) for the next detection's coherence check.
          WasStable: bool }

/// Named struct result (see `StepResult`'s rationale in Types.fs): no per-sample tuple
/// allocation on the sampling-timer hot path, and C# reads `.State`/`.StablePresence` instead
/// of `.Item1`/`.Item2`.
[<Struct>]
type FilterResult =
    { State: FilterState
      StablePresence: bool }

/// Pure per-frame presence stabilizer, sibling to `Policy` (see the module-level rationale
/// above `FaceBox`). No clocks, no I/O: `step` is a pure function of the previous `FilterState`
/// and this frame's detection.
module PresenceFilter =

    /// The state before any frame has been observed by a fresh camera acquisition. The shell
    /// resets to this value everywhere it resets `Policy`'s own baselines — `StartWatchingAsync`'s
    /// init-success path, and the session-unlock/resume paths (`OnSessionSwitch`'s unlock
    /// handling, `Resumed`) — since a fresh acquisition or a fresh watching episode must not
    /// inherit spatial coherence measured against a previous acquisition's or episode's frames.
    let initial: FilterState = { PreviousBox = None; RunStartAt = None; WasStable = false }

    /// Intersection-over-union of two normalized boxes, in [0,1]: 0 for disjoint boxes, 1 for
    /// two identical non-degenerate boxes. Symmetric in `a`/`b`. The sole geometry primitive
    /// spatial coherence (`step`'s run-length gate) is built on.
    let iou (a: FaceBox, b: FaceBox) : float =
        let ax2, ay2 = a.X + a.W, a.Y + a.H
        let bx2, by2 = b.X + b.W, b.Y + b.H
        let ix1, iy1 = max a.X b.X, max a.Y b.Y
        let ix2, iy2 = min ax2 bx2, min ay2 by2
        let iw, ih = max 0.0 (ix2 - ix1), max 0.0 (iy2 - iy1)
        let interArea = iw * ih
        let unionArea = a.W * a.H + b.W * b.H - interArea
        if unionArea <= 0.0 then 0.0 else interArea / unionArea

    /// Advances the filter by one frame. `now` is the shell's monotonic clock at this sample
    /// (`Environment.TickCount64`, same clock domain as `Policy.step`'s `now` — see
    /// `MonotonicMs`'s doc in Types.fs). `detection` is the largest detected face's box this
    /// frame, normalized by the shell, or an empty `Nullable<FaceBox>` for "no detection this
    /// frame" — `Nullable`, not `FaceBox option`, at this boundary (house rule, R1-31/R2-12: no
    /// `FSharpOption` at a C#-constructed parameter) and one entry point rather than two: a
    /// single function mirrors `Policy.step`'s single-dispatch shape, and the caller-side branch
    /// (`detection.HasValue`) is no simpler split across two named functions than it is as one
    /// pattern match here.
    ///
    /// A gap (no detection) resets the run immediately and reports absence instantly — the away
    /// threshold in `Policy` is the sole absence integrator; this filter only ever delays how
    /// fast *presence* is reported, never how fast absence is.
    let step (config: FilterConfig, state: FilterState, now: MonotonicMs, detection: Nullable<FaceBox>) : FilterResult =
        if not detection.HasValue then
            { State = { PreviousBox = None; RunStartAt = None; WasStable = false }
              StablePresence = false }
        else
            let box = detection.Value
            let (MonotonicMs nowMs) = now
            // A detection that breaks coherence with the previous one is itself a real, fresh
            // detection — it becomes the start of a new candidate run at `now`, exactly like the
            // first detection after a gap, rather than merely failing to extend the old one.
            // Hysteresis: the bar is `MaintainIoU` (looser) once the run has already reached
            // stability, `AcquireIoU` (stricter) otherwise — `state.PreviousBox`/`RunStartAt` are
            // always both `Some` or both `None` together, so the run's start time is available
            // whenever there is a previous box to compare against.
            let runStartAt =
                match state.PreviousBox, state.RunStartAt with
                | Some prevBox, Some startedAt ->
                    let threshold = if state.WasStable then config.MaintainIoU else config.AcquireIoU
                    if iou (prevBox, box) >= threshold then startedAt else now
                | _ -> now
            let (MonotonicMs runStartMs) = runStartAt
            let stable = nowMs - runStartMs >= config.MinCoherentMs
            { State = { PreviousBox = Some box; RunStartAt = Some runStartAt; WasStable = stable }
              StablePresence = stable }
