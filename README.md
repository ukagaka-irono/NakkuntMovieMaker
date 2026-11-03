# NakkuntMovieMaker

Windows / Linux / macOS で動作する動画編集ソフトです。

> [!NOTE]
> このページに書かれている内容は、将来の実装に向けたメモ書きを含みます。
> 記載されている機能がすべて搭載されているとは限りませんのでご注意ください。

## 目次

- [動作環境](#動作環境)
- [主な機能](#主な機能)
- [使用しているエンジン・ライブラリ](#使用しているエンジンライブラリ)
- [対応している音声合成ソフト・サービス](#対応している音声合成ソフトサービス)

## 動作環境

| 項目 | 内容 |
| --- | --- |
| 対応 OS | Windows / Linux / macOS |
| フレームワーク | .NET 10.0 |

基本的には LTS 版の .NET を使用しますが、将来的に LTS 版以外の .NET を使用する可能性があります。

## 主な機能

- プラグインによる機能の拡張
  - 音声合成：新しい音声合成ソフト・サービスへの対応
  - 映像エフェクト
  - 音声エフェクト
  - トランジション
  - 図形
  - ファイルの読み込み：動画・画像・音声ファイルの対応形式
  - 動画の出力：出力形式・エンコーダー
  - ツール：編集を補助するツールやウィンドウ
- 他ソフトのプラグインとの互換性（可能な限り対応）
  - [ゆっくりMovieMaker4（YMM4）](https://manjubox.net/ymm4/)
  - [AviUtl](https://spring-fragrance.mints.ne.jp/aviutl/)
  - [AviUtl2（AviUtl ExEdit2）](https://spring-fragrance.mints.ne.jp/aviutl/)

## 使用しているエンジン・ライブラリ

| 名称 | 用途 | 対応 OS |
| --- | --- | --- |
| [AquesTalk1](https://www.a-quest.com/products/aquestalk_1.html) | 音声合成エンジン | Windows / Linux / macOS |
| [AquesTalk2](https://www.a-quest.com/products/aquestalk_2.html) | 音声合成エンジン | Windows / Linux / macOS |
| [AquesTalk10](https://www.a-quest.com/products/aquestalk10.html) | 音声合成エンジン | Windows / Linux / macOS |
| [AqKanji2Koe-A](https://www.a-quest.com/products/aqkanji2koe.html) | 言語処理エンジン | Windows / Linux / macOS |
| [FFmpeg](https://ffmpeg.org/) | 動画・音声処理 | — |

## 対応している音声合成ソフト・サービス

- [A.I.VOICE](https://aivoice.jp/)
- AivisProject（[AivisSpeech](https://aivis-project.com/) / [AivisCloudAPI](https://hub.aivis-project.com/cloud-api/)）
- [Amazon Polly](https://aws.amazon.com/jp/polly/)
- [AquesTalk1](https://www.a-quest.com/products/aquestalk_1.html) / [AquesTalk2](https://www.a-quest.com/products/aquestalk_2.html) / [AquesTalk10](https://www.a-quest.com/products/aquestalk10.html)（営利目的での利用には使用ライセンスが必要）
- [CeVIO AI / CeVIO Creative Studio 7](https://cevio.jp/)
- [CoeFont](https://coefont.cloud/)（一部の音声は無料。それ以外は Enterprise 以上のプランが必要）
- [COEIROINK](https://coeiroink.com/)
- [ElevenLabs](https://elevenlabs.io/)
- [Google Cloud Text-to-Speech](https://cloud.google.com/text-to-speech)（別途 API キーが必要）
- [Gemini Text-to-Speech](https://ai.google.dev/gemini-api/docs/speech-generation)（別途 API キーが必要）
- [Grok Text-to-Speech](https://docs.x.ai/developers/model-capabilities/audio/text-to-speech)（別途 API キーが必要）
- [Irodori-TTS](https://github.com/Aratako/Irodori-TTS)（CUDA 対応 GPU とローカルへのインストールが必要）
- [ITVOICE](https://booth.pm/ja/items/4374126)
- [Kotodama](https://lp.kotodama.go-spiral.ai/)
- [Microsoft Azure Text-to-Speech](https://azure.microsoft.com/ja-jp/products/ai-foundry/tools/speech)
- [OpenAI Text-to-Speech](https://developers.openai.com/api/docs/guides/text-to-speech)（別途 API キーが必要）
- [SAPI](https://learn.microsoft.com/ja-jp/previous-versions/windows/desktop/ee125663%28v=vs.85%29)（Windows 標準搭載）
- [SHAREVOX](https://www.sharevox.app/)
- [Style-Bert-VITS2](https://github.com/litagin02/Style-Bert-VITS2)
- [TALQu3](https://haruqa.github.io/TALQu/)
- [VOICEPEAK](https://www.ah-soft.com/voice/)
- [VOICEVOX](https://voicevox.hiroshiba.jp/)
- [VOICEVOX Nemo](https://voicevox.hiroshiba.jp/nemo/)
- [VoiSona Talk](https://voisona.com/talk/)
- [唄詠２](https://shinta.coresv.com/software/utayomi2-ja-jp/)
- [棒読みちゃん](https://chi.usamimi.info/Program/Application/BouyomiChan/)
