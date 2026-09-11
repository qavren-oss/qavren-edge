# Generates the SQLCipher amalgamation, then copies the result out.
#
# mksqlite3c.tcl cannot be called on its own: it concatenates a pre-processed `tsrc/`
# directory that does not exist in a source checkout. tsrc comes from the `.target_source`
# rule, which first builds lemon.exe and mkkeywordhash.exe, runs the parser generator over
# src/parse.y and ext/fts5/fts5parse.y, and generates ctime.c, opcodes.[ch], pragma.h,
# keywordhash.h and sqlite3.h. Upstream's Makefile.msc owns that whole pipeline, so drive it
# rather than reimplement it.
#
# No external tclsh is needed: Makefile.msc compiles the bundled autosetup/jimsh0.c and runs
# every generator script through it. Its recipes invoke `jimsh0.exe` unqualified, so "." has
# to be on PATH - Windows only searches the working directory implicitly when
# NoDefaultCurrentDirectoryInExePath is unset, which it is not on every machine.
#
# On Windows this expects nmake and cl on PATH; build-windows.ps1 runs the whole build inside
# vcvars. Makefile.msc is nmake-only, so the Unix lanes (build-apple.sh, build-linux.sh and
# build-android.sh, all of which build the cipher variant too) drive the very same pipeline
# through upstream's autosetup build instead: `./configure` writes a Makefile that includes
# main.mk, whose `sqlite3.c` rule is the POSIX twin of the Makefile.msc one. No system tclsh is
# needed there either - autosetup-find-tclsh compiles the bundled autosetup/jimsh0.c when it
# cannot find one, and every generator in this tree is JimTCL-compatible.
#
# The generators are HOST tools (lemon, mkkeywordhash, src-verify, jimsh), so this must never
# inherit the cross-compile toolchain. Nothing exports CC/CFLAGS into a custom-command
# environment, and neither branch passes any, so configure picks the host compiler.

if(CMAKE_HOST_WIN32)
  find_program(NMAKE NAMES nmake REQUIRED)
  execute_process(
      COMMAND "${CMAKE_COMMAND}" -E env "PATH=.;$ENV{PATH}"
              "${NMAKE}" /NOLOGO /f Makefile.msc sqlite3.c
      WORKING_DIRECTORY "${SQLCIPHER_DIR}"
      RESULT_VARIABLE rc)
  if(NOT rc EQUAL 0)
    message(FATAL_ERROR "nmake /f Makefile.msc sqlite3.c failed (rc=${rc}) in ${SQLCIPHER_DIR}")
  endif()
else()
  find_program(MAKE_EXE NAMES gmake make REQUIRED)
  # configure is invoked through /bin/sh so a lost execute bit on the wrapper cannot break the
  # build; it re-execs autosetup itself. Re-running it on every slice would be pure waste, and
  # the Makefile it writes is source-tree state, not build-tree state.
  if(NOT EXISTS "${SQLCIPHER_DIR}/Makefile")
    execute_process(
        COMMAND /bin/sh ./configure
        WORKING_DIRECTORY "${SQLCIPHER_DIR}"
        RESULT_VARIABLE rc)
    if(NOT rc EQUAL 0)
      message(FATAL_ERROR "./configure failed (rc=${rc}) in ${SQLCIPHER_DIR}")
    endif()
  endif()
  execute_process(
      COMMAND "${MAKE_EXE}" sqlite3.c
      WORKING_DIRECTORY "${SQLCIPHER_DIR}"
      RESULT_VARIABLE rc)
  if(NOT rc EQUAL 0)
    message(FATAL_ERROR "make sqlite3.c failed (rc=${rc}) in ${SQLCIPHER_DIR}")
  endif()
endif()

foreach(generated sqlite3.c sqlite3.h)
  if(NOT EXISTS "${SQLCIPHER_DIR}/${generated}")
    message(FATAL_ERROR "the amalgamation build did not produce ${SQLCIPHER_DIR}/${generated}")
  endif()
endforeach()

file(COPY "${SQLCIPHER_DIR}/sqlite3.c" "${SQLCIPHER_DIR}/sqlite3.h" DESTINATION "${OUT_DIR}")
