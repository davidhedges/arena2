# Combat Build v2 Hub rehearsal module

This module exists only for the approved Combat Build v2 Hub rehearsals. It
owns no canonical identity, matchmaking, armor, ticket, or gameplay data and
must never be published over the canonical `arena-hub-local` database.

The Phase 2 probe publishes it under a unique disposable local database name,
runs one anonymous save/reload/rejection transaction sequence, captures
evidence, and deletes that database.

The Phase 3 probe reuses the aggregate under another unique disposable Hub
identity, freezes exact canonical v2 snapshot bytes into a rehearsal-only
ticket row, hands those bytes to disposable PvP/open-world rehearsal modules,
and deletes every identity.
