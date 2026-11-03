# 同梱・使用しているソフトウェアのライセンス

NakkuntMovieMaker 自体は MIT ライセンスです（[LICENSE](LICENSE)）。配布物には、次のソフトウェアを含みます。

## 配布物に含めるもの

| 名称 | 版 | ライセンス | 入手先 |
| --- | --- | --- | --- |
| FFmpeg（共有ライブラリ） | 9.0.2 | LGPL-3.0-or-later（GPL の部品と x264 を含まない構成） | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)（ソースは [ffmpeg.org](https://ffmpeg.org/)） |
| miniaudio（`nmm_audio` に組み込み） | 0.11.25 | Unlicense または MIT No Attribution | [mackron/miniaudio](https://github.com/mackron/miniaudio) |
| BIZ UDPゴシック | Google Fonts の版 | SIL Open Font License 1.1 | [google/fonts](https://github.com/google/fonts/tree/main/ofl/bizudpgothic) |
| SkiaSharp・HarfBuzzSharp | 3.119.4・8.3.1.3 | MIT（中の Skia は BSD-3-Clause、HarfBuzz は Old MIT） | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) |
| Avalonia | 12.1.5 | MIT | [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |
| FFmpeg.AutoGen | 9.0.1.1 | LGPL-3.0 | [Ruslan-B/FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) |
| System.CommandLine | 2.0.14 | MIT | [dotnet/command-line-api](https://github.com/dotnet/command-line-api) |

- FFmpeg・miniaudio・BIZ UDPゴシックのライセンスの全文は、`tools/fetch-native` が `native/bin/<rid>/licenses/` に置き、配布物にも同梱します。
- FFmpeg は共有ライブラリ（DLL・.so）のまま同梱し、利用者が差し替えられるようにしています（LGPL の条件）。
  GPL の部品を含む FFmpeg（x264 など）は同梱しません。
- 版とハッシュは `tools/native-deps.json` と `Directory.Packages.props` で固定しています。

## 同梱しないもの（利用者が任意で導入する）

| 名称 | 説明 |
| --- | --- |
| AquesTalk1・AquesTalk2・AquesTalk10・AqKanji2Koe | 株式会社アクエストの製品。利用者が [A-quest](https://www.a-quest.com/) から入手し、その使用許諾に従って使う。営利目的での利用には使用ライセンスが必要 |
| SAPI の音声 | Windows に標準で入っているもの |

## テストにだけ使うもの

| 名称 | 説明 |
| --- | --- |
| xunit.v3・Avalonia.Headless・BenchmarkDotNet | テストと性能の計測（配布物には含めない） |
| FFmpeg（GPL の構成） | 開発者の PC で、テスト用の素材を作るときだけ使う。リポジトリと配布物には含めない |
