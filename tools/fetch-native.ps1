#Requires -Version 7
# 同梱するフォントを取得して native/bin/<rid>/ に置く（Windows 用）。
# FFmpeg（LGPL・共有ライブラリ）と、それに組み込む部品、ColorfulTalk（音声合成）のソースも取得する（ビルドは tools/build-native）
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

# ColorfulTalk のソース（ファイルごとに SHA-256 を確かめる）
$colorful = $deps.colorfulTalk
$colorfulDir = Join-Path $downloads $colorful.dir
foreach ($source in $colorful.files) {
    $target = Join-Path $colorfulDir $source.path
    if (-not (Test-Path $target)) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        Write-Host "ダウンロード: $($colorful.baseUrl)$($source.path)"
        Invoke-WebRequest -Uri "$($colorful.baseUrl)$($source.path)" -OutFile $target
    }

    $hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $source.sha256) {
        Remove-Item $target
        throw "SHA-256 が一致しません: $target（期待値 $($source.sha256)、実際 $hash）"
    }
}
Write-Host "ColorfulTalk $($colorful.version) のソースを $colorfulDir に取得しました"

# FFmpeg と、FFmpeg に組み込む部品のソースを取得する（ビルドは tools/build-native）。rids がない部品は、すべての RID で使う
if ($deps.ffmpeg.sourceBuild.rids -notcontains $Rid) {
    Write-Warning "FFmpeg のビルドに対応していない RID です: $Rid"
    exit 0
}

$names = @()
foreach ($source in $deps.ffmpeg.sourceBuild.sources) {
    if ($source.rids -and $source.rids -notcontains $Rid) { continue }
    $names += $source.name
    $archive = Join-Path $downloads "$($source.name)-$($source.version)-$([System.IO.Path]::GetFileName($source.url))"
    if (-not (Test-Path $archive)) {
        Write-Host "ダウンロード: $($source.url)"
        Invoke-WebRequest -Uri $source.url -OutFile $archive
    }

    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $source.sha256) {
        Remove-Item $archive
        throw "SHA-256 が一致しません: $archive（期待値 $($source.sha256)、実際 $hash）"
    }

    $sourceDir = Join-Path $downloads "src-$Rid-$($source.name)"
    if (Test-Path $sourceDir) { Remove-Item -Recurse -Force $sourceDir }
    New-Item -ItemType Directory -Force $sourceDir | Out-Null
    tar -xf $archive -C $sourceDir --strip-components 1
    if ($LASTEXITCODE -ne 0) { throw "展開に失敗しました: $archive" }
}

Write-Host "FFmpeg $($deps.ffmpeg.version) と部品（$(($names | Select-Object -Skip 1) -join ' ')）のソースを取得しました（ビルドは tools/build-native.ps1）"
