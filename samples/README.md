# サンプルプロジェクト

| フォルダ | 内容 |
| --- | --- |
| `general/` | 一般的な動画編集のサンプル（10 秒）。背景の画像、動画、ロゴ、図形、文字のアニメーション（キーフレームとイージング）、エフェクト（色調補正・影・発光）、トランジション（クロスフェード・ワイプ）、縦書きと一文字ずつの表示、BGM（フェード） |
| `yukkuri/` | ゆっくり解説のサンプル（12 秒）。2 人のキャラクター（パーツ式の立ち絵・口パク・目パチ）、セリフと字幕、BGM（セリフの間は下げる） |

`.nmmproj` をアプリで開くか、コマンドラインで書き出せます。

```
nmm export samples/general/general.nmmproj -o general.mp4 --preset youtube-1080p
```

素材はすべて `tests/NakkuntMovieMaker.E2E.Tests/SampleGenerator.cs` がプログラムで作ったもので、自由に使えます。
セリフの声は ColorfulTalk（同梱の音声合成）で作ったものです。漢字を含むセリフには、読み（記号列）を付けてあります。

サンプルを作り直すときは、次を実行します。

```
NMM_GENERATE_SAMPLES=1 dotnet test --project tests/NakkuntMovieMaker.E2E.Tests -- --filter-class "*SampleGenerator"
```
