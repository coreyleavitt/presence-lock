using System.Diagnostics;
using System.Text.Json;
using Windows.Graphics.Imaging;
using EnclosurePanel = Windows.Devices.Enumeration.Panel;
using Core = PresenceLock.Core;

namespace PresenceLock;

/// Pure, shell-side helpers that bridge legacy sensing data to the `PresenceLock.Core` API
/// (0001-core-brain.md, slice 8a). Everything here is sensing/mapping, not policy — it stays
/// on the C# side of the boundary by design (RFC "Notes: recovery boundary" /
/// "Config: file schema, mapping, validation") — but each function is exactly the class of
/// "expected behavior existed only as code" defect the RFC's Motivation cites, so each is
/// named and unit-tested (see PresenceLock.Tests) rather than left inline.
static class PolicyBridge
{
    /// Built-in defaults for all nine `PolicyConfig` fields (the ninth, `UpgradeCooldownMs`,
    /// added by the 2026-08-01 addendum), applied as a unit (never a partial mix —
    /// 0001-core-brain.md, "Config" validation rules) whenever the persisted values fail
    /// validation. Matches the pinned mapping table exactly.
    static readonly Core.PolicyConfig DefaultPolicyConfig = new(
        awayThresholdMs: 5000,
        inputIdleRequiredMs: 10000,
        graceMs: 10000,
        noSignalReportAfterMs: 10000,
        reevaluateAfterMs: 20000,
        reevaluateCooldownMs: 600000,
        recoveryFailureThreshold: 3,
        recoveryCooldownMs: 600000,
        upgradeCooldownMs: 600000);

    /// Maps the flat, on-disk `Config` (unchanged schema) to `PolicyConfig`, per the pinned
    /// mapping table. Validates the nine policy fields as a single unit — any one out of
    /// range falls the whole set back to `DefaultPolicyConfig`, never a partially-defaulted
    /// mix (0001-core-brain.md: "a zero-filled cooldown would make the next camera hiccup an
    /// instant restart loop"). Sensing fields (`CameraNameContains`, `DarkFrameMeanThreshold`,
    /// `SampleIntervalMs`) are untouched by this function and never affected by a policy-field
    /// failure — except that `SampleIntervalMs` is read (not written) as one input to the
    /// away-threshold floor below, the one place a sensing field and a policy field interact.
    /// Shared by file load and (once the Settings dialog wires it) the commit path, per the
    /// RFC — same function, so an invalid committed value can never reach `step` unvalidated.
    /// The `out defaultsInUse` overload additionally reports whether the all-or-nothing
    /// fallback fired, feeding `Render`'s config-fallback annotation (stage-4 follow-up:
    /// "Status annotation when config makes locking effectively unreachable") — the load-time
    /// log line below stays the detailed diagnostic; the annotation is the always-visible cue.
    internal static Core.PolicyConfig BuildPolicyConfig(Config cfg) =>
        BuildPolicyConfig(cfg, out _);

    internal static Core.PolicyConfig BuildPolicyConfig(Config cfg, out bool defaultsInUse)
    {
        long awayMs = (long)(cfg.AwayThresholdSeconds * 1000);
        long idleMs = (long)(cfg.InputIdleSeconds * 1000);
        long graceMs = (long)(cfg.GraceSeconds * 1000);
        long noSignalMs = cfg.NoSignalReportAfterMs;
        long reevaluateAfterMs = cfg.ReevaluateAfterMs;
        long reevaluateCooldownMs = cfg.ReevaluateCooldownMs;
        int recoveryThreshold = cfg.RecoveryFailureThreshold;
        long recoveryCooldownMs = cfg.RecoveryCooldownMs;
        long upgradeCooldownMs = cfg.UpgradeCooldownMs;

        // Bug fix (false-lock window): worst-case re-stabilization after one dropped detection
        // is ~one sample gap plus the presence filter's MinCoherentMs dwell. If AwayThresholdMs
        // could sit below that, a single dropped detection under fast sampling could make the
        // away clock fire before presence has had a chance to re-stabilize -- a false lock
        // while the user never left. This makes that emergent interaction impossible by
        // construction, the same way the other eight fields are validated as a unit.
        long awayFloorMs = Core.FilterConfig.Default.MinCoherentMs + 2 * cfg.SampleIntervalMs;

        // Upper bounds (SEC-3): before this, only lower bounds were checked, so an absurdly
        // large value -- a bad hand-edit, a corrupted file, or same-user tampering (this app
        // cannot defend against a same-user writer; see CLAUDE.md's threat model) -- passed
        // validation and silently made locking effectively unreachable while Status kept
        // reading plain "Watching," with no cue anything was wrong (unlike the SMTC inhibitor
        // path, which does annotate). Mirrors SanitizeSensingConfig's existing discipline of
        // giving every field a real ceiling, not just a floor. The away/grace/idle-class fields
        // gate the lock decision itself, so their ceiling is a few hours -- locking on a
        // multi-hour timescale is not a meaningful "auto-lock while away" configuration under
        // any legitimate use, but the ceiling stays generous rather than opinionated about what
        // "a few hours" should be. Cooldown-class fields gate retry/re-evaluation cadence, not
        // the lock decision, so they get a longer, day-scale ceiling. RecoveryFailureThreshold
        // is a small integer retry count; a three-digit ceiling is already far past any
        // legitimate value while still bounding it against, e.g., an accidental extra zero.
        const long PolicyFieldCeilingMs = 4 * 3600_000L;      // 4 hours
        const long CooldownCeilingMs = 24 * 3600_000L;        // 24 hours
        const int RecoveryFailureThresholdCeiling = 100;

        bool valid =
            awayMs > 0 && idleMs > 0 && graceMs > 0 &&
            noSignalMs > 0 && reevaluateAfterMs > 0 && reevaluateCooldownMs > 0 &&
            recoveryCooldownMs > 0 && recoveryThreshold >= 1 && upgradeCooldownMs > 0 &&
            awayMs <= PolicyFieldCeilingMs && idleMs <= PolicyFieldCeilingMs && graceMs <= PolicyFieldCeilingMs &&
            noSignalMs <= PolicyFieldCeilingMs && reevaluateAfterMs <= PolicyFieldCeilingMs &&
            reevaluateCooldownMs <= CooldownCeilingMs && recoveryCooldownMs <= CooldownCeilingMs &&
            upgradeCooldownMs <= CooldownCeilingMs && recoveryThreshold <= RecoveryFailureThresholdCeiling &&
            // noSignalMs's ceiling is also implied transitively (reevaluateAfterMs >= noSignalMs, below,
            // and reevaluateAfterMs <= PolicyFieldCeilingMs, above), so it can never be the *sole*
            // reason validation fails. It is kept explicit as defense-in-depth: were the coupling ever
            // relaxed, noSignalMs would still carry its own bound rather than silently losing it.
            reevaluateAfterMs >= noSignalMs && awayMs >= awayFloorMs;

        defaultsInUse = !valid;
        if (!valid)
        {
            Log.Write("policy config values out of range — falling back to built-in defaults for all policy fields");
            return DefaultPolicyConfig;
        }

        return new Core.PolicyConfig(
            awayThresholdMs: awayMs,
            inputIdleRequiredMs: idleMs,
            graceMs: graceMs,
            noSignalReportAfterMs: noSignalMs,
            reevaluateAfterMs: reevaluateAfterMs,
            reevaluateCooldownMs: reevaluateCooldownMs,
            recoveryFailureThreshold: recoveryThreshold,
            recoveryCooldownMs: recoveryCooldownMs,
            upgradeCooldownMs: upgradeCooldownMs);
    }

