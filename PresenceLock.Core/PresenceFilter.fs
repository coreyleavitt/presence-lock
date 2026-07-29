namespace PresenceLock.Core

open System

/// Presence-stabilization filter (rfc-core-brain.handoff.md, "Burn-in incident 2026-07-28"):
/// Windows' `FaceDetector` produces threshold-flicker false positives on an empty scene under a
/// hunting auto-framing crop — a single stray `FaceSeen` reset `Policy`'s away clock, so the app
/// never locked. Absence already integrates over `AwayThresholdMs`; presence previously flipped
/// on one noisy sample. This module restores evidential symmetry: presence counts only after
/// `RequiredConsecutive` consecutive detections whose boxes are spatially coherent
/// (consecutive-pair IoU >= `MinCoherenceIoU`), using the `FaceBox` signal `FaceDetector` already
/// returns but the shell previously discarded outright. A pure sibling of `Policy` — not inside
/// it — because it filters *sensing*, not decision: it never touches `Policy`'s pinned contract,
/// and both legacy and the shadow core consume its single stabilized `bool` upstream of any
/// decision logic, preserving burn-in parity between them.

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

/// Tunables for `PresenceFilter.step`. `Default` is the pinned burn-in-incident fix: 3
/// consecutive coherent detections required, at least 30% consecutive-pair IoU. No on-disk
/// schema for these yet (rfc-core-brain.handoff.md, burn-in incident note: "no json/schema
/// changes — defaults only for now") — `Default` is the only value the shell wires in.
type FilterConfig =
    { RequiredConsecutive: int
      MinCoherenceIoU: float }

    static member Default: FilterConfig =
        { RequiredConsecutive = 3
          MinCoherenceIoU = 0.3 }

/// Opaque filter state, carried by the shell exactly like `Policy.State`: constructed only by
/// `PresenceFilter.initial`, threaded through `step`, and never pattern-matched or hand-rolled
/// outside this assembly — `internal` fields, same rationale as `State`'s in Types.fs.
type FilterState =
    internal
        { /// The most recent detection's box, or `None` immediately after a gap (a `step` call
          /// with no detection) — a gap always clears this, so stability after a gap requires
          /// entirely fresh consecutive detections (no memory across gaps).
          PreviousBox: FaceBox option
          /// Length of the current run of consecutive detections that have been spatially
          /// coherent with their immediate predecessor. Reset to 1 (not 0) on a detection that
          /// breaks coherence with the previous one — that detection is itself a real, fresh
          /// detection and becomes the start of a new candidate run, exactly like the first
          /// detection after a gap.
          RunLength: int }

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
    /// resets to this value wherever it resets its own `armed`/`noSignalStreak` baseline
    /// (`StartWatchingAsync`'s init-success path) — a fresh camera must not inherit spatial
    /// coherence measured against the previous acquisition's frames.
    let initial: FilterState = { PreviousBox = None; RunLength = 0 }

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

    /// Advances the filter by one frame. `detection` is the largest detected face's box this
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
    let step (config: FilterConfig, state: FilterState, detection: Nullable<FaceBox>) : FilterResult =
        if not detection.HasValue then
            { State = { PreviousBox = None; RunLength = 0 }
              StablePresence = false }
        else
            let box = detection.Value
            // A detection that breaks coherence with the previous one is itself a real, fresh
            // detection — it becomes the start of a new candidate run (length 1), exactly like
            // the first detection after a gap, rather than dropping to 0.
            let runLength =
                match state.PreviousBox with
                | Some prevBox when iou (prevBox, box) >= config.MinCoherenceIoU -> state.RunLength + 1
                | _ -> 1
            { State = { PreviousBox = Some box; RunLength = runLength }
              StablePresence = runLength >= config.RequiredConsecutive }
