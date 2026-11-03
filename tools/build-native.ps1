#Requires -Version 7
# native/nmm_audio をビルドして native/bin/<rid>/ に置く（Windows 用）。CMake と MSVC が必要
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