    /// The pristine defaults `SanitizeSensingConfig` falls back to, one field at a time — a
    /// plain `new Config()`, not a hand-duplicated literal, so the fallback values can never
    /// drift from `Config`'s own field initializers.
    static readonly Config DefaultSensingConfig = new();

    /// Bug fix (startup crash on a corrupted/hand-edited presencelock.json): `Config.Load()`
    /// deserialized with no bounds check, and `WinFormsTimer.Interval` throws
    /// `ArgumentOutOfRangeException` for a `SampleIntervalMs` &lt; 1 — a bad on-disk value could
    /// kill the app before the tray icon even existed. Each sensing field is validated and
    /// defaulted independently — deliberately not an all-or-nothing unit like
    /// `BuildPolicyConfig`'s nine policy fields (0001-core-brain.md: that unit exists because a
    /// zero-filled cooldown makes the next camera hiccup an instant restart loop; no such
    /// cross-field coupling exists between `SampleIntervalMs` and `DarkFrameMeanThreshold`).
    /// `CameraNameContains` and every policy field pass through untouched — this function is
    /// sensing-only, the mirror image of `BuildPolicyConfig` reading (never writing) sensing
    /// fields.
    internal static Config SanitizeSensingConfig(Config cfg)
    {
        int sampleIntervalMs = cfg.SampleIntervalMs;
        if (sampleIntervalMs < 100 || sampleIntervalMs > 10000)
        {
            Log.Write($"config: SampleIntervalMs {sampleIntervalMs} out of range [100, 10000] — " +
                      $"falling back to default {DefaultSensingConfig.SampleIntervalMs}");
            sampleIntervalMs = DefaultSensingConfig.SampleIntervalMs;
        }

        double darkFrameMeanThreshold = cfg.DarkFrameMeanThreshold;
        if (darkFrameMeanThreshold < 0 || darkFrameMeanThreshold > 255)
        {
            Log.Write($"config: DarkFrameMeanThreshold {darkFrameMeanThreshold} out of range [0, 255] — " +
                      $"falling back to default {DefaultSensingConfig.DarkFrameMeanThreshold}");
            darkFrameMeanThreshold = DefaultSensingConfig.DarkFrameMeanThreshold;
        }

        return new Config
        {
            AwayThresholdSeconds = cfg.AwayThresholdSeconds,
            SampleIntervalMs = sampleIntervalMs,
            GraceSeconds = cfg.GraceSeconds,
            InputIdleSeconds = cfg.InputIdleSeconds,
            CameraNameContains = cfg.CameraNameContains,
            DarkFrameMeanThreshold = darkFrameMeanThreshold,
            NoSignalReportAfterMs = cfg.NoSignalReportAfterMs,
            ReevaluateAfterMs = cfg.ReevaluateAfterMs,
            ReevaluateCooldownMs = cfg.ReevaluateCooldownMs,
            RecoveryFailureThreshold = cfg.RecoveryFailureThreshold,
            RecoveryCooldownMs = cfg.RecoveryCooldownMs,
            UpgradeCooldownMs = cfg.UpgradeCooldownMs,
            // RFC 0002-environment-levels: a bool kill switch has no numeric range to validate,
            // but it MUST still be copied through explicitly -- this method rebuilds a Config
            // field-by-field rather than cloning, so an omitted field here would silently reset
            // a user's `false` back to the type's `true` default on every single load, exactly
            // the kind of silent-behavior-change this RFC exists to eliminate.
            MediaInhibitorEnabled = cfg.MediaInhibitorEnabled,
        };
    }

    /// The real handle-invalid classifier (0001-core-brain.md, "Notes: recovery boundary" /
    /// R2-7): HResult `0x80070006` OR a message-substring match, because the WinRT projection
    /// does not reliably preserve the HResult. Two-parameter form so `OnCaptureFailed` (which
    /// receives `MediaCaptureFailedEventArgs` — only `Code` + `Message`, no `Exception`) can
    /// use it too; the `Exception` overload serves the init path. Shell-side by design
    /// (sensing, not policy) but named and unit-tested per the RFC rather than left inline.
    internal static bool IsHandleInvalid(int hresult, string message) =>
        hresult == unchecked((int)0x80070006) ||
        (message is not null && message.Contains("handle is invalid", StringComparison.OrdinalIgnoreCase));

    internal static bool IsHandleInvalid(Exception ex) => IsHandleInvalid(ex.HResult, ex.Message);

    /// The complete, core-owned gate for "should we attempt (re)acquisition" (0001-core-brain.md
    /// R2-4/R2-15): true exactly while `Policy.status` is `AcquiringCamera` or `Recovering` —
    /// first-init retries only, per the Status priority table, which makes this test
    /// structurally incapable of gating a post-success in-process re-init. Shared by the retry
    /// timer's `Tick` guard and both `KickAcquisitionIfNeeded` call sites, replacing the legacy
    /// shell-local `!paused && !sessionLocked` booleans with one source of truth. Pure and
    /// factored out here (rather than inlined three times as `.Tag ==` comparisons) specifically
    /// so it is independently unit-tested against every `Status` case.
    internal static bool IsAcquiringOrRecovering(Core.Status status) =>
        status.Tag == Core.Status.Tags.AcquiringCamera || status.Tag == Core.Status.Tags.Recovering;

    /// Suppression per the Status priority table (0001-core-brain.md / RFC 0002-environment-
    /// levels "Status"): `Paused` and `SessionLocked` are the two rows sampling stops for.
    /// Used by `Advance`'s suppression-transition rule (filter/freshness resets, sample-timer
    /// stop/start, `KickAcquisitionIfNeeded`) to detect the before/after status pair's edge
    /// without duplicating the priority-row list inline.
    internal static bool IsSuppressedStatus(Core.Status status) =>
        status.Tag == Core.Status.Tags.Paused || status.Tag == Core.Status.Tags.SessionLocked;

