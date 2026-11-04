#Requires -Version 7
# native/nmm_audio と、tools/fetch-native で取得した ColorfulTalk をビルドして native/bin/<rid>/ に置く（Windows 用）。CMake と MSVC が必要
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
