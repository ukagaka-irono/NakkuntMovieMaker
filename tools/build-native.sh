#!/usr/bin/env bash
# native/nmm_audio と、tools/fetch-native で取得した ColorfulTalk・OpenColorIO（native/nmm_ocio に組み込む）をビルドして native/bin/<rid>/ に置く（Linux・macOS 用）。
# FFmpeg も tools/build-ffmpeg.sh でビルドする。CMake と C/C++ コンパイラ、make、pkg-config、jq（x64 では nasm も）が必要
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

# ColorfulTalk（共有ライブラリだけを作る）
deps="$root/tools/native-deps.json"
colorful_source="$root/native/downloads/$(jq -r '.colorfulTalk.dir' "$deps")"
if [ ! -f "$colorful_source/CMakeLists.txt" ]; then
  echo "ColorfulTalk のソースがありません。先に tools/fetch-native.sh を実行してください" >&2
  exit 1
fi
colorful_build="$root/native/build/$rid-colorful-talk"
cmake -S "$colorful_source" -B "$colorful_build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$colorful_build" --config Release --target colorful_talk
cp -a "$colorful_build"/libcolorful_talk.* "$out_dir/"
mkdir -p "$out_dir/licenses"
cp "$colorful_source/LICENSE" "$out_dir/licenses/colorful-talk-LICENSE.txt"

echo "ColorfulTalk $(jq -r '.colorfulTalk.version' "$deps") を $out_dir に配置しました"

# OpenColorIO と依存するライブラリを静的ライブラリ（PIC）にして（native/nmm_ocio/deps）、それを包む nmm_ocio を作る。
# ソースの版とビルドの手順が同じなら、前のビルド（CI ではキャッシュ）を使う
downloads="$root/native/downloads"
if [ ! -d "$downloads/src-ocio-opencolorio" ]; then
  echo "OpenColorIO のソースがありません。先に tools/fetch-native.sh を実行してください" >&2
  exit 1
fi
export CMAKE_BUILD_PARALLEL_LEVEL="$(nproc 2>/dev/null || sysctl -n hw.ncpu)"
ocio_root="$root/native/build/$rid-ocio"
ocio_prefix="$ocio_root/install"
ocio_deps="$root/native/nmm_ocio/deps"
if command -v sha256sum >/dev/null; then sha256_stdin() { sha256sum | cut -d' ' -f1; }; else sha256_stdin() { shasum -a 256 | cut -d' ' -f1; }; fi
ocio_stamp="$({ jq -c '.ocio' "$deps"; cat "$ocio_deps/CMakeLists.txt"; } | sha256_stdin)"
if [ "$(cat "$ocio_root/.stamp" 2>/dev/null || true)" != "$ocio_stamp" ]; then
  rm -rf "$ocio_root"
  cmake -S "$ocio_deps" -B "$ocio_root/build" -DNMM_DOWNLOADS="$downloads" -DCMAKE_INSTALL_PREFIX="$ocio_prefix"
  cmake --build "$ocio_root/build" --config Release
  echo "$ocio_stamp" > "$ocio_root/.stamp"
else
  echo "OpenColorIO $(jq -r '.ocio.version' "$deps") は前のビルドを使います（${ocio_prefix}）"
fi

ocio_shim_build="$root/native/build/$rid-nmm_ocio"
cmake -S "$root/native/nmm_ocio" -B "$ocio_shim_build" -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_PREFIX_PATH="$ocio_prefix" -DNMM_DOWNLOADS="$downloads"
cmake --build "$ocio_shim_build" --config Release
ctest --test-dir "$ocio_shim_build" -C Release --output-on-failure
cmake --install "$ocio_shim_build" --config Release --prefix "$out_dir"

echo "nmm_ocio（OpenColorIO $(jq -r '.ocio.version' "$deps")）を $out_dir に配置しました"

# FFmpeg を、OpenH264・kvazaar などを組み込んでソースからビルドする（tools/fetch-native.sh で取得したもの）。GPL の部品は含めない
if jq -e --arg rid "$rid" '.ffmpeg.sourceBuild.rids | index($rid)' "$deps" >/dev/null; then
  "$root/tools/build-ffmpeg.sh" "$rid" "$out_dir"
fi