    /// Consequences of one before/after `Status` pair across an `Advance` call (RFC
    /// 0002-environment-levels, "Advance and timers", round 3): extracted as a pure function
    /// so the rule generalizing the 2026-07-28 burn-in fix (filter/freshness resets) and the
    /// 2026-08-12 incident's unconditional sample-timer restart is itself independently
    /// testable -- including via the slice-4 WTS-reconciliation correction path, which the
    /// harness cannot see (it models `step` semantics, not WinForms timers) -- without driving
    /// a real `WatcherContext`/WinForms `Timer`. `TransitionLogMessage` is `null` on a
    /// non-transition tick (every other field `false`); `Advance` writes it verbatim when
    /// non-null, matching the two hand-written log lines this replaces exactly.
    internal readonly record struct SuppressionTransitionVerdict(
        bool ResetFilters, bool RestartSampleTimer, bool KickAcquisition, bool StopSampleTimer,
        string? TransitionLogMessage);

    internal static SuppressionTransitionVerdict SuppressionTransitionStep(Core.Status previousStatus, Core.Status newStatus)
    {
        bool wasSuppressed = IsSuppressedStatus(previousStatus);
        bool isSuppressed = IsSuppressedStatus(newStatus);

        if (wasSuppressed && !isSuppressed)
            return new SuppressionTransitionVerdict(
                ResetFilters: true, RestartSampleTimer: true, KickAcquisition: true, StopSampleTimer: false,
                TransitionLogMessage: $"suppression exited: {previousStatus} -> {newStatus}");

        if (!wasSuppressed && isSuppressed)
            return new SuppressionTransitionVerdict(
                ResetFilters: false, RestartSampleTimer: false, KickAcquisition: false, StopSampleTimer: true,
                TransitionLogMessage: $"suppression entered: {previousStatus} -> {newStatus}");

        return new SuppressionTransitionVerdict(
            ResetFilters: false, RestartSampleTimer: false, KickAcquisition: false, StopSampleTimer: false,
            TransitionLogMessage: null);
    }

    /// Result of one WTS session-lock query (RFC 0002-environment-levels, "Session mirror with
    /// reconciliation"): `Succeeded = false` means fail-open for reconciliation purposes,
    /// covering both an outright `WTSQuerySessionInformation` API failure and a
    /// Succeeded-but-uninterpretable `SessionFlags` alike (see `InterpretSessionFlags`) --
    /// both must never set `Locked = true` by side effect, so `Locked` is meaningless whenever
    /// `Succeeded` is false and callers must not read it in that case.
    internal readonly record struct WtsLockQueryResult(bool Succeeded, bool Locked);

    /// Interprets `WTSINFOEX`'s `SessionFlags` (RFC 0002-environment-levels, "Session mirror
    /// with reconciliation", spike deliverable): documented Windows 8+ semantics, confirmed
    /// live on this machine (Windows 11 26200) against a demonstrably unlocked console session
    /// -- `WTS_SESSIONSTATE_LOCK = 0`, `WTS_SESSIONSTATE_UNLOCK = 1`,
    /// `WTS_SESSIONSTATE_UNKNOWN = -1` (0xFFFFFFFF as a signed 32-bit value). Fail-open:
    /// `UNKNOWN` and any other unrecognized value report `Succeeded = false` rather than
    /// guessing a lock state -- "the query did not succeed, for reconciliation purposes" per
    /// the RFC, treating an uninterpretable-but-present value identically to the P/Invoke call
    /// itself failing. Pure and separated from the P/Invoke call itself (`Program.cs`'s
    /// `QuerySessionLocked`) so the one genuinely meaningful piece of logic in the query path
    /// -- what each `SessionFlags` value means -- is unit-tested without a live WTS handle.
    internal static WtsLockQueryResult InterpretSessionFlags(int sessionFlags) => sessionFlags switch
    {
        0 => new WtsLockQueryResult(Succeeded: true, Locked: true),
        1 => new WtsLockQueryResult(Succeeded: true, Locked: false),
        _ => new WtsLockQueryResult(Succeeded: false, Locked: false),
    };

    /// Verdict of one `ReconciliationStep` evaluation: `Mirror`/`DisagreementCount` are the
    /// caller's next `sessionLocked`/disagreement-counter fields (a plain replace, matching
    /// `WatchdogVerdict`'s precedent); `CorrectionApplied` is true only on the tick the mirror
    /// actually changes as a result of this call.
    internal readonly record struct ReconciliationVerdict(bool Mirror, int DisagreementCount, bool CorrectionApplied);

    /// The pure WTS-reconciliation decision (RFC 0002-environment-levels, "Session mirror with
    /// reconciliation"), extracted per the RFC's explicit instruction, mirroring the
    /// `SamplingWatchdogStep` precedent: a several-branch state machine on the mechanism
    /// closest to the incident this RFC exists to fix gets the same independent, dedicated unit
    /// coverage as everything else here, rather than resting on one manual live-smoke
    /// observation.
    ///
    /// Composition, in order:
    /// 1. Skip window (round-3 boundary rule 2): within one watchdog interval of a
    ///    SessionSwitch-driven mirror-value-CHANGING update
    ///    (`ticksSinceLastSessionSwitchMirrorChange &lt; 1`), reconciliation is skipped
    ///    entirely -- mirror and counter both untouched, no correction -- regardless of query
    ///    success or agreement. (Whether an idempotent duplicate `SessionSwitch` delivery
    ///    restarts that counter is decided before this function ever runs -- see
    ///    `SessionSwitchTicksAfterDelivery`.)
    /// 2. Fail-open (round-3 boundary rule 1): a failed query (`querySucceeded = false`) HOLDS
    ///    the disagreement counter -- neither counts toward it nor resets it -- and never
    ///    corrects the mirror. A reset-on-failure reading would let an intermittent-failure
    ///    pattern (disagree, fail, disagree, fail, ...) starve the heal forever.
    /// 3. Agreement resets the streak to zero (only CONSECUTIVE disagreement counts).
    /// 4. Disagreement increments the streak; a correction is applied -- mirror flips to the
    ///    queried value, counter resets to zero -- only once the streak reaches two
    ///    (hysteresis): the first disagreeing tick is recorded but not yet acted on.
    internal static ReconciliationVerdict ReconciliationStep(
        bool mirror,
        bool queriedLocked,
        bool querySucceeded,
        int ticksSinceLastSessionSwitchMirrorChange,
        int consecutiveDisagreementCount)
    {
        if (ticksSinceLastSessionSwitchMirrorChange < 1)
            return new ReconciliationVerdict(Mirror: mirror, DisagreementCount: consecutiveDisagreementCount, CorrectionApplied: false);

        if (!querySucceeded)
            return new ReconciliationVerdict(Mirror: mirror, DisagreementCount: consecutiveDisagreementCount, CorrectionApplied: false);

        if (queriedLocked == mirror)
            return new ReconciliationVerdict(Mirror: mirror, DisagreementCount: 0, CorrectionApplied: false);

        int nextCount = consecutiveDisagreementCount + 1;
        if (nextCount < 2)
            return new ReconciliationVerdict(Mirror: mirror, DisagreementCount: nextCount, CorrectionApplied: false);

        return new ReconciliationVerdict(Mirror: queriedLocked, DisagreementCount: 0, CorrectionApplied: true);
    }

