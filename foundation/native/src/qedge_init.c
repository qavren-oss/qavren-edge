/*
 * qedge_init.c - the single SQLITE_EXTRA_INIT hook for Qavren.Edge.
 *
 * SQLCipher 4.19 hard-#errors unless SQLITE_EXTRA_INIT and SQLITE_EXTRA_SHUTDOWN are
 * defined, and sqlite-vec's documented static-registration path also wants
 * SQLITE_EXTRA_INIT. Only one symbol can own it, so this file owns it and chains.
 *
 * SQLite calls SQLITE_EXTRA_INIT from sqlite3_initialize(), which runs before any
 * connection exists. sqlite3_auto_extension therefore applies to every physical
 * connection, including ones Microsoft.Data.Sqlite opens from its pool.
 */
#include "sqlite3.h"

extern int sqlite3_vec_init(sqlite3 *db, char **pzErrMsg, const sqlite3_api_routines *pApi);

#ifdef QEDGE_CIPHER
extern int sqlcipher_extra_init(const char *arg);
extern void sqlcipher_extra_shutdown(void);
#endif

#ifndef QEDGE_SQLITE_VERSION
#define QEDGE_SQLITE_VERSION "unknown"
#endif
#ifndef QEDGE_VEC_VERSION
#define QEDGE_VEC_VERSION "unknown"
#endif
#ifndef QEDGE_CIPHER_VERSION
#define QEDGE_CIPHER_VERSION "none"
#endif
#ifndef QEDGE_BUILD_SHA
#define QEDGE_BUILD_SHA "local"
#endif

static const char qedge_version_string[] =
    "sqlite " QEDGE_SQLITE_VERSION
    " | vec " QEDGE_VEC_VERSION
    " | cipher " QEDGE_CIPHER_VERSION
    " | build " QEDGE_BUILD_SHA;

static void qedge_version_func(sqlite3_context *ctx, int argc, sqlite3_value **argv)
{
  (void)argc;
  (void)argv;
  sqlite3_result_text(ctx, qedge_version_string, -1, SQLITE_STATIC);
}

static int qedge_register_version(sqlite3 *db, char **pzErrMsg, const sqlite3_api_routines *pApi)
{
  (void)pzErrMsg;
  (void)pApi;
  return sqlite3_create_function(
      db, "qedge_version", 0,
      SQLITE_UTF8 | SQLITE_DETERMINISTIC | SQLITE_INNOCUOUS,
      0, qedge_version_func, 0, 0);
}

int qedge_extra_init(const char *unused)
{
  int rc;
  (void)unused;

#ifdef QEDGE_CIPHER
  rc = sqlcipher_extra_init(0);
  if (rc != SQLITE_OK)
  {
    return rc;
  }
#endif

  rc = sqlite3_auto_extension((void (*)(void))sqlite3_vec_init);
  if (rc != SQLITE_OK)
  {
    return rc;
  }

  return sqlite3_auto_extension((void (*)(void))qedge_register_version);
}

void qedge_extra_shutdown(void)
{
#ifdef QEDGE_CIPHER
  sqlcipher_extra_shutdown();
#endif
}

