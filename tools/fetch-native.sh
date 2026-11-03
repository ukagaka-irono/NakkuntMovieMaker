#!/usr/bin/env bash
# FFmpeg（LGPL・共有ライブラリ版）を取得して native/bin/<rid>/ に置く（Linux・macOS 用）
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

url="$(jq -r --arg rid "$rid" '.ffmpeg.files[$rid].url // empty' "$deps")"
if [ -z "$url" ]; then
  echo "警告: FFmpeg の配布物が用意されていない RID です: $rid" >&2
  exit 0
fi
sha256="$(jq -r --arg rid "$rid" '.ffmpeg.files[$rid].sha256' "$deps")"
lib_dir="$(jq -r --arg rid "$rid" '.ffmpeg.files[$rid].libDir' "$deps")"
version="$(jq -r '.ffmpeg.version' "$deps")"

downloads="$root/native/downloads"
out_dir="$root/native/bin/$rid"
mkdir -p "$downloads" "$out_dir"

archive="$downloads/$(basename "$url")"
if [ ! -f "$archive" ]; then
  echo "ダウンロード: $url"
  curl -fL --retry 3 -o "$archive" "$url"
fi

if command -v sha256sum >/dev/null; then
  actual="$(sha256sum "$archive" | cut -d' ' -f1)"
else
  actual="$(shasum -a 256 "$archive" | cut -d' ' -f1)"
fi
if [ "$actual" != "$sha256" ]; then
  rm -f "$archive"
  echo "SHA-256 が一致しません: $archive（期待値 $sha256、実際 $actual）" >&2
  exit 1
fi

extract="$downloads/ffmpeg-$rid"
rm -rf "$extract"
mkdir -p "$extract"
tar -xf "$archive" -C "$extract"
top="$(find "$extract" -mindepth 1 -maxdepth 1 -type d | head -n 1)"

cp -a "$top/$lib_dir"/*.so* "$out_dir/"
mkdir -p "$out_dir/licenses"
cp "$top/LICENSE.txt" "$out_dir/licenses/ffmpeg-LICENSE.txt"

echo "FFmpeg $version を $out_dir に配置しました"