    /// Round-3 boundary rule 2's companion (RFC 0002-environment-levels, "Session mirror with
    /// reconciliation"): the shell's `ticksSinceLastSessionSwitchMirrorChange` counter,
    /// advanced across one `SessionSwitch` delivery. Resets to zero only when the delivery
    /// actually CHANGES the mirror value -- Windows demonstrably double-fires `SessionSwitch`
    /// (0001-core-brain.md), and restarting the skip window on an idempotent duplicate could
    /// push a stuck mirror's heal out indefinitely under unrelated session traffic. Kept as its
    /// own tiny pure function, separate from `ReconciliationStep` (which only ever sees the
    /// resulting tick count as a plain `int`), so this specific correctness rule has its own
    /// dedicated unit test rather than being trusted to a single `if` inline in
    /// `OnSessionSwitch`.
    internal static int SessionSwitchTicksAfterDelivery(bool mirrorValueChanged, int currentTicks) =>
        mirrorValueChanged ? 0 : currentTicks;

    /// The sampling watchdog's status gate (incident 2026-08-12: a resume path that only
    /// restarted `sampleTimer` when `reader is not null` left it permanently stopped after a
    /// camera died inside the restart cooldown while paused). True exactly for the statuses
    /// where sample events should be flowing — `Watching`/`NoSignal` — so `Paused`/
    /// `SessionLocked` (sampling deliberately stopped) and `AcquiringCamera`/`Recovering`
    /// (pre-first-success acquisition is the retry timer's job, not the sample timer's) never
    /// count as starvation. Paired with `SamplingWatchdogStep`.
    internal static bool ExpectsSampling(Core.Status status) => status.Tag switch
    {
        Core.Status.Tags.Watching => true,
        Core.Status.Tags.NoSignal => true,
        Core.Status.Tags.SessionLocked => false,
        Core.Status.Tags.Paused => false,
        Core.Status.Tags.AcquiringCamera => false,
        Core.Status.Tags.Recovering => false,
        _ => throw new UnreachableException(),
    };

    /// Verdict of one `SamplingWatchdogStep` evaluation: `Strikes`/`StampMs` are the caller's
    /// next `watchdogStrikes`/`lastSamplePassAt` (a plain replace, never a merge); `StartSampleTimer`
    /// and `TreatAsCaptureFailed` are mutually exclusive one-shot actions for this tick only —
    /// never both true, and both false on every non-escalating branch.
    internal readonly record struct WatchdogVerdict(int Strikes, long StampMs, bool StartSampleTimer, bool TreatAsCaptureFailed);

    /// The sampling watchdog's pure two-strike escalation (incident 2026-08-12: a resume path
    /// that only restarted `sampleTimer` when `reader is not null` left sampling permanently
    /// stopped after a camera died inside the restart cooldown while paused). Belt-and-
    /// suspenders for the whole starvation class, including a `SampleAsync` pass hung forever in
    /// `await detector.DetectFacesAsync` with the `sampling` reentrancy guard stuck true — a
    /// stuck guard starves `lastSamplePassMs` exactly like a stopped timer, since the caller only
    /// advances that stamp when a pass actually completes.
    ///
    /// `!expectsSampling`: paused/locked/acquiring time never counts as starvation, and
    /// refreshing the stamp to `nowMs` makes the first check after re-entering a sampling status
    /// measure from ~now — this is also what absorbs a sleep/wake gap while paused, with no
    /// separate wake-detection mechanism.
    ///
    /// First starved check (`strikes == 0`): a cheap self-heal — request `sampleTimer.Start()`
    /// (idempotent if already running) and refresh the stamp, so the next evaluation measures the
    /// self-heal's own effect. A post-sleep monotonic gap costs one harmless self-heal rather than
    /// a spurious restart.
    ///
    /// Second consecutive starved check (`strikes >= 1`): the self-heal did not clear the
    /// starvation, so request capture-failed treatment instead. The caller feeds
    /// `Core.Event.CaptureFailed` through `Advance` — the core's existing flagless
    /// capture-failure handling gates the resulting restart on `RecoveryCooldownMs`, so repeated
    /// watchdog fires while truly stuck converge to one restart per cooldown window, never a
    /// storm. This introduces no new restart authority; it only feeds the one that already exists.
    /// The sampling watchdog's starvation threshold (incident 2026-08-12), fed into
    /// `SamplingWatchdogStep`'s `starvationMs` parameter every watchdog tick: a completed sample
    /// pass is overdue once `nowMs - lastSamplePassMs` reaches this many milliseconds. Ten missed
    /// sample intervals, not one or two -- a single slow pass (a momentarily busy FrameServer, a
    /// slow `DetectFacesAsync` call) must not trip the watchdog; only a pipeline that has gone
    /// quiet for an order of magnitude longer than its own configured cadence counts as starved.
    /// The 15-second floor keeps that 10x multiplier from getting pathologically twitchy at a
    /// fast configured `SampleIntervalMs` -- at the sanitized minimum of 100ms
    /// (`SanitizeSensingConfig`'s own floor), 10x alone would be a 1-second threshold, well
    /// inside ordinary scheduling jitter and camera-pipeline latency, which would false-positive
    /// the watchdog on perfectly healthy sampling. 15s is comfortably longer than every other
    /// timing constant in this codebase that isn't itself policy-configurable (the constructor's
    /// `retryTimer` cadence is 5s), so it holds as an absolute floor regardless of how fast
    /// sampling is configured. Whichever of the two terms is larger wins, so neither a very fast
    /// nor a very slow configured sample interval can push the threshold below a sane minimum or
    /// make it needlessly slow to notice a genuinely wedged pipeline.
    internal static long SamplingStarvationThresholdMs(int sampleIntervalMs) =>
        Math.Max(10L * sampleIntervalMs, 15000L);

