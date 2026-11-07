#!/usr/bin/env bash
# FFmpeg（LGPL・共有ライブラリ）を、tools/fetch-native で取得したソースからビルドして、出力先とその licenses/ に置く。
# GPL の部品は含めない（--enable-gpl を付けず、--disable-autodetect で、ここに書いた部品だけを使う）。
#   使い方: tools/build-ffmpeg.sh <rid> <出力先>
# tools/build-native.sh（Linux・macOS）と tools/build-native.ps1（Windows。MSYS2 の UCRT64 の bash で実行）から呼ぶ。
# 必要なもの: jq、make、pkg-config、C/C++ コンパイラ。x64 では nasm。Windows では MSYS2 の UCRT64 の gcc・cmake・ninja も
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
deps="$root/tools/native-deps.json"
rid="$1"
out_dir="$2"

case "$rid" in
  win-*) os=windows ;;
  linux-*) os=linux ;;
  osx-*) os=macos ;;
  *) echo "FFmpeg のビルドに対応していない RID です: $rid" >&2; exit 1 ;;
esac

# この RID で使う部品の名前（rids がない部品は、すべての RID で使う）
# （macOS の bash 3.2 でも動くように、mapfile や連想配列は使わない）
names=()
while IFS= read -r name; do
  names+=("$name")
done < <(jq -r --arg rid "$rid" '.ffmpeg.sourceBuild.sources[] | select(.rids == null or (.rids | index($rid))) | .name' "$deps" | tr -d '\r')
source_of() { echo "$root/native/downloads/src-$rid-$1"; }
for name in "${names[@]}"; do
  if [ ! -d "$(source_of "$name")" ]; then
    echo "$name のソースがありません。先に tools/fetch-native を実行してください" >&2
    exit 1
  fi
done

prefix="$root/native/build/$rid-ffmpeg"
jobs="$(nproc 2>/dev/null || sysctl -n hw.ncpu)"
pc_path="$prefix/lib/pkgconfig"
# FFmpeg は、構成の文字列（DLL に入る）にこの PC の場所が残らないように、--prefix=/ffmpeg でビルドして、ここに置く
installed="$prefix/ffmpeg-install/ffmpeg"

