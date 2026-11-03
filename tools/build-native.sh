#!/usr/bin/env bash
# native/nmm_audio をビルドして native/bin/<rid>/ に置く（Linux・macOS 用）。CMake と C コンパイラが必要
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) default_rid=linux-x64 ;;
  Linux-aarch64) default_rid=linux-arm64 ;;
  Darwin-arm64) default_rid=osx-arm64 ;;
  *) default_rid=unknown ;;
esac
rid="${1:-$default_rid}"

build="$root/native/build/$rid"
out_dir="$root/native/bin/$rid"

cmake -S "$root/native/nmm_audio" -B "$build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build" --config Release
cmake --install "$build" --config Release --prefix "$out_dir"

echo "nmm_audio を $out_dir に配置しました"