    internal static WatchdogVerdict SamplingWatchdogStep(
        bool expectsSampling, long nowMs, long lastSamplePassMs, int strikes, long starvationMs)
    {
        if (!expectsSampling)
            return new WatchdogVerdict(Strikes: 0, StampMs: nowMs, StartSampleTimer: false, TreatAsCaptureFailed: false);

        bool starved = nowMs - lastSamplePassMs >= starvationMs;
        if (!starved)
            return new WatchdogVerdict(Strikes: 0, StampMs: lastSamplePassMs, StartSampleTimer: false, TreatAsCaptureFailed: false);

        if (strikes == 0)
            return new WatchdogVerdict(Strikes: 1, StampMs: nowMs, StartSampleTimer: true, TreatAsCaptureFailed: false);

        return new WatchdogVerdict(Strikes: 0, StampMs: nowMs, StartSampleTimer: false, TreatAsCaptureFailed: true);
    }

    /// Status → tray/status-text mapping (0001-core-brain.md, slice 8b deliverable: "Status →
    /// tray/log mapping table implemented as a single function"). `lastObservationDark` is the
    /// shell's own current-sample dark-vs-no-frame knowledge (Core's `Status.NoSignal` does not
    /// distinguish the two — see "Notes" — the shell already computed the distinction one line
    /// before constructing the `Observation`). Pure and tested independently of `WatcherContext`.
    internal static string StatusText(Core.Status status, bool lastObservationDark) => status.Tag switch
    {
        Core.Status.Tags.Watching => "Watching",
        Core.Status.Tags.NoSignal => lastObservationDark
            ? "Camera dark/blocked — not locking"
            : "No camera frames — not locking",
        Core.Status.Tags.SessionLocked => "Session locked — watching paused",
        Core.Status.Tags.Paused => "Paused — camera kept open",
        Core.Status.Tags.AcquiringCamera => "Starting…",
        Core.Status.Tags.Recovering => "Camera unavailable — retrying",
        _ => throw new UnreachableException(),
    };

    /// Shell-side inhibitor annotation (RFC 0002-environment-levels, "Status"): appended whenever
    /// the inhibitor registry's cached snapshot is active, REGARDLESS of which `Status` row is
    /// showing -- e.g. "Watching · lock inhibited (media-playing)", the RFC's own pinned example.
    /// Both `inhibited` and `activeNames` must be sourced from the registry's own snapshot
    /// (`LockInhibitorRegistry.Active`/`ActiveNames`), never from `Policy.lockInhibited` — the
    /// core projection exists for the verification harness/diagnostics only (see the RFC's
    /// "Status" section for why one annotation needs one owner for both halves). Deliberately a
    /// separate function from `StatusText` rather than folded into its switch: the annotation is
    /// orthogonal to the status row, so it composes with every arm uniformly instead of needing
    /// its own case in an otherwise-exhaustive match.
    internal static string AppendInhibitionAnnotation(string statusText, bool inhibited, IReadOnlyList<string> activeNames) =>
        inhibited ? $"{statusText} · lock inhibited ({string.Join(", ", activeNames)})" : statusText;

    /// Shell-side config-fallback annotation (stage-4 follow-up: "Status annotation when config
    /// makes locking effectively unreachable"): appended whenever `BuildPolicyConfig` rejected
    /// the on-disk policy fields and the built-in defaults are live, regardless of which
    /// `Status` row is showing. The SEC-3 ceilings already make the *behavior* safe (defaults,
    /// never the absurd value); this makes the substitution visible somewhere better than one
    /// log line at load time, exactly the way the SMTC path annotates inhibition. Composes
    /// AFTER `AppendInhibitionAnnotation` (order pinned by test): same orthogonal-to-the-row
    /// design, and a corrected config clears it on the next Settings commit because both
    /// `BuildPolicyConfig` call sites recompute the flag.
    internal static string AppendConfigFallbackAnnotation(string statusText, bool policyDefaultsInUse) =>
        policyDefaultsInUse ? $"{statusText} · config out of range — defaults in use" : statusText;

    /// Shared event-construction function (0001-core-brain.md slice 8a: "Factor each call
    /// site's event construction into a small named function... reused unchanged by 8a's
    /// `ShadowAdvance` and 8b's `Advance`"). The shell already computes `haveFrame`/`dark`/
    /// `present` one line before constructing the `Observation` today — this only names the
    /// mapping, it does not change what's computed.
    internal static Core.Observation ClassifyObservation(bool haveFrame, bool dark, bool present)
    {
        if (!haveFrame) return Core.Observation.NoFrame;
        if (dark) return Core.Observation.DarkFrame;
        return present ? Core.Observation.FaceSeen : Core.Observation.NoFace;
    }

    /// `SampleAsync`'s shared event-construction function — the `ClassifySample(...): Event`
    /// named in the RFC's slice 8a contract. `InputIdleMs` no longer flows through here (RFC
    /// 0002-environment-levels): it lives in `StepInputs`, sourced fresh in `Advance`'s input
    /// assembly, not in the `Sample` payload.
    internal static Core.Event ClassifySample(bool haveFrame, bool dark, bool present) =>
        Core.Event.NewSample(ClassifyObservation(haveFrame, dark, present));

    /// Bug fix (frozen-frame classification): `TryAcquireLatestFrame` can re-serve a cached
    /// frame forever (a known WinRT quirk) — a frame whose `Core.FrameFreshness` verdict is
    /// stale must be classified exactly as if no frame had been acquired at all, not as a live
    /// (possibly `FaceSeen`) observation. `NoFrame` is already the correct fail-open path: it
    /// resets the away baseline and accrues the no-signal clock toward the existing
    /// `NoSignalReportAfter` → `Reevaluate` → process-restart recovery machinery, so this
    /// collapses to a single boolean AND rather than a new recovery path.
    internal static bool EffectiveHaveFrame(bool haveFrame, bool frameIsFresh) => haveFrame && frameIsFresh;

    /// Picks the largest detected face (by pixel area) and normalizes its bounding box to
    /// [0,1] by the frame's pixel dimensions, for `PresenceLock.Core`'s `PresenceFilter`
    /// (spatially-coherent presence stabilization — 0001-core-brain.handoff.md, "Burn-in
    /// incident 2026-07-28"). Takes `BitmapBounds` (a plain WinRT struct, not `DetectedFace`
    /// itself — `DetectedFace` has no public constructor and cannot be instantiated outside a
    /// real `FaceDetector` result, so this signature is the boundary that keeps the selection
    /// logic unit-testable) so the shell need only pass `faces.Select(f => f.FaceBox)`.
    /// Normalized so filter coherence is resolution/crop independent regardless of camera
    /// format or Studio Effects' auto-framing crop changing mid-session. Returns `null` for an
    /// empty list or a degenerate (zero-dimension) frame — both map to "no detection this
    /// frame" at the `PresenceFilter.step` boundary.
    internal static Core.FaceBox? LargestFaceBoxNormalized(IReadOnlyList<BitmapBounds> boxes, uint frameWidth, uint frameHeight)
    {
        if (boxes.Count == 0 || frameWidth == 0 || frameHeight == 0) return null;

        var largest = boxes[0];
        ulong largestArea = (ulong)largest.Width * largest.Height;
        for (int i = 1; i < boxes.Count; i++)
        {
            ulong area = (ulong)boxes[i].Width * boxes[i].Height;
            if (area > largestArea)
            {
                largest = boxes[i];
                largestArea = area;
            }
        }

        return new Core.FaceBox(
            x: largest.X / (double)frameWidth,
            y: largest.Y / (double)frameHeight,
            w: largest.Width / (double)frameWidth,
            h: largest.Height / (double)frameHeight);
    }

