#!/usr/bin/env bash
# 同梱するフォントを取得して native/bin/<rid>/ に置く（Linux・macOS 用）。
# FFmpeg（LGPL・共有ライブラリ）と、それに組み込む部品、ColorfulTalk（音声合成）、OpenColorIO と依存するライブラリのソースも取得する（ビルドは tools/build-native）。
# ビルドに使う道具（Slang の slangc）は native/tools/<この PC の RID>/ に、macOS では MoltenVK を native/bin/<rid>/ に置く
# 取得するバージョンとハッシュは tools/native-deps.json で固定している。jq が必要
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
deps="$root/tools/native-deps.json"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) host_rid=linux-x64 ;;
  Linux-aarch64) host_rid=linux-arm64 ;;
  Darwin-arm64) host_rid=osx-arm64 ;;
  *) host_rid=unknown ;;
esac
rid="${1:-$host_rid}"

downloads="$root/native/downloads"
out_dir="$root/native/bin/$rid"
mkdir -p "$downloads" "$out_dir"

sha256_of() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# url を downloads に取得して、SHA-256 を確かめる。取得したファイルの場所を verified に入れる
fetch_verified() {
  local url="$1" sha256="$2" actual
  verified="$downloads/$(basename "$url")"
  if [ ! -f "$verified" ]; then
    echo "ダウンロード: $url"
    curl -fL --retry 3 -o "$verified" "$url"
  fi

  actual="$(sha256_of "$verified")"
  if [ "$actual" != "$sha256" ]; then
    rm -f "$verified"
    echo "SHA-256 が一致しません: $verified（期待値 $sha256、実際 $actual）" >&2
    exit 1
  fi
}

# アーカイブを downloads/<name>-<version>-<ファイル名> に取得して、SHA-256 を確かめる。場所を archive に入れる。
# ハッシュをまだ決めていない（空の）ときは、実際の値を表示して missing_hash を立て、1 を返す（最後に失敗させる）
missing_hash=0
fetch_archive() {
  local name="$1" version="$2" url="$3" sha256="$4" actual
  archive="$downloads/$name-$version-$(basename "$url")"
  if [ ! -f "$archive" ]; then
    echo "ダウンロード: $url"
    # （if の条件の中で呼ぶので set -e が効かない。失敗をここで止める）
    curl -fL --retry 3 -o "$archive" "$url" || { rm -f "$archive"; echo "ダウンロードに失敗しました: $url" >&2; exit 1; }
  fi

  actual="$(sha256_of "$archive")"
  if [ -z "$sha256" ]; then
    echo "SHA-256 が未設定です: $name $version の SHA-256 は $actual" >&2
    rm -f "$archive"
    missing_hash=1
    return 1
  fi
  if [ "$actual" != "$sha256" ]; then
    rm -f "$archive"
    echo "SHA-256 が一致しません: $archive（期待値 $sha256、実際 $actual）" >&2
    exit 1
  fi
}

# archive を dir に展開し直す。strip が 1 なら、先頭のフォルダーを外す
extract_to() {
  local dir="$1" strip="$2"
  rm -rf "$dir"
  mkdir -p "$dir"
  tar -xf "$archive" -C "$dir" --strip-components "$strip"
}

# jq の結果（JSON の配列の要素）の数
count_of() { jq "$1 | length" "$deps"; }

# rids がない要素は、すべての RID で使う
applies_to_rid() { jq -e --arg rid "$rid" "$1 | .rids == null or (.rids | index(\$rid) != null)" "$deps" >/dev/null; }

# フォント（OS に関係なく同じもの）
font_count="$(count_of '.fonts.files')"
for i in $(seq 0 $((font_count - 1))); do
  fetch_verified "$(jq -r ".fonts.files[$i].url" "$deps")" "$(jq -r ".fonts.files[$i].sha256" "$deps")"
  dir="$out_dir/$(jq -r ".fonts.files[$i].dir" "$deps")"
  name="$(jq -r ".fonts.files[$i].name // empty" "$deps")"
  mkdir -p "$dir"
  cp "$verified" "$dir/${name:-$(basename "$verified")}"
done
echo "フォント $(jq -r '.fonts.name' "$deps") を $out_dir に配置しました"

# ColorfulTalk のソース（ファイルごとに SHA-256 を確かめる）
colorful_base="$(jq -r '.colorfulTalk.baseUrl' "$deps")"
colorful_dir="$downloads/$(jq -r '.colorfulTalk.dir' "$deps")"
colorful_count="$(count_of '.colorfulTalk.files')"
for i in $(seq 0 $((colorful_count - 1))); do
  path="$(jq -r ".colorfulTalk.files[$i].path" "$deps")"
  expected="$(jq -r ".colorfulTalk.files[$i].sha256" "$deps")"
  target="$colorful_dir/$path"
  if [ ! -f "$target" ]; then
    mkdir -p "$(dirname "$target")"
    echo "ダウンロード: $colorful_base$path"
    curl -fL --retry 3 -o "$target" "$colorful_base$path"
  fi
  actual="$(sha256_of "$target")"
  if [ "$actual" != "$expected" ]; then
    rm -f "$target"
    echo "SHA-256 が一致しません: $target（期待値 $expected、実際 $actual）" >&2
    exit 1
  fi
