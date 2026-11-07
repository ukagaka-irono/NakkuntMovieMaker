#Requires -Version 7
# native/nmm_audio と、tools/fetch-native で取得した ColorfulTalk・OpenColorIO（native/nmm_ocio に組み込む）・FFmpeg をビルドして native/bin/<rid>/ に置く（Windows 用）。
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

# OpenColorIO と依存するライブラリを静的ライブラリにして（native/nmm_ocio/deps）、それを包む nmm_ocio を作る。
# ソースの版とビルドの手順が同じなら、前のビルド（CI ではキャッシュ）を使う
$downloads = Join-Path $root 'native/downloads'
if (-not (Test-Path (Join-Path $downloads 'src-ocio-opencolorio'))) { throw "OpenColorIO のソースがありません。先に tools/fetch-native.ps1 を実行してください" }
$env:CMAKE_BUILD_PARALLEL_LEVEL = [Environment]::ProcessorCount
$ocioRoot = Join-Path $root "native/build/$Rid-ocio"
$ocioPrefix = Join-Path $ocioRoot 'install'
$ocioDeps = Join-Path $root 'native/nmm_ocio/deps'
$stampText = ($deps.ocio | ConvertTo-Json -Compress -Depth 10) + (Get-Content (Join-Path $ocioDeps 'CMakeLists.txt') -Raw)
$stamp = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($stampText)))
$stampFile = Join-Path $ocioRoot '.stamp'
if (-not (Test-Path $stampFile) -or (Get-Content $stampFile -Raw).Trim() -ne $stamp) {
    if (Test-Path $ocioRoot) { Remove-Item -Recurse -Force $ocioRoot }
    cmake -S $ocioDeps -B (Join-Path $ocioRoot 'build') -A x64 "-DNMM_DOWNLOADS=$downloads" "-DCMAKE_INSTALL_PREFIX=$ocioPrefix"
    if ($LASTEXITCODE -ne 0) { throw 'OpenColorIO の CMake の構成に失敗しました' }
    cmake --build (Join-Path $ocioRoot 'build') --config Release
    if ($LASTEXITCODE -ne 0) { throw 'OpenColorIO のビルドに失敗しました' }
    Set-Content $stampFile $stamp
}
else {
    Write-Host "OpenColorIO $($deps.ocio.version) は前のビルドを使います（$ocioPrefix）"
}

$ocioShimBuild = Join-Path $root "native/build/$Rid-nmm_ocio"
cmake -S (Join-Path $root 'native/nmm_ocio') -B $ocioShimBuild -A x64 "-DCMAKE_PREFIX_PATH=$ocioPrefix" "-DNMM_DOWNLOADS=$downloads"
if ($LASTEXITCODE -ne 0) { throw 'nmm_ocio の CMake の構成に失敗しました' }
cmake --build $ocioShimBuild --config Release
if ($LASTEXITCODE -ne 0) { throw 'nmm_ocio のビルドに失敗しました' }
ctest --test-dir $ocioShimBuild -C Release --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'nmm_ocio のテストに失敗しました' }
cmake --install $ocioShimBuild --config Release --prefix $outDir
if ($LASTEXITCODE -ne 0) { throw 'nmm_ocio の配置に失敗しました' }

Write-Host "nmm_ocio（OpenColorIO $($deps.ocio.version)）を $outDir に配置しました"

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