# ソースの版とこのスクリプトが同じなら、前のビルドを使う（CI のキャッシュ用）
sha256_of() {
  if command -v sha256sum >/dev/null; then sha256sum | cut -d' ' -f1; else shasum -a 256 | cut -d' ' -f1; fi
}
stamp="$({ jq -c '.ffmpeg.sourceBuild' "$deps"; jq -c '.vulkanHeaders' "$deps"; cat "$0"; } | sha256_of)"
if [ "$(cat "$prefix/.stamp" 2>/dev/null || true)" != "$stamp" ]; then
  rm -rf "$prefix"
  mkdir -p "$prefix"

  ffmpeg_flags=(
    --prefix=/ffmpeg
    --enable-shared --disable-static --disable-programs --disable-doc --disable-debug
    --disable-autodetect --enable-zlib
    --enable-libopenh264 --enable-libkvazaar
  )

  case "$os" in
    macos)
      # OpenH264・kvazaar は共有ライブラリにして、FFmpeg と同じフォルダに置く
      make -C "$(source_of openh264)" -j"$jobs" PREFIX="$prefix" install-shared
      (
        cd "$(source_of kvazaar)"
        ./configure --prefix="$prefix" --disable-static --enable-shared
        make -j"$jobs"
        make install
      )
      ffmpeg_flags+=(
        --install-name-dir=@loader_path --enable-pthreads --enable-bzlib
        --enable-videotoolbox --enable-audiotoolbox
      )
      ;;

    linux | windows)
      # OpenH264・kvazaar は静的ライブラリにして、FFmpeg の共有ライブラリに組み込む
      if [ "$os" = windows ]; then openh264_os=mingw_nt; else openh264_os=linux; fi
      make -C "$(source_of openh264)" -j"$jobs" OS="$openh264_os" ARCH=x86_64 BUILDTYPE=Release \
        PREFIX="$prefix" LIBDIR_NAME=lib install-static
      (
        cd "$(source_of kvazaar)"
        ./configure --prefix="$prefix" --disable-shared --enable-static --with-pic
        make -j"$jobs"
        make install
      )
      # Vulkan（ヘッダーだけ。ローダーは実行時に読み込む）。Vulkan Video でデコードし、GPU の描画と同じデバイスでフレームを渡すため
      vulkan_headers="$root/native/downloads/src-vulkan-headers"
      if [ ! -f "$vulkan_headers/include/vulkan/vulkan.h" ]; then
        echo "Vulkan-Headers のソースがありません。先に tools/fetch-native を実行してください" >&2
        exit 1
      fi
      ffmpeg_flags+=(--enable-vulkan "--extra-cflags=-I$vulkan_headers/include")

      # NVENC・NVDEC（ヘッダーだけ。ドライバーは実行時に読み込む）
      make -C "$(source_of nv-codec-headers)" PREFIX="$prefix" install
      ffmpeg_flags+=(--pkg-config-flags=--static --enable-ffnvcodec --enable-nvenc --enable-nvdec --enable-cuvid)

      if [ "$os" = windows ]; then
        # AMF（ヘッダーだけ。ドライバーは実行時に読み込む）
        mkdir -p "$prefix/include"
        cp -R "$(source_of amf)/AMF" "$prefix/include/AMF"

        # oneVPL（QSV。ディスパッチャーを静的ライブラリにして組み込む）。gcc では _MSC_VER がないので、その判定を直す
        vpl_source="$(source_of libvpl)"
        sed -i 's/^#if _MSC_VER < 1400$/#if defined(_MSC_VER) \&\& _MSC_VER < 1400/' "$vpl_source/libvpl/src/windows/mfx_dispatcher_defs.h"
        cmake -S "$vpl_source" -B "$prefix/build-libvpl" -G Ninja -DCMAKE_BUILD_TYPE=Release \
          -DCMAKE_INSTALL_PREFIX="$prefix" -DCMAKE_INSTALL_LIBDIR=lib -DBUILD_SHARED_LIBS=OFF \
          -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF -DBUILD_TOOLS=OFF -DINSTALL_EXAMPLES=OFF -DBUILD_EXPERIMENTAL=OFF
        cmake --build "$prefix/build-libvpl"
        cmake --install "$prefix/build-libvpl"
        # ディスパッチャーは C++ なので、静的にリンクするときは libstdc++ も要る
        sed -i 's/^Libs.private:.*/Libs.private: -lstdc++/' "$pc_path/vpl.pc"

        # 実行時に MinGW の DLL（libgcc・libstdc++・winpthread・zlib）が要らないように、すべて静的にリンクする
        ffmpeg_flags+=(
          --enable-w32threads --enable-d3d11va --enable-d3d12va --enable-dxva2 --enable-mediafoundation
          --enable-amf --enable-libvpl
          --extra-cflags=-DKVZ_STATIC_LIB --extra-ldflags=-static
        )
      else
        # VA-API は使わない（libva を直接リンクすると、libva がない環境で FFmpeg ごと読み込めなくなるため）。
        # 互いを同じフォルダから探すようにする
        ffmpeg_flags+=(--enable-pthreads "--extra-ldflags=-Wl,-rpath,'\$\$ORIGIN'" --extra-libs=-lstdc++)
      fi
      ;;
  esac

  (
    cd "$(source_of ffmpeg)"
    PKG_CONFIG_PATH="$pc_path" ./configure "${ffmpeg_flags[@]}"
    make -j"$jobs"
    make install DESTDIR="$prefix/ffmpeg-install"
  )

  echo "$stamp" > "$prefix/.stamp"
fi

