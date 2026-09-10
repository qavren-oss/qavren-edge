#!/usr/bin/env bash
# Builds libqedge_sqlite3.so (or libqedge_sqlcipher.so) for one Linux RID.
set -euo pipefail

NATIVE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-linux-x64}"
CIPHER="${2:-OFF}"
BUILD_SHA="${3:-local}"

case "$RID" in
  linux-x64)   CC_BIN=gcc ;;
  linux-arm64) CC_BIN=aarch64-linux-gnu-gcc ;;
  *) echo "unsupported rid: $RID" >&2; exit 2 ;;
esac

BUILD_DIR="$NATIVE_ROOT/build/$RID-$CIPHER"
cmake -S "$NATIVE_ROOT" -B "$BUILD_DIR" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_COMPILER="$CC_BIN" \
  -DBUILD_SHARED_LIBS=ON \
  -DQEDGE_CIPHER="$CIPHER" \
  -DQEDGE_BUILD_SHA="$BUILD_SHA"
cmake --build "$BUILD_DIR"

mkdir -p "$NATIVE_ROOT/artifacts/$RID"
cp "$BUILD_DIR"/out/lib*.so "$NATIVE_ROOT/artifacts/$RID/"
echo "OK: $NATIVE_ROOT/artifacts/$RID"
