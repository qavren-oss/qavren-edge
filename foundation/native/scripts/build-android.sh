#!/usr/bin/env bash
# Builds libqedge_sqlite3.so for every Android ABI using the pinned NDK.
set -euo pipefail

NATIVE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CIPHER="${1:-OFF}"
BUILD_SHA="${2:-local}"
: "${ANDROID_NDK_ROOT:?ANDROID_NDK_ROOT must point at NDK r28 or newer}"

declare -A RID_FOR_ABI=(
  [arm64-v8a]=android-arm64
  [x86_64]=android-x64
  [armeabi-v7a]=android-arm
)

for ABI in "${!RID_FOR_ABI[@]}"; do
  RID="${RID_FOR_ABI[$ABI]}"
  BUILD_DIR="$NATIVE_ROOT/build/$RID-$CIPHER"
  cmake -S "$NATIVE_ROOT" -B "$BUILD_DIR" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_ROOT/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI="$ABI" \
    -DANDROID_PLATFORM=android-21 \
    -DANDROID_STL=none \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON \
    -DQEDGE_CIPHER="$CIPHER" \
    -DQEDGE_BUILD_SHA="$BUILD_SHA"
  cmake --build "$BUILD_DIR"

  mkdir -p "$NATIVE_ROOT/artifacts/$RID"
  cp "$BUILD_DIR"/out/lib*.so "$NATIVE_ROOT/artifacts/$RID/"

  # A silently 4 KB-aligned .so fails Play submission, not the build, and raises XA0141 in every
  # consuming app. Assert it here rather than discovering it at store review.
  for SO in "$NATIVE_ROOT/artifacts/$RID"/lib*.so; do
    if ! "$ANDROID_NDK_ROOT"/toolchains/llvm/prebuilt/*/bin/llvm-readelf -l "$SO" \
         | grep -E '^\s+LOAD' | grep -q '0x4000'; then
      echo "FAIL: $SO is not 16 KB aligned" >&2
      exit 1
    fi
  done
done

echo "OK: android artifacts under $NATIVE_ROOT/artifacts"
