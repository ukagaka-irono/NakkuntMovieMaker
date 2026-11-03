# NakkuntMovieMaker

Windows / Linux / macOS で動作する動画編集ソフトです。

> [!NOTE]
> このページに書かれている内容は、将来の実装に向けたメモ書きを含みます。
> 記載されている機能がすべて搭載されているとは限りませんのでご注意ください。

## 目次

- [動作環境](#動作環境)
- [使用しているエンジン・ライブラリ](#使用しているエンジンライブラリ)
- [対応している音声合成ソフト・サービス](#対応している音声合成ソフトサービス)

## 動作環境

| 項目 | 内容 |
| --- | --- |
| 対応 OS | Windows / Linux / macOS |
| フレームワーク | .NET 10.0 |

基本的には LTS 版の .NET を使用しますが、将来的に LTS 版以外の .NET を使用する可能性があります。

## 使用しているエンジン・ライブラリ

| 名称 | 用途 | 対応 OS |
| --- | --- | --- |
| AquesTalk1 | 音声合成エンジン | Windows / Linux / macOS |
| AquesTalk2 | 音声合成エンジン | Windows / Linux / macOS |
| AquesTalk10 | 音声合成エンジン | Windows / Linux / macOS |
| AqKanji2Koe-A | 言語処理エンジン | Windows / Linux / macOS |
| FFmpeg | 動画・音声処理 | — |

## 対応している音声合成ソフト・サービス

- A.I.VOICE
- AivisProject（AivisSpeech / AivisCloudAPI）
- Amazon Polly
- AquesTalk1 / AquesTalk2 / AquesTalk10（営利目的での利用には使用ライセンスが必要）
- CeVIO AI / CeVIO Creative Studio 7
- CoeFont（一部の音声は無料。それ以外は Enterprise 以上のプランが必要）
- COEIROINK
- ElevenLabs
- Google Cloud Text-to-Speech（別途 API キーが必要）
- Gemini Text-to-Speech（別途 API キーが必要）
- Grok Text-to-Speech（別途 API キーが必要）
- Irodori-TTS（CUDA 対応 GPU とローカルへのインストールが必要）
- ITVOICE
- Kotodama
- LMROID（配布・サポート終了）
- Microsoft Azure Text-to-Speech
- OpenAI Text-to-Speech（別途 API キーが必要）
- SAPI（Windows 標準搭載）
- SHAREVOX
- Style-Bert-VITS2
- TALQu3
- VOICEPEAK
- VOICEVOX
- VOICEVOX Nemo
- VoiSona Talk
- よみてのれゐ（配布・サポート終了）
- 唄詠２
