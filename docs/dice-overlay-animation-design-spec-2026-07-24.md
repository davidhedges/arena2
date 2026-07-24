# Dice Overlay Animation — Design Specification

Status: design specification only. No implementation, asset production,
game-event system, reward system, or UI integration is in scope.

## Goal

Create polished, non-interactive dice-roll presentations for UI events such as
reward screens. The dice are visual overlays, not gameplay objects. Supported
dice are d4, d6, d8, d10, d12, and d20, with a percentile d10 expected later.

The required experience is a trustworthy dice roll that can be shown, skipped,
dismissed, and read. Future systems may use a roll to determine an event, a
reward, or another outcome, but those systems are outside this specification.

## Core rule: resolve first, present second

The roll result and its visual presentation are separate systems:

1. The authoritative game logic resolves the roll.
2. That result is recorded before it is shown.
3. The overlay receives the resolved value and animates toward the matching
   face.
4. Physics, animation selection, skipping, dismissal, frame rate, and client
   reconnection can never alter the result.

This separation makes the roll fair and keeps the presentation free to favor
polish over physical simulation.

## Fair result generation

### Authority

- Any roll that can affect an event, reward, inventory, progression, combat, or
  other shared game state is generated on the SpacetimeDB server.
- Use the authoritative server platform's supported uniform random-number
  source, not Unity animation, animation physics, timestamps, identity hashes,
  or a client-supplied value.
- Every face has equal probability.
- Each die in a future multi-die request receives an independent draw.
- Local random generation is allowed only in an explicitly non-gameplay
  preview tool. A preview result must never be accepted as an authoritative
  outcome.

The project uses SpacetimeDB, whose reducer context provides server-side,
deterministic, reproducible, platform-seeded randomness. This specification
requires fair authoritative game randomness; it does not claim casino-style
"provably fair" randomness.

### One roll, one outcome

- The server validates that the triggering event is allowed to roll.
- A logical event receives an idempotent roll identity. Repeating the same
  request, reconnecting, skipping, or reopening the presentation returns the
  same resolved roll rather than drawing again.
- When a future roll determines an event or reward, the roll and its outcome
  are committed in the same server transaction. Either both commit or neither
  does.
- The client receives only committed results and cannot submit or override the
  resulting face.
- Cosmetic trajectory selection uses a separate client-only random stream. It
  does not consume or influence gameplay RNG.

### Fairness acceptance requirements

The completed result system must demonstrate:

- every returned value being within `1..=faces`
- d4, d6, d8, d10, d12, and d20 using the requested number of faces
- batches returning the requested number of independent results
- the same logical event being unable to obtain a second roll
- client-supplied values being rejected or ignored
- roll and future outcome data committing atomically
- no obvious distribution bias across a large automated sample

A statistical smoke test is a regression detector, not proof that any short
sequence must look evenly distributed. Legitimate random rolls can contain
streaks and repeated values.

If rolls ever carry real-money stakes or require public proof against the server
operator, a separate commit-reveal or verifiable-randomness design will be
needed. That is intentionally outside the current game requirement.

## Result behavior

- The result is known before animation begins.
- The final visible face exactly matches the authoritative result.
- The overlay cannot ask the result generator to reroll.
- The final die remains visible until the host presentation is dismissed.
- The surrounding UI does not wait for the moving animation to finish.
- The first scope assumes one die at a time.
- The eventual system supports multiple dice, but a roll contains only one die
  type.

## Motion and interaction

- Use authored trajectories with small randomized visual variations and a
  deterministic final orientation.
- Physics may inform secondary motion but never determines the result.
- Begin with three main trajectories, varying spin axis, direction changes, and
  effect timing.
- The die tumbles through controlled magical suspension with one or two smooth
  direction changes. There is no visible or implied hard floor impact.
- The camera remains fixed.
- The presenter owns responsive movement within the supplied region or, by
  default, the full overlay.
- The die may temporarily cover UI controls or text.
- The player may tap/click to skip the moving portion. Skip transitions
  immediately and cleanly to the correct final pose.
- The final pose is near the center of the active region with the result face
  aimed toward the camera.
- The held result does not continue rotating. It may use extremely subtle resin
  shimmer or breathing emissive motion.
- Dismissing the host presentation dismisses the dice overlay. A separate
  dice-only dismissal lifecycle is not required initially.

Initial timing:

- anticipation: 0.2–0.4 seconds
- moving roll: 1.0–1.5 seconds
- result hold: indefinite, until dismissal

## Visual direction

