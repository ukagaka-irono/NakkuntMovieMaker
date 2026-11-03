#Requires -Version 7
# FFmpeg（LGPL・共有ライブラリ版）を取得して native/bin/<rid>/ に置く（Windows 用）
# 取得するバージョンとハッシュは tools/native-deps.json で固定している
param(
    [string]$Rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deps = Get-Content (Join-Path $PSScriptRoot 'native-deps.json') -Raw | ConvertFrom-Json
$entry = $deps.ffmpeg.files.$Rid
if (-not $entry) {
    Write-Warning "FFmpeg の配布物が用意されていない RID です: $Rid"
    exit 0
}

$downloads = Join-Path $root 'native/downloads'
$outDir = Join-Path $root "native/bin/$Rid"
New-Item -ItemType Directory -Force $downloads, $outDir | Out-Null

$archive = Join-Path $downloads ([System.IO.Path]::GetFileName($entry.url))
if (-not (Test-Path $archive)) {
    Write-Host "ダウンロード: $($entry.url)"
    Invoke-WebRequest -Uri $entry.url -OutFile $archive
}

$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $entry.sha256) {
    Remove-Item $archive
    throw "SHA-256 が一致しません: $archive（期待値 $($entry.sha256)、実際 $hash）"
}

$extract = Join-Path $downloads "ffmpeg-$Rid"
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
Expand-Archive -Path $archive -DestinationPath $extract
$top = Get-ChildItem $extract -Directory | Select-Object -First 1

Copy-Item (Join-Path $top.FullName "$($entry.libDir)/*.dll") $outDir -Force
$licenses = Join-Path $outDir 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $top.FullName 'LICENSE.txt') (Join-Path $licenses 'ffmpeg-LICENSE.txt') -Force

Write-Host "FFmpeg $($deps.ffmpeg.version)（$($deps.ffmpeg.license)）を $outDir に配置しました"
