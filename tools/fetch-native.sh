#!/usr/bin/env bash
# 同梱するフォントを取得して native/bin/<rid>/ に置く（Linux・macOS 用）。
# FFmpeg（LGPL・共有ライブラリ）と、それに組み込む部品、ColorfulTalk（音声合成）のソースも取得する（ビルドは tools/build-native）
# 取得するバージョンとハッシュは tools/native-deps.json で固定している。jq が必要
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
deps="$root/tools/native-deps.json"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) default_rid=linux-x64 ;;
  Linux-aarch64) default_rid=linux-arm64 ;;
  Darwin-arm64) default_rid=osx-arm64 ;;
  *) default_rid=unknown ;;
esac
rid="${1:-$default_rid}"

downloads="$root/native/downloads"
out_dir="$root/native/bin/$rid"
mkdir -p "$downloads" "$out_dir"

# url を downloads に取得して、SHA-256 を確かめる。取得したファイルの場所を verified に入れる
fetch_verified() {
  local url="$1" sha256="$2" actual
  verified="$downloads/$(basename "$url")"
  if [ ! -f "$verified" ]; then
    echo "ダウンロード: $url"
    curl -fL --retry 3 -o "$verified" "$url"
  fi

  if command -v sha256sum >/dev/null; then
    actual="$(sha256sum "$verified" | cut -d' ' -f1)"
  else
    actual="$(shasum -a 256 "$verified" | cut -d' ' -f1)"
  fi
  if [ "$actual" != "$sha256" ]; then
    rm -f "$verified"
    echo "SHA-256 が一致しません: $verified（期待値 $sha256、実際 $actual）" >&2
    exit 1
  fi
}

# フォント（OS に関係なく同じもの）
font_count="$(jq '.fonts.files | length' "$deps")"
for i in $(seq 0 $((font_count - 1))); do
  fetch_verified "$(jq -r ".fonts.files[$i].url" "$deps")" "$(jq -r ".fonts.files[$i].sha256" "$deps")"
  dir="$out_dir/$(jq -r ".fonts.files[$i].dir" "$deps")"
  name="$(jq -r ".fonts.files[$i].name // empty" "$deps")"
  mkdir -p "$dir"
  cp "$verified" "$dir/${name:-$(basename "$verified")}"
done
echo "フォント $(jq -r '.fonts.name' "$deps") を $out_dir に配置しました"

# ColorfulTalk のソース（ファイルごとに SHA-256 を確かめる）
sha256_of() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}
colorful_base="$(jq -r '.colorfulTalk.baseUrl' "$deps")"
colorful_dir="$downloads/$(jq -r '.colorfulTalk.dir' "$deps")"
colorful_count="$(jq '.colorfulTalk.files | length' "$deps")"
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

# FFmpeg と、FFmpeg に組み込む部品のソースを取得する（ビルドは tools/build-native）。rids がない部品は、すべての RID で使う
if ! jq -e --arg rid "$rid" '.ffmpeg.sourceBuild.rids | index($rid)' "$deps" >/dev/null; then
  echo "警告: FFmpeg のビルドに対応していない RID です: $rid" >&2
  exit 0
fi

missing_hash=0
names=()
source_count="$(jq '.ffmpeg.sourceBuild.sources | length' "$deps")"
for i in $(seq 0 $((source_count - 1))); do
  if ! jq -e --arg rid "$rid" ".ffmpeg.sourceBuild.sources[$i] | .rids == null or (.rids | index(\$rid) != null)" "$deps" >/dev/null; then
    continue
  fi
  name="$(jq -r ".ffmpeg.sourceBuild.sources[$i].name" "$deps")"
  names+=("$name")
  source_url="$(jq -r ".ffmpeg.sourceBuild.sources[$i].url" "$deps")"
  source_sha256="$(jq -r ".ffmpeg.sourceBuild.sources[$i].sha256" "$deps")"
  archive="$downloads/$name-$(jq -r ".ffmpeg.sourceBuild.sources[$i].version" "$deps")-$(basename "$source_url")"
  if [ ! -f "$archive" ]; then
    echo "ダウンロード: $source_url"
    curl -fL --retry 3 -o "$archive" "$source_url"
  fi

  actual="$(sha256_of "$archive")"
  if [ -z "$source_sha256" ]; then
    # ハッシュをまだ決めていない（版を上げたとき）。表示して、最後に失敗させる
    echo "SHA-256 が未設定です: $name の SHA-256 は $actual" >&2
    rm -f "$archive"
    missing_hash=1
    continue
  fi

  if [ "$actual" != "$source_sha256" ]; then
    rm -f "$archive"
    echo "SHA-256 が一致しません: $archive（期待値 $source_sha256、実際 $actual）" >&2
    exit 1
  fi

  source_dir="$downloads/src-$rid-$name"
  rm -rf "$source_dir"
  mkdir -p "$source_dir"
  tar -xf "$archive" -C "$source_dir" --strip-components 1
done

if [ "$missing_hash" = 1 ]; then
  echo "tools/native-deps.json に SHA-256 を書いてから、もう一度実行してください" >&2
  exit 1
fi

echo "FFmpeg $(jq -r '.ffmpeg.version' "$deps") と部品（${names[*]:1}）のソースを取得しました（ビルドは tools/build-native.sh）"
