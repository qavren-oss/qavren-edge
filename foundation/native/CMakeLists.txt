cmake_minimum_required(VERSION 3.24)
project(qedge_sqlite LANGUAGES C)

option(QEDGE_CIPHER "Build the SQLCipher + libtomcrypt variant" OFF)
set(QEDGE_BUILD_SHA "local" CACHE STRING "Build provenance recorded in qedge_version()")

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(DEPS "${CMAKE_CURRENT_SOURCE_DIR}/_deps")

# --- versions.json is the single source of truth for the version strings -----------------
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/versions.json" QEDGE_VERSIONS_JSON)
string(JSON QEDGE_SQLITE_VERSION GET "${QEDGE_VERSIONS_JSON}" sqlite version)
string(JSON QEDGE_VEC_VERSION GET "${QEDGE_VERSIONS_JSON}" sqliteVec version)
string(JSON QEDGE_SQLCIPHER_VERSION GET "${QEDGE_VERSIONS_JSON}" sqlcipher version)
string(JSON QEDGE_SQLITE_DIR GET "${QEDGE_VERSIONS_JSON}" sqlite extractedDir)

# --- common SQLite compile configuration (spec 10.2) -------------------------------------
set(QEDGE_COMMON_DEFS
    SQLITE_ENABLE_FTS5
    SQLITE_ENABLE_RTREE
    SQLITE_ENABLE_MATH_FUNCTIONS
    SQLITE_ENABLE_DBSTAT_VTAB
    SQLITE_ENABLE_COLUMN_METADATA
    SQLITE_THREADSAFE=1
    SQLITE_DQS=0
    SQLITE_DEFAULT_WAL_SYNCHRONOUS=1
    SQLITE_USE_URI=1
    SQLITE_TEMP_STORE=2
    SQLITE_LIKE_DOESNT_MATCH_BLOBS
    SQLITE_OMIT_DEPRECATED
    SQLITE_OMIT_LOAD_EXTENSION
    SQLITE_DEFAULT_FOREIGN_KEYS=1
    SQLITE_EXTRA_INIT=qedge_extra_init
    SQLITE_EXTRA_SHUTDOWN=qedge_extra_shutdown
    # sqlite-vec: SQLITE_CORE is load-bearing (switches the include to sqlite3.h and drops
    # the SQLITE_EXTENSION_INIT1/2 thunk); SQLITE_VEC_STATIC only suppresses dllexport on Windows.
    SQLITE_CORE
    SQLITE_VEC_STATIC
    QEDGE_SQLITE_VERSION="${QEDGE_SQLITE_VERSION}"
    QEDGE_VEC_VERSION="v${QEDGE_VEC_VERSION}"
    QEDGE_BUILD_SHA="${QEDGE_BUILD_SHA}")

# sqlite-vec's SIMD kernels define PORTABLE_ALIGN32/64 as __attribute__((aligned(N))), which
# cl.exe rejects (C2143/C2065/C2168 at sqlite-vec.c:133+). Enable them on GCC/Clang toolchains
# only; MSVC builds use sqlite-vec's portable scalar paths.
if(NOT MSVC)
  if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64|ARM64")
    list(APPEND QEDGE_COMMON_DEFS SQLITE_VEC_ENABLE_NEON)
  elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64|x64")
    list(APPEND QEDGE_COMMON_DEFS SQLITE_VEC_ENABLE_AVX)
  endif()
endif()

set(QEDGE_VEC_DIR "${DEPS}/sqlite-vec")

# --- plain variant -----------------------------------------------------------------------
if(NOT QEDGE_CIPHER)
  set(SQLITE_DIR "${DEPS}/${QEDGE_SQLITE_DIR}")
  add_library(qedge_sqlite3
      "${SQLITE_DIR}/sqlite3.c"
      "${QEDGE_VEC_DIR}/sqlite-vec.c"
      "${CMAKE_CURRENT_SOURCE_DIR}/src/qedge_init.c")
  target_include_directories(qedge_sqlite3 PRIVATE "${SQLITE_DIR}" "${QEDGE_VEC_DIR}")
  target_compile_definitions(qedge_sqlite3 PRIVATE ${QEDGE_COMMON_DEFS} QEDGE_CIPHER_VERSION="none")
  set(QEDGE_TARGET qedge_sqlite3)
endif()

