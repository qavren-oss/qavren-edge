# Third-party notices

Qavren.Edge is MIT licensed. It redistributes or derives from the following.

## SQLite (public domain)

`foundation/native` compiles the SQLite amalgamation, which its authors have
dedicated to the public domain.

## sqlite-vec (MIT OR Apache-2.0)

Copyright (c) 2024 Alex Garcia. Dual licensed MIT OR Apache-2.0; both licences apply
to the redistributed source and to binaries built from it. Full texts:
https://github.com/asg017/sqlite-vec/blob/main/LICENSE-MIT and
https://github.com/asg017/sqlite-vec/blob/main/LICENSE-APACHE

## SQLCipher (BSD-style, Zetetic LLC) — Cipher packages only

`Qavren.Edge.Sqlite.Native.Cipher` contains SQLCipher. See
https://github.com/sqlcipher/sqlcipher/blob/master/LICENSE

## LibTomCrypt (public domain / Unlicense) — Cipher packages only

https://github.com/libtom/libtomcrypt

## SQLitePCLRaw (Apache-2.0)

`foundation/src/Qavren.Edge.Sqlite.Provider/Generated/SQLite3Provider_qedge.g.cs` is
derived from `SQLitePCLRaw.provider.internal/Generated/provider_internal_funcptrs.cs`
at tag v3.0.5, Copyright Eric Sink, licensed under the Apache License 2.0. The Apache
header is retained in the vendored template at
`foundation/src/Qavren.Edge.Sqlite.Provider/Template/provider_internal_funcptrs.cs.template`.
