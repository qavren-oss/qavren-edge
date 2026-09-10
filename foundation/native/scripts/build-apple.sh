#!/usr/bin/env bash
set -euo pipefail

NATIVE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CIPHER="${1:-OFF}"
BUILD_SHA="${2:-local}"
NAME=$([ "$CIPHER" = "ON" ] && echo qedge_sqlcipher || echo qedge_sqlite3)

build_slice() {  # $1 slice, $2 sdk, $3 arch, $4 target-triple
  local slice="$1" sdk="$2" arch="$3" triple="$4"
  local dir="$NATIVE_ROOT/build/apple/$slice-$arch-$CIPHER"
  cmake -S "$NATIVE_ROOT" -B "$dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_SYSROOT="$(xcrun --sdk "$sdk" --show-sdk-path)" \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_C_FLAGS="-target $triple" \
    -DBUILD_SHARED_LIBS=OFF \
    -DQEDGE_CIPHER="$CIPHER" \
    -DQEDGE_BUILD_SHA="$BUILD_SHA"
  cmake --build "$dir"
  echo "$dir/out/lib$NAME.a"
}

OUT="$NATIVE_ROOT/artifacts/apple"
rm -rf "$OUT"; mkdir -p "$OUT"/{ios,iossim,maccatalyst,macos}

lipo -create "$(build_slice ios iphoneos arm64 arm64-apple-ios15.0)" -output "$OUT/ios/lib$NAME.a"
lipo -create \
  "$(build_slice iossim iphonesimulator arm64 arm64-apple-ios15.0-simulator)" \
  "$(build_slice iossim iphonesimulator x86_64 x86_64-apple-ios15.0-simulator)" \
  -output "$OUT/iossim/lib$NAME.a"
lipo -create \
  "$(build_slice maccatalyst macosx arm64 arm64-apple-ios15.0-macabi)" \
  "$(build_slice maccatalyst macosx x86_64 x86_64-apple-ios15.0-macabi)" \
  -output "$OUT/maccatalyst/lib$NAME.a"
lipo -create \
  "$(build_slice macos macosx arm64 arm64-apple-macos12.0)" \
  "$(build_slice macos macosx x86_64 x86_64-apple-macos12.0)" \
  -output "$OUT/macos/lib$NAME.a"

xcodebuild -create-xcframework \
  -library "$OUT/ios/lib$NAME.a" \
  -library "$OUT/iossim/lib$NAME.a" \
  -library "$OUT/maccatalyst/lib$NAME.a" \
  -library "$OUT/macos/lib$NAME.a" \
  -output "$OUT/$NAME.xcframework"

# Mac Catalyst and macOS desktop consume a dylib through runtimes/, not the xcframework.
for rid_arch in "maccatalyst-arm64:maccatalyst:arm64" "maccatalyst-x64:maccatalyst:x86_64" \
                "osx-arm64:macos:arm64" "osx-x64:macos:x86_64"; do
  IFS=: read -r rid slice arch <<<"$rid_arch"
  dir="$NATIVE_ROOT/build/apple/$slice-$arch-dylib-$CIPHER"
  triple=$([ "$slice" = maccatalyst ] && echo "$arch-apple-ios15.0-macabi" || echo "$arch-apple-macos12.0")
  cmake -S "$NATIVE_ROOT" -B "$dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_SYSROOT="$(xcrun --sdk macosx --show-sdk-path)" \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_C_FLAGS="-target $triple" \
    -DQEDGE_CIPHER="$CIPHER" -DQEDGE_BUILD_SHA="$BUILD_SHA"
  cmake --build "$dir"
  mkdir -p "$NATIVE_ROOT/artifacts/$rid"
  cp "$dir/out/lib$NAME.dylib" "$NATIVE_ROOT/artifacts/$rid/"
done

echo "OK: $OUT/$NAME.xcframework"
