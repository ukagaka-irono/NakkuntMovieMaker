#Requires -Version 7
# 同梱するフォントを取得して native/bin/<rid>/ に置く（Windows 用）。
# FFmpeg（LGPL・共有ライブラリ）と、それに組み込む部品、ColorfulTalk（音声合成）、OpenColorIO と依存するライブラリのソースも取得する（ビルドは tools/build-native）。
# ビルドに使う道具（Slang の slangc）は native/tools/<この PC の RID>/ に置く
# 取得するバージョンとハッシュは tools/native-deps.json で固定している
param(
    [string]$Rid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deps = Get-Content (Join-Path $PSScriptRoot 'native-deps.json') -Raw | ConvertFrom-Json
$hostRid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
$downloads = Join-Path $root 'native/downloads'
$outDir = Join-Path $root "native/bin/$Rid"
New-Item -ItemType Directory -Force $downloads, $outDir | Out-Null

# url を downloads に取得して、SHA-256 を確かめる。取得したファイルの場所を返す
function Get-Verified([string]$Url, [string]$Sha256) {
    $file = Join-Path $downloads ([System.Uri]::UnescapeDataString([System.IO.Path]::GetFileName($Url)))
    if (-not (Test-Path $file)) {
        Write-Host "ダウンロード: $Url"
        Invoke-Download $Url $file
    }

    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256) {
        Remove-Item $file
        throw "SHA-256 が一致しません: $file（期待値 $Sha256、実際 $hash）"
    }

    return $file
}

# ダウンロードする。つながらない・途中で切れたときは、間を空けて 5 回まで試す（CI で ffmpeg.org などにつながらないことがあるため）。
# 途中まで書いたファイルは消す
function Invoke-Download([string]$Url, [string]$OutFile) {
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $OutFile -TimeoutSec 300
            return
        }
        catch {
            if (Test-Path $OutFile) { Remove-Item $OutFile }
            if ($attempt -ge 5) { throw }
            Write-Warning "ダウンロードに失敗しました（$attempt 回目。$($_.Exception.Message)）。$($attempt * 15) 秒後にやり直します: $Url"
            Start-Sleep -Seconds ($attempt * 15)
        }
    }
}

# アーカイブを downloads/<name>-<version>-<ファイル名> に取得して、SHA-256 を確かめる。場所を返す。
# ハッシュをまだ決めていない（空の）ときは、実際の値を表示して $null を返す（最後に失敗させる）
$script:missingHash = $false
function Get-Archive([string]$Name, [string]$Version, [string]$Url, [string]$Sha256) {
    $archive = Join-Path $downloads "$Name-$Version-$([System.IO.Path]::GetFileName($Url))"
    if (-not (Test-Path $archive)) {
        Write-Host "ダウンロード: $Url"
        Invoke-Download $Url $archive
    }

    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $Sha256) {
        Write-Warning "SHA-256 が未設定です: $Name $Version の SHA-256 は $hash"
        Remove-Item $archive
        $script:missingHash = $true
        return $null
    }
    if ($hash -ne $Sha256) {
        Remove-Item $archive
        throw "SHA-256 が一致しません: $archive（期待値 $Sha256、実際 $hash）"
    }

    return $archive
}

# アーカイブを dir に展開し直す。Strip が 1 なら、先頭のフォルダーを外す
function Expand-ArchiveTo([string]$Archive, [string]$Dir, [int]$Strip) {
    if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
    New-Item -ItemType Directory -Force $Dir | Out-Null
    tar -xf $Archive -C $Dir --strip-components $Strip
    if ($LASTEXITCODE -ne 0) { throw "展開に失敗しました: $Archive" }
}

# rids がない要素は、すべての RID で使う
function Test-AppliesToRid($Entry) { -not $Entry.rids -or $Entry.rids -contains $Rid }

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
        Invoke-Download "$($colorful.baseUrl)$($source.path)" $target
    }

    $hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $source.sha256) {
        Remove-Item $target
        throw "SHA-256 が一致しません: $target（期待値 $($source.sha256)、実際 $hash）"
    }
}
Write-Host "ColorfulTalk $($colorful.version) のソースを $colorfulDir に取得しました"

# Slang（slangc）。ビルドする PC で動く版を native/tools/<host-rid>/slang/ に置く（アーカイブには先頭のフォルダーがない）
$slangHost = $deps.slang.hosts | Where-Object rid -eq $hostRid
if (-not $slangHost) {
    Write-Warning "Slang の版がない PC です: $hostRid"
}
elseif ($archive = Get-Archive 'slang' $deps.slang.version $slangHost.url $slangHost.sha256) {
    $slangDir = Join-Path $root "native/tools/$hostRid/slang"
    Expand-ArchiveTo $archive $slangDir 0
    & (Join-Path $slangDir 'bin/slangc.exe') -version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "slangc を実行できません: $slangDir" }
    Write-Host "Slang $($deps.slang.version)（slangc）を $slangDir に配置しました"
}

# MoltenVK は macOS だけ（tools/fetch-native.sh）。Vulkan-Headers は FFmpeg の Vulkan 対応のビルド用
if (Test-AppliesToRid $deps.vulkanHeaders) {
    $vk = $deps.vulkanHeaders
    if ($archive = Get-Archive 'vulkan-headers' $vk.version $vk.url $vk.sha256) {
        Expand-ArchiveTo $archive (Join-Path $downloads 'src-vulkan-headers') 1
        Write-Host "Vulkan-Headers $($vk.version) を取得しました"
    }
}

# VulkanMemoryAllocator のライセンス（ネイティブライブラリは NuGet の Vortice.VulkanMemoryAllocator に入っている）
if ($archive = Get-Archive 'vma' $deps.vma.version $deps.vma.licenseUrl $deps.vma.licenseSha256) {
    $licenses = Join-Path $outDir 'licenses'
    New-Item -ItemType Directory -Force $licenses | Out-Null
    Copy-Item $archive (Join-Path $licenses 'vma-LICENSE.txt') -Force
    Write-Host "VulkanMemoryAllocator $($deps.vma.version) のライセンスを $licenses に配置しました"
}

# OpenColorIO と依存するライブラリのソース（どの RID でも同じソースを、別のフォルダーでビルドする）
foreach ($source in $deps.ocio.sources) {
    if ($archive = Get-Archive $source.name $source.version $source.url $source.sha256) {
        Expand-ArchiveTo $archive (Join-Path $downloads "src-ocio-$($source.name)") 1
    }
}
Write-Host "OpenColorIO $($deps.ocio.version) と依存するライブラリのソースを取得しました（ビルドは tools/build-native.ps1）"

# FFmpeg と、FFmpeg に組み込む部品のソースを取得する（ビルドは tools/build-native）。rids がない部品は、すべての RID で使う
if ($deps.ffmpeg.sourceBuild.rids -notcontains $Rid) {
    Write-Warning "FFmpeg のビルドに対応していない RID です: $Rid"
}
else {
    $names = @()
    foreach ($source in $deps.ffmpeg.sourceBuild.sources) {
        if (-not (Test-AppliesToRid $source)) { continue }
        $names += $source.name
        if ($archive = Get-Archive $source.name $source.version $source.url $source.sha256) {
            Expand-ArchiveTo $archive (Join-Path $downloads "src-$Rid-$($source.name)") 1
        }
    }
    Write-Host "FFmpeg $($deps.ffmpeg.version) と部品（$(($names | Select-Object -Skip 1) -join ' ')）のソースを取得しました（ビルドは tools/build-native.ps1）"
}

if ($script:missingHash) {
    throw 'tools/native-deps.json に SHA-256 を書いてから、もう一度実行してください'
}
