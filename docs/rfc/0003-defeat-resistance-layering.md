# RFC: Defeat-resistance layering — OS inactivity-lock backstop + crash-survival watchdog

Status: Draft (stage-1 RFC + slicing, not yet sliced; architecture review not yet run) —
spun out of 0002-environment-levels.md's stage-4 review follow-up, 2026-09-01.
Depends on: 0001-core-brain.md and 0002-environment-levels.md. Purely additive — changes no
existing core or shell contract; PresenceLock's decision core is untouched.

## Motivation

0002's stage-4 review surfaced same-user termination/tamper: a process running as the user
can kill PresenceLock or edit the files it reads (config, restart stamps). The stage-4 fixes
made every such failure *loud* (mutex-collision log, startup-inherited-pause warning, config
upper-bound rejection) but cannot *prevent* it — a file both the app and a same-user actor
can read and write is not cryptographically defensible, and a process can always be
terminated by another at the same integrity level.

The tempting response — an app-level supervising/integrity service that guards PresenceLock —
is the **wrong** response, and this RFC exists partly to record why so it isn't re-proposed:

- PresenceLock's threat model is **physical access by a non-user** (shoulder-surfer, someone
  at the unattended desk). **Same-user code is already inside the boundary the screen lock
  defends.** A physical attacker who can kill the app already holds the unlocked session the
  lock would otherwise deny; malware with same-user execution already has the session's files,
  keystrokes, and tokens. Self-protection defends a boundary the product doesn't own.
- A silent respawning watchdog would make the *persistence/evasion* case **worse**: the loud
  failures 0002 added exist so the **user notices** their lock tool stopped; a silent respawn
  hides exactly the tampering it claims to counter.
- An elevated integrity service can't win against an admin user anyway, and adds a privileged
  attack surface of its own.

What *does* have value is two separable things the "service" framing conflated:

1. **Defeat-resistance belongs in the OS, not the app.** A winlogon / Group-Policy
   machine-inactivity lock runs below any userland process and cannot be killed by same-user
   code. That is the real enforcement floor.
2. **Crash-survival is a reliability concern, not anti-tamper.** An *accidental* death (bug,
   driver hiccup, OOM) silently stops guarding future away-windows. Surviving that is
   legitimate — but it is reliability, and must not be dressed up as tamper-resistance.

Intended layering: **OS inactivity lock = the enforcement floor; PresenceLock = the smart,
face-presence fast-path on top** — a latency/UX win over a fixed idle timer, never a
tamper-proof enforcement layer.

## Proposed direction (sketch — pre-review)

- **OS inactivity-lock backstop.** Provision a machine-inactivity lock (GPO
  `InactivityTimeoutSecs` / secpol, or equivalent) as the floor. Its timeout must sit *above*
  PresenceLock's away threshold so the smart path normally wins, but low enough that a dead
  app degrades to a bounded OS lock rather than to "unprotected."
- **Restart-only crash-survival watchdog.** A lightweight restart-only mechanism (Scheduled
  Task — logon + periodic — is the leading candidate; the MSIX `startupTask` already covers
  logon). Constraints: restart-only, unprivileged, and it must keep failures **visible** (no
  silent respawn that masks the loud-failure signals from 0002).

## Load-bearing property (candidate — confirm in stage 1/2)

After PresenceLock stops for *any* reason, the machine still locks on inactivity within a
bounded time — protection degrades to the OS floor, never to "unprotected." The OS backstop is
the slice that must land first (it makes the property true without the app), matching the
flow's "produce the load-bearing property in slice 1" rule.

## Non-goals

- An app-level supervising / integrity-checking service that protects PresenceLock from the
  same user who installed it (wrong boundary; privileged attack surface; theater against admin).
- Any attempt to make same-user file tamper cryptographically impossible.
- Changes to the decision core, camera lifecycle, or the 0002 level/inhibitor contracts.

## Open questions (stage 1 — TODO before slicing)

- Exact OS mechanism on SLS2 (GPO vs secpol vs scheduled `rundll32 user32,LockWorkStation`)
  and how it interacts with the deliberately Windows-Hello-disabled posture ([[preference_no_windows_hello]]).
- Watchdog host: Scheduled Task vs a real service; how to guarantee restart-only and keep it
  from becoming the very self-protection service this RFC rejects.
- Relationship between the OS floor timeout and PresenceLock's away threshold — the floor must
  be ≥ the app's normal lock latency (so the fast path wins in the common case) yet bounded.
- Is the loud-failure surface from 0002 (missing tray icon + log line) sufficient as the
  "visible" requirement, or does the watchdog need an explicit health signal?
- Does this warrant touching the installer/packaging to provision the OS backstop, or is that
  a documented manual host step (consistent with this repo's host-only, no-Settings-UI habits)?

## Slices

TODO — stage 1 is not complete until this RFC is sliced (OS backstop first, per the
load-bearing-property rule). Not yet done.
