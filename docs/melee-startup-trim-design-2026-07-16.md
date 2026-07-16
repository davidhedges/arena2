# Melee Startup Trim Design

Date: 2026-07-16

## Decision

Single-clip melee attacks may author `startupTrimSeconds` on their existing
`CombatAnimationSet` attack entry. The value moves the beginning of the played
presentation forward without modifying or duplicating the source animation
clip.

`OnStrikeHit` remains stamped on the physical contact pose in the source clip.
The effective gameplay timeline subtracts the resolved startup trim:

```text
effective_event_seconds = max(0, authored_event_seconds - startup_trim_seconds)
playback_start_normalized = startup_trim_seconds / clip_length_seconds
```

The resolved trim is clamped to the first `OnStrikeHit` event. Authoring beyond
first contact is invalid. A trim exactly equal to first contact is valid and
produces a zero-delay hit.

## Ownership

- `CombatAnimationSet` owns startup trim beside the single-clip melee
  presentation it modifies.
- The Event Stamper continues to own the physical `OnStrikeHit` marker.
- Hit-window mirroring and melee-manifest export consume effective event times.
- Local playback, remote catch-up, and predicted contact cues consume the same
  resolved trim/effective first-hit time.
- Recovery remains independently authored gameplay timing and is not reduced by
  startup trim.

## Authoring Workflow

1. Stamp `OnStrikeHit` on the frame where the weapon visually contacts.
2. In the Event Stamper, scrub the preview to the frame where playback should
   begin and click **Set Start Here**. The selected animation clip is the whole
   authoring context; the tool updates every compatible melee attack that uses
   it. Use **Trim to Contact** for an immediate hit or **Remove Trim** to restore
   the full opening. The numeric field remains available for exact entry.
3. Keep some anticipation by choosing a frame before contact, or set trim equal
   to the first hit event for an immediate hit/follow-through presentation.
4. The Event Stamper saves the `CombatAnimationSet`, mirrors the effective hit
   windows, and updates the affected server-manifest strikes immediately through
   the same synchronization path used by `OnStrikeHit` edits. The
   `CombatAnimationSet` inspector exposes the same field and uses a short
   debounce before performing that synchronization.
5. Republish the catalog before expecting the server timing to change live.

When the selected clip has an `OnStrikeHit` event, the Event Stamper also shows
an informational estimate for input to first authoritative server damage. The
estimate uses the exported effective first-hit delay, an idealized 20–40 ms
input-to-server leg, and 0–33 ms of alignment to the current combat tick. It is
not a validation rule or a measured connection value. Queued combo execution,
gap-close arrival, projectile travel, server stalls, and replication back to the
client can add time beyond the displayed direct-melee estimate.

## V1 Scope

- Supported: single-clip player melee and auto attacks using that strike as
  their visual source.
- Unsupported: phased melee, playback-speed/time-warp curves, clip mutation,
  predicted health/damage numbers, and recovery retiming.
- Compatibility: the default trim is zero, so all existing animation sets keep
  their current playback and exported timing until explicitly authored.

## Validation

- Startup trim must be non-negative.
- The attack must be single-clip and have an authored `OnStrikeHit` event.
- Startup trim must not exceed the first authored hit event.
- A zero effective hit delay remains a valid hit window and still schedules the
  cosmetic predicted contact layer.