- Style: medieval, stylized fantasy, and magical.
- The dice feel fantastical rather than like ordinary manufactured tabletop
  dice.
- Initial set: classic translucent dark-red resin.
- Numerals: shallow engraved with warm ivory or muted ember-gold inlay.
- Typeface: start with the project's Cinzel display face and verify it on every
  die, especially the d4.
- The geometry, material, and motion should look clean and polished.
- Magic remains restrained during the tumble and concentrates into a short
  result flourish.
- The first release has one universal set. Additional themed or unlockable sets
  may be added later.
- No faction-, rarity-, character-, or reward-specific colors are required.

### Result effects

Ordinary result:

- d20 values `2–19`
- any non-d20 value below that die's maximum
- subtle warm settle pulse only

Positive special result:

- d20 `20`
- the maximum value on d4, d6, d8, d10, and d12
- short ember-gold flare, brief rune halo, and a small burst of upward sparks

Negative special result:

- d20 `1` only
- short dark-crimson pulse with brief inward/downward motes
- numeral readability is preserved

There are no screen flashes, strong luminance changes, camera shake, UI shake,
or distortion. The effects are simple flourishes around the die.

## Faces and typography

- Only the final face must be readable.
- All dice use numerals, including the d6.
- All dice share one typeface and numbering treatment.
- The d10 displays `10`, not `0`.
- The future percentile d10 displays `00` through `90`.
- The d4 uses one centered numeral per face so that the presented face directly
  shows the result. It does not use a traditional corner/edge-reading numbering
  scheme.
- No separate UI text repeats the final value.
- No special 6/9 disambiguation is required.

## Presentation constraints

- Target desktop layouts; mobile support is not required.
- The animation appears on a transparent overlay with no visible surface.
- It works in portrait, landscape, and differently sized overlay regions.
- The dice presentation owns its responsive travel path and final placement
  within the available region.
- Starting a roll has no perceptible loading pause.
- A roll does not create a visible gameplay hitch or frame-rate drop.
- Exact rendering architecture, resolution, lighting implementation, loading
  strategy, and performance budgets are intentionally left to later technical
  design.

## Audio, haptics, and accessibility scope

- Sound is deferred from the first visual implementation but will be added.
- Future materials may receive distinct sound sets; decide this when a second
  material actually exists.
- Haptics are not required.
- Reduced-motion behavior is not part of the initial scope.
- Flashing, strong shake, and aggressive luminance changes are not permitted.

## Future multiple-dice behavior

- The maximum simultaneous dice count remains intentionally deferred.
- Dice share one motion region but settle into distinct final slots.
- Every result is simultaneously readable.
- Dice do not collide with each other.
- Dice reveal together.
- Skip finishes the whole group at once.
- Dismissal removes the whole group.

These choices do not expand the initial single-die scope; they only record how
the presentation should behave when multiple-dice design is revisited.

## Reference presentation

The d20 is the reference die for evaluating the design because it exercises the
largest result set, the most recognizable silhouette, and both positive and
negative special outcomes.

The reference presentation consists of:

- one translucent dark-red resin d20
- a fixed camera and transparent background
- three visually distinct authored motion paths
- deterministic presentation of every result from `1–20`
- ordinary, `1`, and `20` reveal treatments
- a result-facing final pose held until dismissal
- skip-to-result behavior
- an authoritative, uniform server-generated result
- no perceptible loading pause

## Overall acceptance criteria

- Every supported die can present every legal result.
- Every final face matches the resolved authoritative value.
- Each face has equal generation probability.
- Animation and physics cannot affect the value.
- The final numeral is immediately readable without separate UI text.
- Skipping reaches the same correct final pose.
- The final pose remains visible until dismissal.
- Ordinary, maximum, d20 `1`, and d20 `20` treatments follow this
  specification.
- The overlay has no visible surface and remains visually coherent across its
  supported shapes and aspect ratios.
- Motion, resin, typography, lighting, and effects read as one polished
  medieval-fantasy dice set.
- The presentation begins without perceptible loading and does not visibly
  disrupt the underlying game.

## Non-goals

- interactive or gameplay-physical 3D dice
- event, reward, inventory, progression, or combat outcome design
- a casino-style publicly verifiable randomness protocol
- mobile support
- additional dice sets or materials
- first-pass sound design
- reduced-motion behavior
- final multiple-dice capacity

## References

- [SpacetimeDB reducer context and RNG](https://spacetimedb.com/docs/functions/reducers/reducer-context/)
- [SpacetimeDB transactions and atomicity](https://spacetimedb.com/docs/databases/transactions-atomicity/)
