# 10. Content-addressed chunk keys

Date: 2026-09-11

## Status

Accepted

## Context

The prior art's chunk key is `documentId#index` — an ordinal appended to the
document id. Inserting or removing a chunk mid-document shifts every
following ordinal, which re-embeds the entire tail of the document on any
single insertion: a one-paragraph edit near the top of a 500-chunk document
re-embeds up to 499 chunks whose content never changed.

Pure content addressing — hashing the chunk's text alone — fixes that but
breaks on a repeated paragraph: two chunks with identical content collide on
one key, and the second silently overwrites the first.

## Decision

Chunk identity is `xxHash128(sourceId, documentId, chunkContentHash,
duplicateOrdinal)`. `duplicateOrdinal` disambiguates repeated content within
one document; it is **stored** alongside the chunk but is **not** part of
identity beyond that role — a chunk's key does not change when a preceding,
differently-hashed chunk is inserted or removed.

## Consequences

Inserting a new paragraph at the top of a 500-chunk document costs one
embedding (for the new chunk) plus 499 ordinal/offset **repairs** to the
chunks that shifted — and the repairs are not free. SP1's `"<fts>_au"`
trigger is `AFTER UPDATE ON <data>`, unqualified, so it fires a full FTS5
delete-then-insert on every one of those 499 rows even though only the
ordinal and offset columns changed. That is still three to four orders of
magnitude cheaper than 499 re-embeddings, which is the comparison that
matters; `RepairOrdinals` exists as an option to turn the repair pass off for
a consumer who does not need ordinal/offset to stay exact, and verification
item 7 measures the real cost.

An SP1 amendment that narrowed the trigger to `AFTER UPDATE OF <fts columns>
ON` would remove this cost entirely, because a pure ordinal/offset repair
touches no FTS5-indexed column. That amendment is filed as an SP1 issue and
is explicitly **not** a dependency of sub-project 3 — SP3 ships against SP1
as it stands today.