# --- cipher variant ----------------------------------------------------------------------
if(QEDGE_CIPHER)
  set(SQLCIPHER_DIR "${DEPS}/sqlcipher-${QEDGE_SQLCIPHER_VERSION}")
  set(LTC_DIR "${DEPS}/libtomcrypt-1.18.2")

  # SQLCipher ships no amalgamation; generate it with tclsh (Git for Windows provides one).
  set(SQLCIPHER_AMALGAMATION "${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation/sqlite3.c")
  add_custom_command(
      OUTPUT "${SQLCIPHER_AMALGAMATION}"
      COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation"
      COMMAND ${CMAKE_COMMAND} -E chdir "${SQLCIPHER_DIR}" ${CMAKE_COMMAND}
              -DSQLCIPHER_DIR=${SQLCIPHER_DIR}
              -DOUT_DIR=${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation
              -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/make-sqlcipher-amalgamation.cmake"
      DEPENDS "${SQLCIPHER_DIR}/tool/mksqlite3c.tcl"
      COMMENT "Generating the SQLCipher amalgamation with tclsh"
      VERBATIM)

  execute_process(
      COMMAND python "${CMAKE_CURRENT_SOURCE_DIR}/cmake/ltc-sources.py" "${LTC_DIR}"
      OUTPUT_VARIABLE LTC_SOURCES
      RESULT_VARIABLE LTC_RC)
  if(NOT LTC_RC EQUAL 0)
    message(FATAL_ERROR "ltc-sources.py failed (rc=${LTC_RC})")
  endif()

  add_library(qedge_sqlcipher
      "${SQLCIPHER_AMALGAMATION}"
      "${QEDGE_VEC_DIR}/sqlite-vec.c"
      "${CMAKE_CURRENT_SOURCE_DIR}/src/qedge_init.c"
      ${LTC_SOURCES})
  target_include_directories(qedge_sqlcipher PRIVATE
      "${CMAKE_CURRENT_BINARY_DIR}/sqlcipher-amalgamation"
      "${QEDGE_VEC_DIR}"
      "${LTC_DIR}/src/headers")
  target_compile_definitions(qedge_sqlcipher PRIVATE
      ${QEDGE_COMMON_DEFS}
      QEDGE_CIPHER
      QEDGE_CIPHER_VERSION="${QEDGE_SQLCIPHER_VERSION}"
      SQLITE_HAS_CODEC
      SQLCIPHER_CRYPTO_LIBTOMCRYPT
      CIPHER="AES-256-CBC"
      LTC_SOURCE
      LTC_NO_PROTOTYPES)
  if(WIN32)
    # _WIN32 is dropped: MSVC predefines it. ENDIAN_LITTLE is dropped on x64, where
    # libtomcrypt's src/headers/tomcrypt_cfg.h derives it from _M_X64 itself and our define
    # only produced C4005 macro redefinition on all 214 LTC translation units. ARM64 still
    # needs it: MSVC's _M_ARM64 appears nowhere in that header's detection table, and its
    # endianness fallback (tomcrypt_cfg.h:174) tests only compiler macros cl.exe never sets,
    # so the ARM64 slice would stop at "#error Cannot detect endianness".
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64|ARM64")
      target_compile_definitions(qedge_sqlcipher PRIVATE ENDIAN_LITTLE)
    endif()
    target_link_libraries(qedge_sqlcipher PRIVATE advapi32 bcrypt)
  endif()
  set(QEDGE_TARGET qedge_sqlcipher)
endif()

if(MSVC)
  target_compile_options(${QEDGE_TARGET} PRIVATE /guard:cf /W1 /O2 /Oi)
  target_link_options(${QEDGE_TARGET} PRIVATE /GUARD:CF /DYNAMICBASE /NXCOMPAT)
  target_compile_definitions(${QEDGE_TARGET} PRIVATE SQLITE_OS_WIN SQLITE_WIN32_FILEMAPPING_API=1 SQLITE_API=__declspec\(dllexport\))
else()
  target_compile_definitions(${QEDGE_TARGET} PRIVATE SQLITE_OS_UNIX)
endif()

if(ANDROID)
  # NDK r28+ aligns to 16 KB by default; pass the flags anyway so a downgrade or an
  # armeabi-v7a build cannot silently regress and trip XA0141 in every consuming app.
  target_link_options(${QEDGE_TARGET} PRIVATE
      "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
  target_link_libraries(${QEDGE_TARGET} PRIVATE log)
endif()

set_target_properties(${QEDGE_TARGET} PROPERTIES
    C_VISIBILITY_PRESET hidden
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out")
