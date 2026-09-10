# 1. Record architecture decisions

Date: 2026-09-10

## Status

Accepted

## Context

Qavren.Edge makes several decisions that are expensive to reverse: owning a
native SQLite build, generating an `ISQLite3Provider` instead of consuming a
published one, and pinning upstream C sources by hash.

## Decision

We record architecturally significant decisions as numbered ADRs in
`foundation/docs/adr/`. Later sub-projects add their own numbered files to the
same sequence.

## Consequences

Decisions are reviewable in a pull request and durable beyond any one spec.
