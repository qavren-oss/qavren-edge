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
