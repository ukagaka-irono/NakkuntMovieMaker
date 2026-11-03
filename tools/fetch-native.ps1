#Requires -Version 7
# 同梱するフォントと FFmpeg（LGPL・共有ライブラリ版）を取得して native/bin/<rid>/ に置く（Windows 用）
# 取得するバージョンとハッシュは tools/native-deps.json で固定している
param(
    [string]$Rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deps = Get-Content (Join-Path $PSScriptRoot 'native-deps.json') -Raw | ConvertFrom-Json
$downloads = Join-Path $root 'native/downloads'
$outDir = Join-Path $root "native/bin/$Rid"
New-Item -ItemType Directory -Force $downloads, $outDir | Out-Null

# url を downloads に取得して、SHA-256 を確かめる。取得したファイルの場所を返す
function Get-Verified([string]$Url, [string]$Sha256) {
    $file = Join-Path $downloads ([System.Uri]::UnescapeDataString([System.IO.Path]::GetFileName($Url)))
    if (-not (Test-Path $file)) {
        Write-Host "ダウンロード: $Url"
        Invoke-WebRequest -Uri $Url -OutFile $file
    }

    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256) {
        Remove-Item $file
        throw "SHA-256 が一致しません: $file（期待値 $Sha256、実際 $hash）"
    }

    return $file
}

# フォント（OS に関係なく同じもの）
foreach ($font in $deps.fonts.files) {
    $file = Get-Verified $font.url $font.sha256
    $dir = Join-Path $outDir $font.dir
    New-Item -ItemType Directory -Force $dir | Out-Null
    $name = if ($font.name) { $font.name } else { [System.IO.Path]::GetFileName($file) }
    Copy-Item $file (Join-Path $dir $name) -Force
}
Write-Host "フォント $($deps.fonts.name)（$($deps.fonts.license)）を $outDir に配置しました"

$entry = $deps.ffmpeg.files.$Rid
if (-not $entry) {
    Write-Warning "FFmpeg の配布物が用意されていない RID です: $Rid"
    exit 0
}

$archive = Get-Verified $entry.url $entry.sha256

$extract = Join-Path $downloads "ffmpeg-$Rid"
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
Expand-Archive -Path $archive -DestinationPath $extract
$top = Get-ChildItem $extract -Directory | Select-Object -First 1

Copy-Item (Join-Path $top.FullName "$($entry.libDir)/*.dll") $outDir -Force
$licenses = Join-Path $outDir 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $top.FullName 'LICENSE.txt') (Join-Path $licenses 'ffmpeg-LICENSE.txt') -Force

Write-Host "FFmpeg $($deps.ffmpeg.version)（$($deps.ffmpeg.license)）を $outDir に配置しました"
