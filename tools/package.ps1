# 配布用の zip を作る（Windows x64）。
#   1. tools/fetch-native.ps1 と tools/build-native.ps1 で、ネイティブライブラリを用意する（-SkipNative で省く）
#   2. アプリ（NakkuntMovieMaker.exe）とコマンドライン（nmm.exe）を、self-contained で artifacts/<名前>/ に publish する
#   3. samples・README・LICENSE・THIRD-PARTY-NOTICES を入れ、要るファイルがそろっているかを確かめる
#   4. artifacts/<名前>.zip にして、SHA-256 を表示する（リリースノートに書く）
# 名前は NakkuntMovieMaker-<版>-<RID>。版は Directory.Build.props の Version
param(
    [string]$Rid = 'win-x64',
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) {
    throw 'Directory.Build.props に Version がありません'
}

$name = "NakkuntMovieMaker-$version-$Rid"
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"

if (-not $SkipNative) {
    & (Join-Path $PSScriptRoot 'fetch-native.ps1') $Rid
    & (Join-Path $PSScriptRoot 'build-native.ps1') $Rid
}

if (Test-Path $output) {
    Remove-Item -Recurse -Force $output
}

if (Test-Path $zip) {
    Remove-Item -Force $zip
}

foreach ($project in 'src/NakkuntMovieMaker.App', 'src/NakkuntMovieMaker.Cli') {
    dotnet publish $project -c Release -r $Rid --self-contained -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "$project を publish できませんでした"
    }
}

Copy-Item -Recurse (Join-Path $root 'samples') (Join-Path $output 'samples')
foreach ($file in 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $root $file) $output
}

# 要るファイル（アプリ・コマンドライン・FFmpeg・音・OCIO・VMA・フォント・ライセンス）がそろっているか
$required = @(
    'NakkuntMovieMaker.exe', 'nmm.exe', 'nmm_audio.dll', 'nmm_ocio.dll', 'vma.dll',
    'fonts/BIZUDPGothic-Regular.ttf',
    'licenses/ffmpeg-LICENSE.txt', 'licenses/opencolorio-LICENSE.txt', 'licenses/vma-LICENSE.txt', 'licenses/BIZUDPGothic-OFL.txt',
    'THIRD-PARTY-NOTICES.md', 'LICENSE', 'samples/general/general.nmmproj'
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $output $_)) }
if (-not (Get-ChildItem $output -Filter 'avcodec-*.dll')) {
    $missing += 'avcodec-*.dll'
}

if ($missing) {
    throw "配布物に足りないファイルがあります: $($missing -join ', ')"
}

# GPL の FFmpeg（テストの素材を作るためだけに使うもの）が紛れ込んでいないか
$gpl = Get-ChildItem $output -Recurse -Filter 'postproc-*.dll'
if ($gpl) {
    throw "GPL の FFmpeg の部品が入っています: $($gpl.Name -join ', ')"
}

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "$zip（$size MB）を作りました"
Write-Host "SHA-256: $hash"