    /// Plain (id, display name, enclosure panel) projection of a candidate color camera
    /// (0001-core-brain.md addendum 2026-08-01, slice 10: camera-arrival upgrade) — the shape
    /// `SelectPreferredCamera` ranks over, kept independent of any live WinRT device handle so
    /// the ranking itself is unit-testable. `Id` is the candidate's `MediaFrameSourceInfo.Id`
    /// (unique per color source, and the same key `InitCameraAsync` already used to look up
    /// `capture.FrameSources[...]`); `Panel` is normalized to `EnclosurePanel.Unknown` for a
    /// camera reporting no enclosure location at all (an external USB webcam), exactly as
    /// `InitCameraAsync`'s original inline `SelectionRank` treated a null `EnclosureLocation`.
    internal readonly record struct CameraCandidate(string Id, string DisplayName, EnclosurePanel Panel);

    /// The camera preference ranking (0001-core-brain.md addendum 2026-08-01, slice 10): the
    /// exact ordering `InitCameraAsync` computed inline before this slice — a non-blank user
    /// filter wins outright (first candidate whose display name contains it, case-insensitive,
    /// preserving input order), else external-over-built-in-front (no enclosure location /
    /// `Unknown` ranks best, `Front` next, any other known panel last). Factored out as a pure,
    /// pane-agnostic function so startup selection (`InitCameraAsync`) and the arrival-triggered
    /// `DeviceWatcher` settle check share one implementation rather than re-deriving the
    /// ordering independently. Returns `null` only when `candidates` is empty — the caller's
    /// "no color camera found" is a shell-level concern, not this function's.
    internal static string? SelectPreferredCamera(IReadOnlyList<CameraCandidate> candidates, string nameFilter)
    {
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            foreach (var candidate in candidates)
                if (candidate.DisplayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    return candidate.Id;
        }

        return candidates.OrderBy(c => PanelRank(c.Panel)).First().Id;
    }

    // External USB webcams report no enclosure panel (normalized to Unknown above); prefer
    // them over the built-in camera, which sees only the lid when docked closed.
    static int PanelRank(EnclosurePanel panel) =>
        panel == EnclosurePanel.Unknown ? 0 : panel == EnclosurePanel.Front ? 1 : 2;

    static readonly Core.RestartStamps EmptyRestartStamps = new(wedgeAt: null, reevalAt: null, upgradeAt: null);

    /// The stamps files' home when no override is supplied — the app's real per-user state
    /// dir. The `stateDir` parameters on the three stamps functions below are a test seam
    /// (stage-4 follow-up: `RestartStampsPersistenceTests` wrote the real %LOCALAPPDATA% file
    /// and flaked in the Windows container): tests point them at a per-test temp dir; product
    /// call sites always pass nothing.
    static readonly string DefaultStateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PresenceLock");

    static string StampsPathIn(string? stateDir) =>
        Path.Combine(stateDir ?? DefaultStateDir, "restart-stamps.json");

    /// The file the JSON stamps file supersedes (0001-core-brain.md, "Restart stamps" / R2-34):
    /// a plain-text single wall-clock timestamp, replaced by the per-`RestartReason` JSON.
    /// Deleted the first time `SaveRestartStamps` runs on an upgraded build — see there.
    static string LegacyRestartStampPathIn(string? stateDir) =>
        Path.Combine(stateDir ?? DefaultStateDir, "last-restart.txt");

    /// Reads the wall-clock stamps file (0001-core-brain.md, "Restart stamps") if present, else
    /// returns empty stamps — any read/parse error also yields empty, since a parse failure
    /// must never block recovery. Feeds `Policy.start` at every startup; the counterpart writer
    /// is `SaveRestartStamps`, called only from `RestartProcess()` immediately before spawn.
    internal static Core.RestartStamps LoadRestartStamps(string? stateDir = null)
    {
        var stampsPath = StampsPathIn(stateDir);
        try
        {
            if (File.Exists(stampsPath))
            {
                var dto = JsonSerializer.Deserialize<RestartStampsDto>(File.ReadAllText(stampsPath));
                if (dto is not null)
                    return new Core.RestartStamps(wedgeAt: dto.WedgeAt, reevalAt: dto.ReevalAt, upgradeAt: dto.UpgradeAt);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"restart stamps load failed: {ex.Message}");
        }
        return EmptyRestartStamps;
    }