done
echo "ColorfulTalk $(jq -r '.colorfulTalk.version' "$deps") のソースを $colorful_dir に取得しました"

# Slang（slangc）。ビルドする PC で動く版を native/tools/<host-rid>/slang/ に置く（アーカイブには先頭のフォルダーがない）
slang_version="$(jq -r '.slang.version' "$deps")"
slang_host="$(jq -c --arg rid "$host_rid" '.slang.hosts[] | select(.rid == $rid)' "$deps")"
if [ -z "$slang_host" ]; then
  echo "警告: Slang の版がない PC です: $host_rid" >&2
elif fetch_archive slang "$slang_version" "$(jq -r '.url' <<<"$slang_host")" "$(jq -r '.sha256' <<<"$slang_host")"; then
  slang_dir="$root/native/tools/$host_rid/slang"
  extract_to "$slang_dir" 0
  "$slang_dir/bin/slangc" -version >/dev/null
  echo "Slang $slang_version（slangc）を $slang_dir に配置しました"
fi

# MoltenVK（macOS の実行時のライブラリ）
if applies_to_rid '.moltenVk'; then
  if fetch_archive moltenvk "$(jq -r '.moltenVk.version' "$deps")" "$(jq -r '.moltenVk.url' "$deps")" "$(jq -r '.moltenVk.sha256' "$deps")"; then
    moltenvk_dir="$downloads/src-moltenvk"
    extract_to "$moltenvk_dir" 1
    cp "$moltenvk_dir/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib" "$moltenvk_dir/MoltenVK/dynamic/dylib/macOS/MoltenVK_icd.json" "$out_dir/"
    mkdir -p "$out_dir/licenses"
    cp "$moltenvk_dir/LICENSE" "$out_dir/licenses/moltenvk-LICENSE.txt"
    echo "MoltenVK $(jq -r '.moltenVk.version' "$deps") を $out_dir に配置しました"
  fi
fi

# Vulkan-Headers（FFmpeg の Vulkan 対応のビルド用）
if applies_to_rid '.vulkanHeaders'; then
  if fetch_archive vulkan-headers "$(jq -r '.vulkanHeaders.version' "$deps")" "$(jq -r '.vulkanHeaders.url' "$deps")" "$(jq -r '.vulkanHeaders.sha256' "$deps")"; then
    extract_to "$downloads/src-vulkan-headers" 1
    echo "Vulkan-Headers $(jq -r '.vulkanHeaders.version' "$deps") を取得しました"
  fi
fi

# OpenColorIO と依存するライブラリのソース（どの RID でも同じソースを、別のフォルダーでビルドする）
ocio_count="$(count_of '.ocio.sources')"
for i in $(seq 0 $((ocio_count - 1))); do
  name="$(jq -r ".ocio.sources[$i].name" "$deps")"
  if fetch_archive "$name" "$(jq -r ".ocio.sources[$i].version" "$deps")" \
    "$(jq -r ".ocio.sources[$i].url" "$deps")" "$(jq -r ".ocio.sources[$i].sha256" "$deps")"; then
    extract_to "$downloads/src-ocio-$name" 1
  fi
done
echo "OpenColorIO $(jq -r '.ocio.version' "$deps") と依存するライブラリのソースを取得しました（ビルドは tools/build-native.sh）"

# FFmpeg と、FFmpeg に組み込む部品のソースを取得する（ビルドは tools/build-native）。rids がない部品は、すべての RID で使う
if ! jq -e --arg rid "$rid" '.ffmpeg.sourceBuild.rids | index($rid)' "$deps" >/dev/null; then
  echo "警告: FFmpeg のビルドに対応していない RID です: $rid" >&2
else
  names=()
  source_count="$(count_of '.ffmpeg.sourceBuild.sources')"
  for i in $(seq 0 $((source_count - 1))); do
    applies_to_rid ".ffmpeg.sourceBuild.sources[$i]" || continue
    name="$(jq -r ".ffmpeg.sourceBuild.sources[$i].name" "$deps")"
    names+=("$name")
    if fetch_archive "$name" "$(jq -r ".ffmpeg.sourceBuild.sources[$i].version" "$deps")" \
      "$(jq -r ".ffmpeg.sourceBuild.sources[$i].url" "$deps")" "$(jq -r ".ffmpeg.sourceBuild.sources[$i].sha256" "$deps")"; then
      extract_to "$downloads/src-$rid-$name" 1
    fi
  done
  echo "FFmpeg $(jq -r '.ffmpeg.version' "$deps") と部品（${names[*]:1}）のソースを取得しました（ビルドは tools/build-native.sh）"
fi

if [ "$missing_hash" = 1 ]; then
  echo "tools/native-deps.json に SHA-256 を書いてから、もう一度実行してください" >&2
  exit 1
fi
