# RFC: SMTC false-positive hardening — attention-aware media inhibition

Status: Seed (problem statement only; not designed, not sliced) — spun out of
0002-environment-levels.md's stage-4 review follow-ups, 2026-09-03.
Depends on: 0002-environment-levels.md (the media inhibitor this refines).

## Problem

The v1 media inhibitor (0002, slice 5) treats any SMTC session reporting `Playing` as "media
is playing, don't lock". Two false-positive classes inhibit the lock while nobody is actually
watching anything:

- **Muted or background autoplay**: a forgotten tab or app auto-playing to no audience keeps
  the machine unlocked indefinitely.
- **Phantom sessions**: a same-user process can register an SMTC session and report `Playing`
  forever without emitting audio at all. (Within the app's threat model this is the same-user
  actor 0003-defeat-resistance-layering.md covers — the OS floor still locks eventually — but
  the honest-bug version of a stuck `Playing` session is real regardless.)

Accepted as a known limitation at 0002's stage-4 review; the v1 mitigations are the
`MediaInhibitorEnabled` kill switch and the always-visible status annotation, so a
wrongly-inhibited lock is at least never silent.

## Direction to evaluate (not committed)

Cross-check `Playing` against evidence the media is actually reaching an audience, e.g. WASAPI
audio-session peak metering (is the session emitting non-silent audio through an unmuted
endpoint?). Open questions for a real stage 1:

- Does per-session WASAPI metering reliably attribute audio to the SMTC session's process,
  including browsers that split playback across renderer processes?
- Polling cost at watchdog cadence, and whether metering belongs inside the existing provider
  `Refresh()` or as its own provider composed alongside it.
- Threshold semantics: silent-for-how-long before `Playing` stops inhibiting, and does that
  timer live in the provider (shell) or as a core-visible input?
- Whether "video playing with audio muted deliberately" (captions viewer) is a case worth
  preserving — attention evidence beyond audio may be out of scope entirely.

## Non-goals

- Defending against a deliberately adversarial same-user process (0003 owns the layering
  answer; this RFC only narrows honest false positives).
- Teams/Zoom presentation detection — still the separate future presentation-provider idea
  from 0002's review, not this RFC.
