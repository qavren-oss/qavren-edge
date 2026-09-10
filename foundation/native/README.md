# Native build

One shared library per RID, built from the SQLite amalgamation plus the sqlite-vec
amalgamation plus `src/qedge_init.c`.

`versions.json` pins every upstream source by tag and hash. sqlite.org publishes only
SHA3-256, so `fetch-sources.ps1` hashes with python rather than `Get-FileHash`.

| Variant | Target | Extra defines |
|---|---|---|
| plain | `qedge_sqlite3` | — |
| cipher | `qedge_sqlcipher` | `SQLITE_HAS_CODEC`, `SQLCIPHER_CRYPTO_LIBTOMCRYPT`, `CIPHER="AES-256-CBC"`, `LTC_SOURCE`, `LTC_NO_PROTOTYPES` |

`SQLITE_EXTRA_INIT` is owned by `qedge_extra_init`, which chains
`sqlcipher_extra_init` (cipher build only) then registers `sqlite3_vec_init` and the
`qedge_version()` scalar through `sqlite3_auto_extension`. SQLCipher 4.19 hard-`#error`s
if `SQLITE_EXTRA_INIT`/`SQLITE_EXTRA_SHUTDOWN` are undefined, so chaining is mandatory,
not stylistic.

## Local (Windows x64 only)

```powershell
pwsh .\scripts\fetch-sources.ps1
pwsh .\scripts\build-windows.ps1
```

Apple, Android and Linux artifacts come from `.github/workflows/native.yml`; the
development box has no NDK, clang or macOS.