    /// Writer counterpart to `LoadRestartStamps` (0001-core-brain.md, "Restart stamps" /
    /// R1-29): persists both stamp fields verbatim (Policy.step itself only ever bumps the one
    /// matching the reason it fired, so a plain round-trip here can't poison the other reason's
    /// cooldown) plus the paused flag, in one JSON write. Called only from `RestartProcess()`,
    /// immediately before spawn — never on any other path (R2-11's pinned lifecycle: the flag
    /// is written only here and consumed-and-cleared by `ConsumePersistedPausedFlag`). A write
    /// failure must not block the restart itself, so it is caught and logged, not thrown.
    internal static void SaveRestartStamps(Core.RestartStamps stamps, bool paused, string? stateDir = null)
    {
        var stampsPath = StampsPathIn(stateDir);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stampsPath)!);
            var dto = new RestartStampsDto
            {
                WedgeAt = stamps.WedgeAt,
                ReevalAt = stamps.ReevalAt,
                UpgradeAt = stamps.UpgradeAt,
                Paused = paused,
            };
            File.WriteAllText(stampsPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"restart stamps save failed: {ex.Message}");
        }

        // Migration cleanup (0001-core-brain.md, "Restart stamps" / R2-34): last-restart.txt is
        // superseded by the stamps file above; delete it here, the first time this method runs
        // on an upgraded build. File.Delete is already a silent no-op when the path is absent
        // (true for every call after the first), so no separate "have we done this yet" state
        // is needed. A cleanup failure must never block the restart this method exists to
        // support, so it is caught and logged, not thrown.
        try
        {
            File.Delete(LegacyRestartStampPathIn(stateDir));
        }
        catch (Exception ex)
        {
            Log.Write($"legacy restart stamp cleanup failed: {ex.Message}");
        }
    }

    /// Paused-flag consume-and-clear (0001-core-brain.md R2-11 / RFC 0002-environment-levels
    /// "Pause ownership", pinned lifecycle): reads the persisted flag; if set, immediately
    /// rewrites the file with it cleared (stamps untouched) and returns true, which the caller
    /// assigns directly to its `paused` mirror before building the initial `StepInputs` for
    /// `Policy.start` — inheritance is ordinary input passing now, not an event refeed. Returns
    /// false — and touches nothing on disk — when absent/false/unparseable, so a normal launch
    /// or a tray Exit never inherits a stale pause, and a parse failure never blocks startup.
    /// Called exactly once, from `WatcherContext`'s constructor.
    internal static bool ConsumePersistedPausedFlag(string? stateDir = null)
    {
        var stampsPath = StampsPathIn(stateDir);
        try
        {
            if (!File.Exists(stampsPath)) return false;
            var dto = JsonSerializer.Deserialize<RestartStampsDto>(File.ReadAllText(stampsPath));
            if (dto is null || !dto.Paused) return false;

            var cleared = new RestartStampsDto
            {
                WedgeAt = dto.WedgeAt,
                ReevalAt = dto.ReevalAt,
                UpgradeAt = dto.UpgradeAt,
                Paused = false,
            };
            File.WriteAllText(stampsPath, JsonSerializer.Serialize(cleared, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"paused-flag consume failed: {ex.Message}");
            return false;
        }
    }

    /// SEC-4 (reframed): the persisted pause flag survives a deliberate self-restart by design
    /// (RFC 0002-environment-levels, "Pause ownership") -- but the stamps file backing it
    /// (under `DefaultStateDir`) is an ordinary same-user readable/writable file, so a forged or corrupted
    /// `Paused:true` entry would otherwise start the app paused with only the tray text as a
    /// cue. Same-user tampering can't be prevented (this app's own threat model -- see
    /// CLAUDE.md), so an inherited-pause startup gets exactly one loud WARNING line instead of
    /// silence. Pure and named, mirroring `SuppressionTransitionVerdict.TransitionLogMessage`'s
    /// null-means-no-log contract, so the trigger condition (and only the trigger condition) is
    /// unit-tested independently of the WinForms constructor that calls it.
    internal static string? StartupPauseInheritedWarning(bool startupPaused) =>
        startupPaused
            ? "WARNING: starting PAUSED from persisted restart-stamps state -- if this was not " +
              "expected (no deliberate Pause before the last self-restart), the stamps file may " +
              "have been tampered with or corrupted"
            : null;

    sealed class RestartStampsDto
    {
        public long? WedgeAt { get; init; }
        public long? ReevalAt { get; init; }
        // RFC addendum 2026-08-01, slice 10: absent on an old two-field stamps file, which
        // deserializes it as null — exactly how WedgeAt/ReevalAt already behaved before their
        // first restart of either reason had ever fired. Back-compat by construction.
        public long? UpgradeAt { get; init; }
        public bool Paused { get; init; }
    }
}

/// The session-lock mirror (RFC 0002-environment-levels, "Session mirror with reconciliation",
/// DES-2): owns the three fields that used to be loose `WatcherContext` state --
/// `sessionLocked`/`ticksSinceLastSessionSwitchMirrorChange`/`sessionLockDisagreementCount` --
/// behind the two call sites that ever mutate them after construction (`OnSessionSwitch`'s live
/// SessionLock/SessionUnlock delivery, and the watchdog tick's WTS reconciliation), mirroring
/// `LockInhibitorRegistry`'s precedent one slice later: "these fields change together, only from
/// these places" stops being a convention documented in prose and becomes a structural fact once
/// one type is the only thing that can write them. Delegates every actual decision to the
/// existing pure `PolicyBridge.SessionSwitchTicksAfterDelivery`/`PolicyBridge.ReconciliationStep`
/// unchanged -- this type is field ownership, not new policy, so its externally observable
/// behavior is exactly the pre-extraction inline code, byte-for-byte. `Locked` is read fresh by
/// `BuildStepInputs`/`Advance` at any cadence, exactly like the old bare field; mutated only from
/// the UI thread, by the same two call sites (`OnSessionSwitch`'s `ui.Post` continuation and the
/// watchdog `Tick` handler) that already carried the threading invariant `Advance` asserts (DES-1)
/// -- this type adds no locking of its own because it needs none under that discipline.
internal sealed class SessionLockMirror
{
    // Ticks since the last SessionSwitch-driven update that actually CHANGED `Locked` (round-3
    // boundary rule 2's skip window; see `PolicyBridge.SessionSwitchTicksAfterDelivery`). Starts
    // at 1 (not 0): at process start no SessionSwitch has fired at all, so the very first
    // watchdog tick must not be treated as "adjacent to a session switch."
    int ticksSinceLastSessionSwitchMirrorChange = 1;
    // Consecutive watchdog ticks the WTS query has disagreed with `Locked`
    // (`PolicyBridge.ReconciliationStep`'s two-tick hysteresis counter; round-3 boundary rule 1
    // holds this across a failed query rather than resetting it).
    int disagreementCount;

    public bool Locked { get; private set; }

    /// Seeds `Locked` from the constructor's synchronous startup WTS query
    /// (`WatcherContext.AssembleStartupInputs`) -- fail-open, so a failed startup query leaves
    /// this at its safe default, false, via the caller's own fail-open `WtsLockQueryResult`
    /// handling; this constructor does not itself re-derive fail-open behavior.
    public SessionLockMirror(bool initialLocked) => Locked = initialLocked;

    /// The live `SessionLock`/`SessionUnlock` delivery path (`WatcherContext.OnSessionSwitch`):
    /// sets `Locked` to the delivered value and advances the skip-window counter via
    /// `SessionSwitchTicksAfterDelivery` -- reset to zero only when this delivery actually
    /// changed the mirror, per that function's own contract (an idempotent duplicate
    /// `SessionSwitch` re-delivery, which Windows demonstrably produces, must leave the counter
    /// running). Returns whether this delivery actually changed `Locked`, mirroring
    /// `LockInhibitorRegistry.Refresh`'s and `ReconcileResult.CorrectionApplied`'s own
    /// changed-flag precedent (SHELL-1): the caller gates its log line and
    /// `Advance(Core.Event.Reconcile)` on this return value, so a duplicate/idempotent
    /// `SessionSwitch` delivery -- a genuine no-op for this mirror -- does no redundant work,
    /// matching the other two mirror-changing call sites (WTS reconciliation gates on
    /// `CorrectionApplied`; inhibitor aggregation gates on `Refresh()`'s own `changed`). A
    /// delivery that DOES change `Locked` still always returns true, so the caller's
    /// suppression-exit transition (filter reset, sample-timer restart, `KickAcquisitionIfNeeded`
    /// -- all riding `Advance`'s internal transition rule) is never skipped for a genuine
    /// transition, only for a true repeat.
    public bool OnSessionSwitch(bool locked)
    {
        bool changed = Locked != locked;
        Locked = locked;
        ticksSinceLastSessionSwitchMirrorChange =
            PolicyBridge.SessionSwitchTicksAfterDelivery(changed, ticksSinceLastSessionSwitchMirrorChange);
        return changed;
    }

