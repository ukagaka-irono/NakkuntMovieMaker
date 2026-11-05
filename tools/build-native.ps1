#Requires -Version 7
# native/nmm_audio と、tools/fetch-native で取得した ColorfulTalk・FFmpeg をビルドして native/bin/<rid>/ に置く（Windows 用）。
# CMake と MSVC、FFmpeg のために MSYS2 が必要（UCRT64 の gcc・nasm・pkgconf・cmake・ninja・jq と make・diffutils。入れ方は README）
param(
    [string]$Rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'native/nmm_audio'
$build = Join-Path $root "native/build/$Rid"
$outDir = Join-Path $root "native/bin/$Rid"

cmake -S $source -B $build -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake の構成に失敗しました' }
cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'nmm_audio のビルドに失敗しました' }
cmake --install $build --config Release --prefix $outDir
if ($LASTEXITCODE -ne 0) { throw 'nmm_audio の配置に失敗しました' }

Write-Host "nmm_audio を $outDir に配置しました"

# ColorfulTalk（共有ライブラリだけを作る）
$deps = Get-Content (Join-Path $PSScriptRoot 'native-deps.json') -Raw | ConvertFrom-Json
$colorfulSource = Join-Path $root "native/downloads/$($deps.colorfulTalk.dir)"
if (-not (Test-Path (Join-Path $colorfulSource 'CMakeLists.txt'))) { throw "ColorfulTalk のソースがありません。先に tools/fetch-native.ps1 を実行してください" }
$colorfulBuild = Join-Path $root "native/build/$Rid-colorful-talk"
cmake -S $colorfulSource -B $colorfulBuild -A x64
if ($LASTEXITCODE -ne 0) { throw 'ColorfulTalk の CMake の構成に失敗しました' }
cmake --build $colorfulBuild --config Release --target colorful_talk
if ($LASTEXITCODE -ne 0) { throw 'ColorfulTalk のビルドに失敗しました' }
Copy-Item (Join-Path $colorfulBuild 'Release/colorful_talk.dll') $outDir -Force
New-Item -ItemType Directory -Force (Join-Path $outDir 'licenses') | Out-Null
Copy-Item (Join-Path $colorfulSource 'LICENSE') (Join-Path $outDir 'licenses/colorful-talk-LICENSE.txt') -Force

Write-Host "ColorfulTalk $($deps.colorfulTalk.version) を $outDir に配置しました"

# FFmpeg（OpenH264・kvazaar・QSV・NVENC・AMF・MF 付き、GPL の部品なし）を、MSYS2 の UCRT64 の gcc でビルドする（tools/build-ffmpeg.sh）
$msys = if ($env:MSYS2_ROOT) { $env:MSYS2_ROOT } else { 'C:\msys64' }
$bash = Join-Path $msys 'usr/bin/bash.exe'
if (-not (Test-Path $bash)) { throw "MSYS2 が見つかりません（$msys）。README の手順で入れるか、MSYS2_ROOT に場所を指定してください" }
$env:MSYSTEM = 'UCRT64'
$env:CHERE_INVOKING = '1'
$env:NMM_ROOT = $root
$env:NMM_RID = $Rid
$env:NMM_OUT = $outDir
& $bash -lc 'exec "$(cygpath -u "$NMM_ROOT")/tools/build-ffmpeg.sh" "$NMM_RID" "$(cygpath -u "$NMM_OUT")"'
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg のビルドに失敗しました' }
