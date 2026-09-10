# Runs SQLCipher's own tclsh-driven amalgamation build, then copies the result out.
find_program(TCLSH NAMES tclsh tclsh86 tclsh8.6 REQUIRED)
execute_process(
    COMMAND ${TCLSH} "${SQLCIPHER_DIR}/tool/mksqlite3c.tcl"
    WORKING_DIRECTORY "${SQLCIPHER_DIR}"
    RESULT_VARIABLE rc)
if(NOT rc EQUAL 0)
  message(FATAL_ERROR "mksqlite3c.tcl failed (rc=${rc}). Install tclsh; Git for Windows ships one at Git/mingw64/bin/tclsh.exe.")
endif()
file(COPY "${SQLCIPHER_DIR}/sqlite3.c" "${SQLCIPHER_DIR}/src/sqlite3.h" DESTINATION "${OUT_DIR}")