    /// One watchdog-tick WTS reconciliation evaluation: delegates the decision to
    /// `PolicyBridge.ReconciliationStep` unchanged, over this instance's own `Locked` and
    /// counters, then applies the verdict -- correcting `Locked` only when `CorrectionApplied`.
    /// The skip-window tick counter always advances exactly once per call, AFTER
    /// `ReconciliationStep` has already read the pre-increment value -- matching the field
    /// comment this replaces ("advanced here, once per tick, after this tick's own
    /// reconciliation already read the pre-increment value"). Returns the old/new mirror values
    /// so the caller's WARNING log line reads identically to before extraction; the caller
    /// remains responsible for the log write itself and for `Advance(Core.Event.Reconcile)` on a
    /// correction, for the same reason `OnSessionSwitch` leaves `Advance` to its caller.
    public ReconcileResult Reconcile(bool querySucceeded, bool queriedLocked)
    {
        bool before = Locked;
        var verdict = PolicyBridge.ReconciliationStep(
            mirror: Locked,
            queriedLocked: queriedLocked,
            querySucceeded: querySucceeded,
            ticksSinceLastSessionSwitchMirrorChange: ticksSinceLastSessionSwitchMirrorChange,
            consecutiveDisagreementCount: disagreementCount);
        disagreementCount = verdict.DisagreementCount;
        if (verdict.CorrectionApplied) Locked = verdict.Mirror;
        ticksSinceLastSessionSwitchMirrorChange++;
        return new ReconcileResult(verdict.CorrectionApplied, before, verdict.Mirror);
    }

    /// Verdict of one `Reconcile` call: `OldLocked`/`NewLocked` are only meaningful (and only
    /// ever differ) when `CorrectionApplied` is true -- the caller's WARNING log line reads
    /// `{OldLocked} -> {NewLocked}` exactly as the pre-extraction inline code did.
    internal readonly record struct ReconcileResult(bool CorrectionApplied, bool OldLocked, bool NewLocked);
}

/// One named, cached lock-inhibitor level (RFC 0002-environment-levels, "Inhibitor registry"):
/// "the entire extension surface for future gates (presentation mode, quiet hours, on-battery
/// profiles) -- one named, cached level per provider." `Active` is read by `Advance` at any
/// cadence -- cheap, no I/O; `Refresh()` is called ONLY from the watchdog tick and performs the
/// (possibly I/O-bound) `query`. Fail-open by contract: a throwing query (WinRT/COM reads can
/// die with a zombie session -- the RFC's own named failure mode) is caught, logged rate-limited
/// (the WTS-reconciliation precedent -- see Program.cs's `QuerySessionLocked`), and reported
/// inactive. "Assume uninhibited" is the safe direction for an inhibitor -- "assume active
/// forever" would be the silent-cannot-lock trap in new clothes, and an escaped exception would
/// take down the entire watchdog tick handler on the UI thread, WTS reconciliation and the
/// sampling watchdog with it. A distinct top-level type rather than a nested `PolicyBridge`
/// member: unlike everything else in that file, this class is stateful (mutable cached `Active`),
/// not a pure function -- housing it in the static helper class would blur that distinction.
internal sealed class LockInhibitor(string name, Func<bool> query)
{
    public string Name { get; } = name;
    public bool Active { get; private set; }

    public void Refresh()
    {
        try
        {
            Active = query();
        }
        catch (Exception ex)
        {
            Active = false;
            // Keyed (not message-keyed) rate limiting, same reasoning as the WTS-query failure
            // line: a query's exception message can vary run to run, and keying on raw text
            // would let that variation defeat the rate limit entirely.
            Log.WriteRateLimited($"inhibitor-{Name}", $"inhibitor {Name} query failed: {ex.Message}");
        }
    }
}

/// The aggregate over every registered `LockInhibitor` (RFC 0002-environment-levels, "Inhibitor
/// registry"): `Active` iff any provider is active; `ActiveNames` lists which, in provider order.
/// A plain data carrier -- see `LockInhibitorAggregation.Compute` for the pure logic that
/// produces one and `LockInhibitorRegistry` for the cached snapshot `Advance`/`StatusText` read.
internal readonly record struct InhibitorAggregate(bool Active, IReadOnlyList<string> ActiveNames);

/// Pure aggregation/change-detection (RFC 0002-environment-levels, round 3): extracted out of
/// `LockInhibitorRegistry.Refresh` specifically so it has the same independent, dedicated unit
/// coverage as `ReconciliationStep`/`SamplingWatchdogStep` -- the house precedent this RFC's own
/// new code follows. `PresenceLock.Tests` targets `Compute` directly with plain
/// `(string Name, bool Active)` tuples, no `LockInhibitor`/`Func&lt;bool&gt;` fakes needed;
/// `Refresh` below keeps only the I/O fan-out (`LockInhibitor.Refresh` calls) and the field
/// writes.
internal static class LockInhibitorAggregation
{
    internal static (InhibitorAggregate Aggregate, bool Changed) Compute(
        IReadOnlyList<(string Name, bool Active)> providers, bool previousActive)
    {
        var names = providers.Where(p => p.Active).Select(p => p.Name).ToList();
        bool active = names.Count > 0;
        return (new InhibitorAggregate(active, names), active != previousActive);
    }
}

/// Fans out `Refresh()` over every registered provider, computes the aggregate via
/// `LockInhibitorAggregation.Compute`, and reports whether the aggregate changed this tick (RFC
/// 0002-environment-levels, "Inhibitor registry"). Called ONLY from the watchdog tick's
/// `WatchdogTickStep` middle step; `Advance`/`StatusText` read `.Active`/`.ActiveNames` at any
/// cadence off this cached snapshot -- the cached/slow-cadence discipline lives in this type,
/// not in prose (a bare `(string Name, Func&lt;bool&gt; IsActive)[]` registry was considered and
/// rejected per the RFC: a live-query shape gives no cue that SMTC must not be queried on every
/// sample tick).
internal sealed class LockInhibitorRegistry(IReadOnlyList<LockInhibitor> providers)
{
    public bool Active { get; private set; }
    public IReadOnlyList<string> ActiveNames { get; private set; } = [];

    public bool Refresh()
    {
        foreach (var p in providers) p.Refresh();
        var (agg, changed) = LockInhibitorAggregation.Compute(
            [.. providers.Select(p => (p.Name, p.Active))], Active);
        (Active, ActiveNames) = (agg.Active, agg.ActiveNames);
        return changed;
    }
}