# ライブラリを出力先に置く
mkdir -p "$out_dir"
copied=()
case "$os" in
  windows)
    for lib in "$installed"/bin/*.dll; do
      cp "$lib" "$out_dir/"
      copied+=("$out_dir/$(basename "$lib")")
    done
    ;;
  linux)
    for lib in "$installed"/lib/lib*.so*; do
      cp -a "$lib" "$out_dir/"
      copied+=("$out_dir/$(basename "$lib")")
    done
    ;;
  macos)
    # libavcodec.63.dylib のように、主な版の番号だけが付いた名前で置く（FFmpeg.AutoGen はこの名前で探す）
    for lib in "$prefix"/lib/*.dylib "$installed"/lib/*.dylib; do
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
    ;;
esac

# ライセンス。FFmpeg と、組み込んだ部品のすべてについて、licenses/<名前>-LICENSE.txt を置く
licenses="$out_dir/licenses"
mkdir -p "$licenses"
ffmpeg_source="$(source_of ffmpeg)"
cat "$ffmpeg_source/LICENSE.md" "$ffmpeg_source/COPYING.LGPLv3" "$ffmpeg_source/COPYING.LGPLv2.1" > "$licenses/ffmpeg-LICENSE.txt"

# 先頭のコメントにライセンスが書いてあるヘッダー（ライセンスのファイルがない部品）。同じ文面は 1 回だけ書く
license_from_headers() {
  local dir="$1" header block key seen=""
  while IFS= read -r header; do
    block="$(awk '{ sub(/\r$/, "") } /^(\/\*| \*|\/\/)/ { print; if (/\*\/[[:space:]]*$/) exit; next } { exit }' "$header")"
    [ -n "$block" ] || continue
    key="$(printf '%s' "$block" | sha256_of)"
    case "$seen" in *"$key"*) continue ;; esac
    seen="$seen $key"
    printf '%s\n%s\n\n' "== ${header#"$dir/"}" "$block"
  done < <(find "$dir" -name '*.h' | LC_ALL=C sort)
}

for name in "${names[@]}"; do
  [ "$name" = ffmpeg ] && continue
  license_file="$(jq -r --arg name "$name" '.ffmpeg.sourceBuild.sources[] | select(.name == $name) | .licenseFile // empty' "$deps" | tr -d '\r')"
  license_headers="$(jq -r --arg name "$name" '.ffmpeg.sourceBuild.sources[] | select(.name == $name) | .licenseHeaders // empty' "$deps" | tr -d '\r')"
  if [ -n "$license_file" ]; then
    cp "$(source_of "$name")/$license_file" "$licenses/$name-LICENSE.txt"
  elif [ -n "$license_headers" ]; then
    license_from_headers "$(source_of "$name")/$license_headers" > "$licenses/$name-LICENSE.txt"
  else
    echo "tools/native-deps.json に $name の licenseFile か licenseHeaders を書いてください" >&2
    exit 1
  fi
done

# Vulkan-Headers（Vulkan の型と定数を FFmpeg に組み込んでいる。Windows・Linux）
if [ "$os" != macos ]; then
  cp "$root/native/downloads/src-vulkan-headers/LICENSE.md" "$licenses/vulkan-headers-LICENSE.txt"
fi

# Windows では、MinGW の zlib・winpthreads も静的にリンクしている（libgcc・libstdc++ は GCC Runtime Library Exception で、表記は要らない）
if [ "$os" = windows ]; then
  mingw_licenses="${MINGW_PREFIX:?MSYS2 の UCRT64 の bash で実行してください}/share/licenses"
  cp "$mingw_licenses/zlib/LICENSE" "$licenses/zlib-LICENSE.txt"
  cp "$mingw_licenses/winpthreads/COPYING" "$licenses/winpthreads-LICENSE.txt"
fi

echo "FFmpeg $(jq -r '.ffmpeg.version' "$deps" | tr -d '\r')（${names[*]:1}）を $out_dir に配置しました: ${copied[*]##*/}"
