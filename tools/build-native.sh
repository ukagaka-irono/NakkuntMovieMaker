#!/usr/bin/env bash
# native/nmm_audio と、tools/fetch-native で取得した ColorfulTalk をビルドして native/bin/<rid>/ に置く（Linux・macOS 用）。
# CMake と C コンパイラ、jq が必要
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
