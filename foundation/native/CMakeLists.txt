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
#
# The ISA is decided from the COMPILER's target, never from CMAKE_SYSTEM_PROCESSOR, which is
# the HOST value on two of our three Unix lanes: build-linux.sh's linux-arm64 slice sets only
# CMAKE_C_COMPILER (so an x86_64 host reported x86_64 for an aarch64 target), and the macos-15
# runners are arm64, so build-apple.sh's x86_64 slices would have been handed <arm_neon.h>.
# __aarch64__/__x86_64__ come off the real compile line, including CMAKE_OSX_ARCHITECTURES and
# the -target triple, so every slice is classified correctly.
set(QEDGE_VEC_SIMD_OPTIONS "")
if(NOT MSVC)
  include(CheckCSourceCompiles)
  include(CheckCCompilerFlag)
  # STATIC_LIBRARY: these probes must not try to LINK an executable, which an iphoneos or
  # android cross toolchain cannot do without a sysroot's worth of startup objects.
  set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
  check_c_source_compiles("
#if !defined(__aarch64__) && !defined(__arm64__)
#error not arm64
#endif
int main(void) { return 0; }" QEDGE_TARGET_IS_ARM64)
  check_c_source_compiles("
#if !defined(__x86_64__) && !defined(__amd64__)
#error not x86_64
#endif
int main(void) { return 0; }" QEDGE_TARGET_IS_X86_64)
  if(QEDGE_TARGET_IS_ARM64)
    # Advanced SIMD is baseline on aarch64, so no -m flag is needed and no target CPU can
    # lack it. armeabi-v7a matches neither probe and stays on the scalar path.
    list(APPEND QEDGE_COMMON_DEFS SQLITE_VEC_ENABLE_NEON)
    # sqlite-vec's l1_int8_neon feeds the int8x16_t returned by vabdq_s8 straight into
    # vpaddlq_u8 (sqlite-vec.c:283/288/293/298/308). That is deliberate and correct - an
    # int8 absolute difference is a magnitude that only reads back right as unsigned
    # (|-128 - 127| = 255, which is -1 as int8) - but it is only ACCEPTED by Clang, which
    # defaults to -flax-vector-conversions=integer. GCC rejects it outright ("error:
    # incompatible type for argument 1 of 'vpaddlq_u8'"), which is what broke the
    # aarch64-linux-gnu-gcc cross slice while the Apple and Android arm64 (Clang) slices
    # compiled the identical source. GCC's own diagnostic names this flag as the remedy.
    # Scoped to sqlite-vec.c below, so sqlite3.c and qedge_init.c keep strict vector typing.
    check_c_compiler_flag("-flax-vector-conversions" QEDGE_HAVE_LAX_VECTOR_CONVERSIONS)
    if(QEDGE_HAVE_LAX_VECTOR_CONVERSIONS)
      list(APPEND QEDGE_VEC_SIMD_OPTIONS "-flax-vector-conversions")
    endif()
  elseif(QEDGE_TARGET_IS_X86_64 AND NOT APPLE)
    # Two separate reasons this is not just a -D:
    #   1. GCC and Clang refuse to inline the always_inline _mm256_* intrinsics unless the AVX
    #      target feature is actually on ("target specific option mismatch"), which is what
    #      broke the first linux-x64 CI build. -mavx is scoped to the sqlite-vec translation
    #      unit below so sqlite3.c and qedge_init.c keep the baseline x86-64 ISA.
    #   2. sqlite-vec dispatches on #ifdef, not on a runtime CPU probe (sqlite-vec.c:392), so
    #      defining SQLITE_VEC_ENABLE_AVX makes l2_sqr_float_avx the ONLY float L2 kernel and
    #      the artifact then REQUIRES an AVX CPU. That is a safe bet on linux/win/android x64;
    #      it is not on Apple, where Rosetta 2 does not implement AVX and an osx-x64 or
    #      maccatalyst-x64 slice would SIGILL on every Apple Silicon Mac. Apple x86_64
    #      therefore stays on the portable scalar path.
    check_c_compiler_flag("-mavx" QEDGE_HAVE_MAVX)
    if(QEDGE_HAVE_MAVX)
      list(APPEND QEDGE_COMMON_DEFS SQLITE_VEC_ENABLE_AVX)
      list(APPEND QEDGE_VEC_SIMD_OPTIONS "-mavx")
    endif()
  endif()
  unset(CMAKE_TRY_COMPILE_TARGET_TYPE)
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

# Only sqlite-vec.c contains intrinsics, so the SIMD ISA flag is scoped to that one source
# rather than raised on the whole target.
if(QEDGE_VEC_SIMD_OPTIONS)
  set_source_files_properties("${QEDGE_VEC_DIR}/sqlite-vec.c"
      PROPERTIES COMPILE_OPTIONS "${QEDGE_VEC_SIMD_OPTIONS}")
endif()

if(MSVC)
  target_compile_options(${QEDGE_TARGET} PRIVATE /guard:cf /W1 /O2 /Oi)
  target_link_options(${QEDGE_TARGET} PRIVATE /GUARD:CF /DYNAMICBASE /NXCOMPAT)
  target_compile_definitions(${QEDGE_TARGET} PRIVATE SQLITE_OS_WIN SQLITE_WIN32_FILEMAPPING_API=1 SQLITE_API=__declspec\(dllexport\))
else()
  # Export the public SQLite API despite C_VISIBILITY_PRESET hidden below: the managed provider
  # resolves every sqlite3_* entry point by name (DllImport), so they must stay default-visibility
  # in the .so/.dylib. Everything else (sqlite-vec internals, qedge_init) stays hidden.
  target_compile_definitions(${QEDGE_TARGET} PRIVATE SQLITE_OS_UNIX "SQLITE_API=__attribute__((visibility(\"default\")))")
endif()

if(ANDROID)
  # NDK r28+ aligns to 16 KB by default; pass the flags anyway so a downgrade or an
  # armeabi-v7a build cannot silently regress and trip XA0141 in every consuming app.
  target_link_options(${QEDGE_TARGET} PRIVATE
      "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
  target_link_libraries(${QEDGE_TARGET} PRIVATE log)
endif()

# ARCHIVE_ is load-bearing since add_library() stopped hard-coding SHARED: a
# -DBUILD_SHARED_LIBS=OFF configure (build-apple.sh's static slices) emits an ARCHIVE artifact,
# which ignores LIBRARY_OUTPUT_DIRECTORY and would otherwise land in the build root, not out/.
set_target_properties(${QEDGE_TARGET} PROPERTIES
    C_VISIBILITY_PRESET hidden
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out")
