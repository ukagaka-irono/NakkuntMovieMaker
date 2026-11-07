# 同梱・使用しているソフトウェアのライセンス

NakkuntMovieMaker 自体は MIT ライセンスです（[LICENSE](LICENSE)）。配布物には、次のソフトウェアを含みます。

## 配布物に含めるもの

| 名称 | 版 | ライセンス | 入手先 |
| --- | --- | --- | --- |
| FFmpeg（共有ライブラリ） | 9.0.2 | LGPL-2.1-or-later（GPL の部品を含まず、`--enable-version3` も付けない構成） | [ffmpeg.org](https://ffmpeg.org/) のソースから `tools/build-native`（`tools/build-ffmpeg.sh`）でビルド |
| OpenH264（FFmpeg に組み込み） | 2.6.0 | BSD-2-Clause | [cisco/openh264](https://github.com/cisco/openh264)（ソースからビルド） |
| kvazaar（FFmpeg に組み込み） | 2.3.2 | BSD-3-Clause | [ultravideo/kvazaar](https://github.com/ultravideo/kvazaar)（ソースからビルド） |
| nv-codec-headers（Windows・Linux の FFmpeg に組み込み。NVENC・NVDEC） | 13.0.19.1 | MIT | [FFmpeg/nv-codec-headers](https://github.com/FFmpeg/nv-codec-headers)（ヘッダーだけ） |
| AMF のヘッダー（Windows の FFmpeg に組み込み） | 1.5.3 | MIT | [GPUOpen-LibrariesAndSDKs/AMF](https://github.com/GPUOpen-LibrariesAndSDKs/AMF)（ヘッダーだけ） |
| oneVPL（libvpl。Windows の FFmpeg に組み込み。QSV） | 2.17.0 | MIT | [intel/libvpl](https://github.com/intel/libvpl)（ソースからビルド） |
| zlib（Windows の FFmpeg に組み込み） | 1.3.2 | zlib | [MSYS2](https://packages.msys2.org/base/mingw-w64-zlib) の静的ライブラリ（Linux・macOS は OS のものを使い、同梱しない） |
| winpthreads（Windows の FFmpeg に組み込み） | 14.0.0（MSYS2 の版） | MIT・BSD-3-Clause | [MSYS2](https://packages.msys2.org/base/mingw-w64-winpthreads) の静的ライブラリ |
| ColorfulTalk（旧版） | 1.0.0（コミット 5835079） | MIT | [ukagaka-irono/colorful-talk](https://github.com/ukagaka-irono/colorful-talk)（`tools/fetch-native` でソースを取得してビルド） |
| miniaudio（`nmm_audio` に組み込み） | 0.11.25 | Unlicense または MIT No Attribution | [mackron/miniaudio](https://github.com/mackron/miniaudio) |
| OpenColorIO（`nmm_ocio` に組み込み） | 2.5.2 | BSD-3-Clause（中に取り込まれた xxHash・SampleICC・argparse などは、同梱する `opencolorio-THIRD-PARTY.txt` のとおり） | [AcademySoftwareFoundation/OpenColorIO](https://github.com/AcademySoftwareFoundation/OpenColorIO)（ソースからビルド） |
| Expat（`nmm_ocio` に組み込み） | 2.10.0 | MIT | [libexpat/libexpat](https://github.com/libexpat/libexpat)（ソースからビルド） |
| yaml-cpp（`nmm_ocio` に組み込み） | 0.8.0 | MIT | [jbeder/yaml-cpp](https://github.com/jbeder/yaml-cpp)（ソースからビルド） |
| Imath（`nmm_ocio` に組み込み） | 3.1.12 | BSD-3-Clause | [AcademySoftwareFoundation/Imath](https://github.com/AcademySoftwareFoundation/Imath)（ソースからビルド） |
| pystring（`nmm_ocio` に組み込み） | 1.1.4 | zlib と同じ条件 | [imageworks/pystring](https://github.com/imageworks/pystring)（ソースからビルド） |
| minizip-ng（`nmm_ocio` に組み込み） | 4.0.10 | zlib | [zlib-ng/minizip-ng](https://github.com/zlib-ng/minizip-ng)（ソースからビルド） |
| zlib（`nmm_ocio` に組み込み） | 1.3.2 | zlib | [madler/zlib](https://github.com/madler/zlib)（ソースからビルド。FFmpeg の zlib とは別） |
| MoltenVK（macOS だけ） | 1.4.2 | Apache-2.0 | [KhronosGroup/MoltenVK](https://github.com/KhronosGroup/MoltenVK)（公式のビルド済みのもの） |
| Vulkan-Headers（Windows・Linux の FFmpeg に組み込み） | 1.4.363 | Apache-2.0 または MIT | [KhronosGroup/Vulkan-Headers](https://github.com/KhronosGroup/Vulkan-Headers)（ヘッダーだけ。FFmpeg を Vulkan 対応でビルドするときに使う） |
| Vortice.Vulkan | 3.3.0 | MIT | [amerkoleci/Vortice.Vulkan](https://github.com/amerkoleci/Vortice.Vulkan)（Vulkan を .NET から呼ぶ部分） |
| Vortice.VulkanMemoryAllocator | 1.8.0 | MIT | [amerkoleci/Vortice.Vulkan](https://github.com/amerkoleci/Vortice.Vulkan)（中のネイティブライブラリ `vma.dll`・`libvma` は、AMD の [VulkanMemoryAllocator](https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator) 3.4.0。MIT） |
| BIZ UDPゴシック | Google Fonts の版 | SIL Open Font License 1.1 | [google/fonts](https://github.com/google/fonts/tree/main/ofl/bizudpgothic) |
| SkiaSharp・HarfBuzzSharp | 3.119.4・8.3.1.3 | MIT（中の Skia は BSD-3-Clause、HarfBuzz は Old MIT） | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) |
| Avalonia | 12.1.5 | MIT | [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |
| FFmpeg.AutoGen | 9.0.1.1 | LGPL-3.0 | [Ruslan-B/FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) |
| System.CommandLine | 2.0.14 | MIT | [dotnet/command-line-api](https://github.com/dotnet/command-line-api) |

- FFmpeg と、FFmpeg に組み込んだ部品（上の表の「FFmpeg に組み込み」）・miniaudio・OpenColorIO と `nmm_ocio` に組み込んだ部品・MoltenVK・VulkanMemoryAllocator・ColorfulTalk・BIZ UDPゴシックのライセンスの全文は、`tools/fetch-native`・`tools/build-native` が `native/bin/<rid>/licenses/` に置き、配布物にも同梱します。
  ライセンスのファイルがない部品（nv-codec-headers・AMF のヘッダー）は、ヘッダーの先頭に書かれたライセンスを集めて置きます。
- Windows の FFmpeg は MSYS2 の gcc でビルドし、libgcc・libstdc++ を静的にリンクしています。これらは GCC Runtime Library Exception により、表記や同梱の義務はありません。
- FFmpeg は `--disable-autodetect` で、上の表の部品だけを組み込みます（GPL の部品と、GPL のライブラリを使う部品は含めません）。
- FFmpeg は共有ライブラリ（DLL・.so）のまま同梱し、利用者が差し替えられるようにしています（LGPL の条件）。
  GPL の部品を含む FFmpeg（x264 など）は同梱しません。
- 版とハッシュは `tools/native-deps.json` と `Directory.Packages.props` で固定しています。

- OpenColorIO と、それが依存するライブラリは静的ライブラリにして `nmm_ocio`（共有ライブラリ）に組み込みます。OCIO の構成ファイルは同梱せず、OCIO に組み込まれた ACES の構成を使います。
- GPU 版のシェーダーは、ビルドのときに Slang（slangc）で SPIR-V にして、`NakkuntMovieMaker.Rendering.Vulkan` に埋め込みます。slangc 自体は同梱しません。

## ビルドにだけ使うもの（配布物には含めない）

| 名称 | 版 | ライセンス | 説明 |
| --- | --- | --- | --- |
| Slang（slangc） | 2026.21 | Apache-2.0 WITH LLVM-exception | シェーダーを SPIR-V にコンパイルする。[shader-slang/slang](https://github.com/shader-slang/slang) の公式のビルド済みのものを `tools/fetch-native` で取得する |

## 同梱しないもの

| 名称 | 説明 |
| --- | --- |
| SAPI の音声 | Windows に標準で入っているもの |
| Vulkan のローダー（`vulkan-1.dll`・`libvulkan.so.1`） | Windows・Linux では、GPU のドライバーや OS に入っているものを使う（macOS は MoltenVK を直接読み込む） |
| 外部の ffmpeg（x264・x265） | x264・x265 で書き出したい人が、自分で入手して設定で指定するもの（GPL）。ソフトは別のプロセスとして呼ぶだけで、同梱しない |

## テストにだけ使うもの

| 名称 | 説明 |
| --- | --- |
| xunit.v3・Avalonia.Headless・BenchmarkDotNet | テストと性能の計測（配布物には含めない） |
| FFmpeg（GPL の構成） | 開発者の PC で、テスト用の素材を作るときだけ使う。リポジトリと配布物には含めない |