/*
 * ---------------------------------------------------------------------------------------
 * Stubs for the entry points this build omits.
 *
 * Every other platform binds a DllImport lazily, on the first call, so a P/Invoke to an
 * entry point the library does not export costs nothing until someone calls it (adjustment
 * 24 accepts exactly that for sqlite3_enable_load_extension). iOS is not lazy: the app
 * links libqedge_sqlite3.a into its own executable and the provider compiles
 * DllImport("__Internal"), so the Apple SDK emits one `-u _symbol` linker flag per P/Invoke
 * in the assembly - the managed linker is off here (MtouchLink=None), so it cannot prove
 * any of them unreachable and prunes none. A symbol the library does not define is then a
 * hard `Undefined symbols for architecture arm64` at link time, not a lazy failure. Twelve
 * of the generated provider's P/Invokes land in that hole:
 *
 *   SQLITE_OMIT_LOAD_EXTENSION -> sqlite3_enable_load_extension
 *   SQLITE_OMIT_DEPRECATED     -> sqlite3_trace, sqlite3_profile, sqlite3_aggregate_count
 *   no SQLITE_ENABLE_SNAPSHOT  -> the five sqlite3_snapshot_* entry points
 *   no SQLCipher (plain build) -> sqlite3_key/_v2, sqlite3_rekey/_v2
 *
 * That list is not guesswork: it is `nm -g` over the built ios-simulator slice subtracted
 * from every DllImport entry point in Generated/SQLite3Provider_qedge.g.cs. Re-run that diff
 * if the provider is ever regenerated against a newer upstream template.
 *
 * Defining them here rather than dropping the omissions keeps spec 10.2's compile
 * configuration intact and keeps the public API honest: every stub returns SQLITE_ERROR (or
 * a null/no-op for the two void-ish ones), which is a better answer than the
 * EntryPointNotFoundException the other platforms raise. Each guard matches the define that
 * caused the omission, so a build that later turns one of these features on drops the stub
 * instead of colliding with the real implementation - and the cipher variant, where
 * SQLCipher supplies the four key/rekey functions for real, never sees them.
 *
 * SQLITE_API is the same visibility/dllexport macro the CMake command line hands sqlite3.c,
 * so these are exported by name from the .so, .dylib and .dll exactly like the rest of the
 * public surface. Note that sqlite3.h DECLARES all of these unconditionally - only the
 * implementations in sqlite3.c are compiled out - so each signature below has to match the
 * header exactly (sqlite3_key and friends are the one group the header does not declare,
 * because that is SQLCipher's addition, not SQLite's).
 * ---------------------------------------------------------------------------------------
 */

#ifdef SQLITE_OMIT_LOAD_EXTENSION
SQLITE_API int sqlite3_enable_load_extension(sqlite3 *db, int onoff)
{
  (void)db;
  (void)onoff;
  return SQLITE_ERROR;
}
#endif

#ifdef SQLITE_OMIT_DEPRECATED
SQLITE_API void *sqlite3_trace(sqlite3 *db,
                              void (*xTrace)(void *, const char *),
                              void *pArg)
{
  (void)db;
  (void)xTrace;
  (void)pArg;
  return 0;
}

SQLITE_API void *sqlite3_profile(sqlite3 *db,
                                 void (*xProfile)(void *, const char *, sqlite3_uint64),
                                 void *pArg)
{
  (void)db;
  (void)xProfile;
  (void)pArg;
  return 0;
}

SQLITE_API int sqlite3_aggregate_count(sqlite3_context *ctx)
{
  (void)ctx;
  return 0;
}
#endif

#ifndef SQLITE_ENABLE_SNAPSHOT
SQLITE_API int sqlite3_snapshot_get(sqlite3 *db, const char *zSchema, sqlite3_snapshot **ppSnapshot)
{
  (void)db;
  (void)zSchema;
  if (ppSnapshot)
  {
    *ppSnapshot = 0;
  }
  return SQLITE_ERROR;
}

SQLITE_API int sqlite3_snapshot_open(sqlite3 *db, const char *zSchema, sqlite3_snapshot *pSnapshot)
{
  (void)db;
  (void)zSchema;
  (void)pSnapshot;
  return SQLITE_ERROR;
}

SQLITE_API void sqlite3_snapshot_free(sqlite3_snapshot *pSnapshot)
{
  (void)pSnapshot;
}

SQLITE_API int sqlite3_snapshot_cmp(sqlite3_snapshot *p1, sqlite3_snapshot *p2)
{
  (void)p1;
  (void)p2;
  return 0;
}

SQLITE_API int sqlite3_snapshot_recover(sqlite3 *db, const char *zDb)
{
  (void)db;
  (void)zDb;
  return SQLITE_ERROR;
}
#endif

#ifndef QEDGE_CIPHER
SQLITE_API int sqlite3_key(sqlite3 *db, const void *pKey, int nKey)
{
  (void)db;
  (void)pKey;
  (void)nKey;
  return SQLITE_ERROR;
}

SQLITE_API int sqlite3_key_v2(sqlite3 *db, const char *zDb, const void *pKey, int nKey)
{
  (void)db;
  (void)zDb;
  (void)pKey;
  (void)nKey;
  return SQLITE_ERROR;
}

SQLITE_API int sqlite3_rekey(sqlite3 *db, const void *pKey, int nKey)
{
  (void)db;
  (void)pKey;
  (void)nKey;
  return SQLITE_ERROR;
}

SQLITE_API int sqlite3_rekey_v2(sqlite3 *db, const char *zDb, const void *pKey, int nKey)
{
  (void)db;
  (void)zDb;
  (void)pKey;
  (void)nKey;
  return SQLITE_ERROR;
}
#endif
