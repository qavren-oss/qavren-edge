# 2. Own the SQLite build and the SQLitePCLRaw provider

Date: 2026-09-10

## Status

Accepted

## Context

iOS cannot load SQLite extensions at runtime: the OS-provided `libsqlite3` is
compiled without extension loading, and `SQLITE_OMIT_LOAD_EXTENSION` is the
App Store-safe posture anyway. sqlite-vec must therefore be compiled into the
library and registered at library initialisation.

Consuming `e_sqlite3` is not an option because it has no sqlite-vec.
Consuming `SQLitePCLRaw.provider.dynamic_cdecl` is not an option because it
resolves entry points through `dlsym`, which a static iOS link does not offer.

## Decision

Compile our own library (`qedge_sqlite3`) from the SQLite amalgamation plus the
sqlite-vec amalgamation plus a `SQLITE_EXTRA_INIT` hook, and ship a checked-in,
generator-produced `SQLite3Provider_qedge : ISQLite3Provider` that P/Invokes it.

The provider uses classic `[DllImport(..., ExactSpelling = true,
CallingConvention = CallingConvention.Cdecl)]`, matching upstream SQLitePCLRaw.
`[LibraryImport]` with `[DisableRuntimeMarshalling]` was rejected: the attribute
is assembly-scoped and bans reference types and `in`/`ref`/`out` parameters in
interop, which the `SafeHandle`-typed and `out IntPtr` signatures across
`ISQLite3Provider` require.

## Consequences

We own SQLite CVE response for this library and must bump `native/versions.json`
ourselves; Dependabot cannot see these pins, so `upstream-pins.yml` opens a
monthly issue listing newer upstream tags.

We gain sqlite-vec on every platform including iOS, one code path for
encryption via a sibling SQLCipher build, and full control of compile flags.
