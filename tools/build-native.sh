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

# BtbN の配布物がない RID（macOS）は、FFmpeg を OpenH264・kvazaar 付きで、ソースからビルドする（tools/fetch-native.sh で取得したもの）。
# GPL の部品は含めない（--enable-gpl を付けない）。共有ライブラリは @loader_path で互いを探すようにして、出力先に置く
if jq -e --arg rid "$rid" '.ffmpeg.sourceBuild.rids | index($rid)' "$deps" >/dev/null; then
  source_of() { echo "$root/native/downloads/src-$rid-$1"; }
  for name in ffmpeg openh264 kvazaar; do
    if [ ! -d "$(source_of "$name")" ]; then
      echo "$name のソースがありません。先に tools/fetch-native.sh を実行してください" >&2
      exit 1
    fi
  done

  prefix="$root/native/build/$rid-ffmpeg"
  jobs="$(sysctl -n hw.ncpu 2>/dev/null || nproc)"
  # ソースの版とこのスクリプトが同じなら、前のビルドを使う（CI のキャッシュ用）
  stamp="$(jq -c '.ffmpeg.sourceBuild' "$deps" | shasum -a 256 | cut -d' ' -f1)-1"
  if [ "$(cat "$prefix/.stamp" 2>/dev/null || true)" != "$stamp" ]; then
    rm -rf "$prefix"
    mkdir -p "$prefix"

    make -C "$(source_of openh264)" -j"$jobs" PREFIX="$prefix" install-shared

    (
      cd "$(source_of kvazaar)"
      ./configure --prefix="$prefix" --disable-static --enable-shared
      make -j"$jobs"
      make install
    )

    (
      cd "$(source_of ffmpeg)"
      PKG_CONFIG_PATH="$prefix/lib/pkgconfig" ./configure \
        --prefix="$prefix" --install-name-dir=@loader_path \
        --enable-shared --disable-static --disable-programs --disable-doc --disable-debug \
        --disable-autodetect --enable-pthreads --enable-zlib --enable-bzlib \
        --enable-videotoolbox --enable-audiotoolbox \
        --enable-libopenh264 --enable-libkvazaar
      make -j"$jobs"
      make install
    )

    echo "$stamp" > "$prefix/.stamp"
  fi

  # libavcodec.63.dylib のように、主な版の番号だけが付いた名前で置く（FFmpeg.AutoGen はこの名前で探す）
  copied=()
  for lib in "$prefix"/lib/*.dylib; do
    base="$(basename "$lib")"
    if [[ "$base" =~ ^lib[a-z0-9_]+\.[0-9]+\.dylib$ ]]; then
      rm -f "$out_dir/$base"
      cp -L "$lib" "$out_dir/$base"
      chmod u+w "$out_dir/$base"
      copied+=("$out_dir/$base")
    fi
  done

  # ビルドした場所ではなく、同じフォルダから互いを読み込むようにして、署名し直す（Apple Silicon では署名が必要）
  for lib in "${copied[@]}"; do
    install_name_tool -id "@loader_path/$(basename "$lib")" "$lib"
    otool -L "$lib" | tail -n +2 | awk '{print $1}' | while read -r dependency; do
      case "$dependency" in
        "$prefix"/*|lib*) install_name_tool -change "$dependency" "@loader_path/$(basename "$dependency")" "$lib" ;;
      esac
    done
    codesign --force --sign - "$lib"
  done

  mkdir -p "$out_dir/licenses"
  cat "$(source_of ffmpeg)/LICENSE.md" "$(source_of ffmpeg)/COPYING.LGPLv3" "$(source_of ffmpeg)/COPYING.LGPLv2.1" > "$out_dir/licenses/ffmpeg-LICENSE.txt"
  cp "$(source_of openh264)/LICENSE" "$out_dir/licenses/openh264-LICENSE.txt"
  cp "$(source_of kvazaar)/LICENSE" "$out_dir/licenses/kvazaar-LICENSE.txt"

  echo "FFmpeg $(jq -r '.ffmpeg.version' "$deps")（OpenH264・kvazaar 付き）を $out_dir に配置しました: ${copied[*]##*/}"
fi
