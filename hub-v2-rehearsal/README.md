# Combat Build v2 Hub rehearsal module

This module exists only for the approved Combat Build v2 Phase 2 rehearsal. It
owns no identity, matchmaking, armor, ticket, or gameplay data and must never be
published over the canonical `arena-hub-local` database.

The Phase 2 probe publishes it under a unique disposable local database name,
runs one anonymous save/reload/rejection transaction sequence, captures
evidence, and deletes that database.
