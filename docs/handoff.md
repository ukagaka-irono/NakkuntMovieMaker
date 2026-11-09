# 引き継ぎ資料（2026-11-09）

NakkuntMovieMaker の作業を引き継ぐ人（Claude を含む）向けの資料です。最初にこの文書を読んでから作業してください。
細かい設計は [rendering.md](rendering.md)（GPU 版の描画・色・動画の受け取り方）と [performance.md](performance.md)（性能の記録）にあります。

## 1. 今の状態

| 項目 | 状態 |
| --- | --- |
| M1（一般的な動画編集・ゆっくり解説動画の制作） | 完了。v0.0.1-beta・v0.0.1.1-beta として公開済み |
| M2（GPU での描画・色の管理・プロキシ編集） | 完了。v0.0.2-beta（プレリリース）として公開済み |
| main | 6dd025e（未コミットの変更なし）。CI は Windows・Ubuntu・macOS ですべて成功 |
| 次の段階（M3） | まだ決めていません。始める前に、利用者に内容を確かめてください |

M2 で入れたもの（詳しくは rendering.md）：

- Vulkan での描画（Vortice.Vulkan 3.3.0。シェーダーは Slang で SPIR-V にしてアセンブリに埋め込む）。エフェクト 15 種類・トランジション 7 種類・文字と図形（Skia の GPU 版）を GPU で描く。使えないときは CPU（Skia）で描く
- 作業用の画像は 16 ビット浮動小数点の premultiplied RGBA で、どの GPU でも動くように画像ではなくバッファーに置く
- OpenColorIO 2.5.2（`nmm_ocio`）。変換はシェーパーと 65³ の 3D LUT に焼いて、CPU と GPU で同じように引く
- 動画の受け取り方：Vulkan Video（試験的・既定では使わない）→ D3D11 からの取り込み（Windows。H.264 以外）→ YUV の平面（GPU で RGB にする）→ BGRA
- 書き出しは、GPU が NV12 を作ってエンコーダーに渡す
- プロキシ（H.264、1/2・最大 1080p）はプレビューだけで使い、書き出しでは元の素材を使う
- 画面：設定の「描画」、状態バーの描画の方式、プロジェクトの設定の色とプロキシ、素材の一覧の入力の色空間とプロキシ

M2 の性能の目標（基準の PC。すべて達成）：

| 目標 | 結果 |
| --- | --- |
| フル解像度・8 レイヤー＋1080p60 の H.264＋3 レイヤーにエフェクトで 60fps、ドロップ 1% 以下 | 0.2% |
| 10 分の動画の書き出しが実時間の 3 倍以上 | 3.90 倍 |
| 4K の素材をプロキシで 1/2 プレビュー、60fps | ドロップ 0% |
| GPU のメモリ 1GB 以下 | 最大 549MB |

## 2. 利用者との約束（必ず守ること）

- **返事は日本語で。** コメント・文書・コミットメッセージも日本語で、平易な文にする（既存のファイルの書き方に合わせる）。
- **コミット**：手順ごとに、ビルドとテストが通ったら、確認せずにコミットしてよい。メッセージは日本語で、最後に次の行を付ける。

  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  ```

- **プッシュ**：main へのプッシュ、タグ、GitHub のリリースの作成は、その都度、利用者に確かめてから行う（前に許可されても、次の回はまた確かめる）。
  `ci/` で始まるブランチ（CI でしか確かめられない問題を調べるため）へのプッシュは、確かめずに行ってよい。
- **ダウンロード**：NuGet の復元以外は、取得する前に確かめる（M2 の一覧（Slang・MoltenVK・Vulkan-Headers・OCIO と依存・Vortice・VMA）は許可済み）。
  新しい依存は `tools/native-deps.json` に版と SHA-256 を固定する。
- **GPL の FFmpeg**：PATH にある ffmpeg（GPL 版）は、テスト用の素材を作るためだけに使う。配布物にも、リポジトリにも入れない（`tools/package.ps1` が確かめる）。
- **AquesTalk には対応しない。** 音声合成は ColorfulTalk（利用者が作っているエンジン。旧版 v1 を同梱）と SAPI。新しい ColorfulTalk ができたら、旧版を置き換えずに別のエンジンとして加える。
- **本物のマイクで録音しない**（確かめる前に利用者に聞く）。テストは仮の入力デバイスで行う。
- **性能を測るとき**は、先に CPU の負荷を確かめる。この PC では IntelliJ IDEA・Java のコンパイル・Roblox などが CPU を数コア使うことがあり、結果が数倍揺れる。負荷が高ければ、閉じてもらうよう利用者に頼む。

## 3. 環境

| 項目 | 内容 |
| --- | --- |
| リポジトリ | https://github.com/ukagaka-irono/NakkuntMovieMaker（作業フォルダ `C:\Users\yukin\IdeaProjects\NakkuntMovieMaker`） |
| PC | Let's note SV1（Core i5-1145G7・Iris Xe・16GB）、Windows 11 Pro。シェルは PowerShell 7 と Git Bash |
| .NET | 10.0.303（`global.json`）。テストは Microsoft.Testing.Platform（xunit.v3） |
| 主な依存 | Avalonia 12.1.5、SkiaSharp 3.119.4、FFmpeg 9.0.2（LGPL・ソースからビルド）、OpenColorIO 2.5.2、Vortice.Vulkan 3.3.0、Slang 2026.21 |

### ビルドとテスト

```powershell
pwsh tools/fetch-native.ps1      # 依存の取得（版とハッシュは tools/native-deps.json）。Git Bash からは実行しない（tar が C: のパスを扱えない）
pwsh tools/build-native.ps1      # nmm_audio・nmm_ocio・ColorfulTalk・FFmpeg のビルド（初回は数分）
dotnet build NakkuntMovieMaker.slnx -c Release
dotnet test --solution NakkuntMovieMaker.slnx -c Release
```

- 警告はエラーになります（`TreatWarningsAsErrors`）。
- 一部だけ回す：`dotnet test tests/NakkuntMovieMaker.Rendering.Vulkan.Tests -c Release --filter-class "*ParityTests"`（`--filter-method "*名前*"`、ビルド済みなら `--no-build`、1 件ずつの結果は `--output detailed`）。
  `--project` を使うときは、フォルダーではなく `.csproj` のパスを書く。
- GPU に触れる変更は、`NMM_VULKAN_VALIDATION=1` でも Rendering.Vulkan.Tests を回し、検証レイヤーのエラーがないことを確かめる。
- 配布用の zip：`pwsh tools/package.ps1`（`-SkipNative` で依存の用意を省く）。`artifacts/` に作り、SHA-256 を表示する。

### コマンドライン（nmm）

```powershell
dotnet run --project src/NakkuntMovieMaker.Cli -c Release -- probe --gpu      # GPU と自己テストの結果
dotnet run --project src/NakkuntMovieMaker.Cli -c Release -- export samples/general/general.nmmproj -o out.mp4
dotnet run --project src/NakkuntMovieMaker.Cli -c Release -- bench --project samples/general/general.nmmproj --only export
dotnet run --project src/NakkuntMovieMaker.Cli -c Release -- bench --video <1080p60 の動画> --only playback60
```

### 主な環境変数

| 変数 | 意味 |
| --- | --- |
| `NMM_REQUIRE_FFMPEG`・`_OCIO`・`_VULKAN`・`_FONTS`・`_NMM_AUDIO`・`_COLORFULTALK` | `1` なら、使えないときにテストをスキップせず失敗にする（CI で使う） |
| `NMM_VULKAN_VALIDATION=1` | 検証レイヤーを使う |
| `NMM_VULKAN_ALLOW_SOFTWARE=1` | CPU で動く Vulkan（lavapipe）も使う |
| `NMM_VULKAN_DISABLE=1` | GPU を使わない |
| `NMM_VULKAN_VIDEO=1` | Vulkan Video でデコードする（試験的） |
| `NMM_TEST_VULKAN_VIDEO=1` | Vulkan Video のテストを回す（落ちることがあるので既定では回さない） |
| `NMM_D3D11_IMPORT=0` | D3D11 からの取り込みを使わない |
| `NMM_D3D11_H264=1` | H.264 も D3D11 から取り込む（既定ではソフトウェアでデコード） |
| `NMM_SKIA_GPU=0` | 文字と図形を CPU で描いて送る |
| `NMM_PARITY_DUMP=<フォルダー>` | ParityTests で、CPU 版と GPU 版の画像を書き出す |
| `NMM_UPDATE_GOLDEN=1` | ゴールデン画像を作り直す（CPU 版の見た目を変えたときだけ） |

## 4. コードの地図

| プロジェクト | 中身 |
| --- | --- |
| `Core` | プロジェクトのモデル（不変）、編集の操作（元に戻せる `IOperation`）、保存の形式（スキーマ 2、`ProjectMigration`） |
| `Media` | FFmpeg（デコード・エンコード・プロキシ・キャッシュ）、D3D11 の共有テクスチャ（`D3D11VideoDevice`） |
| `Rendering` | 描画の抽象（`IRenderBackend`・`FramePlan`）、色の LUT |
| `Rendering.Skia` | CPU 版の描画（正しい結果の基準。ゴールデン画像で確かめる） |
| `Rendering.Vulkan` | GPU 版の描画（`VulkanBackend`・`GpuScheduler`・`SkiaGpu`・`FfmpegD3D11`・シェーダーは `Shaders/*.slang`） |
| `ColorManagement` | OpenColorIO（`nmm_ocio` を呼ぶ）と、変換を LUT に焼く処理 |
| `Engine` | タイムラインの評価、プレビューの再生（`FrameScheduler`・`PlaybackController`）、書き出し（`ExportPipeline`）、素材の読み出し（`ProjectMediaSource`）、描画の方式の選択（`RenderBackendFactory`）、ベンチマーク |
| `App` | Avalonia の画面（MVVM。`EditorSession` が編集の状態を持つ） |
| `Cli` | `nmm`（export・render-frame・probe・bench・proxy） |
| `Audio`・`Text`・`Voice*` | 音の再生と録音（miniaudio）、文字の配置、音声合成 |

テストは `tests/` に、プロジェクトごとにあります。GPU 版は `ParityTests`（CPU 版と同じ場面を描いて PSNR で比べる。基準は rendering.md の表）で確かめます。

## 5. 作業のこつと注意

- 画面（`App`）を変えるときは、既存の画面の書き方（`Themes/Styles.axaml` のクラス `field-label`・`secondary`・`warning`、`Space1`〜`Space4`、選択肢は `ToString()` が名前を返す小さな record）に合わせる。
- GPU 版の変更は、CPU 版と比べるテスト（ParityTests）と、検証レイヤーで確かめる。
- macOS（MoltenVK）は SPIR-V を Metal のシェーダーに変換するので、シェーダーの変数名に Metal の予約語（`device`・`constant`・`thread` など）を使わない（`SpirvTests` が確かめる）。macOS の問題は CI でしか確かめられないので、`ci/` のブランチにプッシュして、`gh run view <id> --log` で macOS のジョブのログ（「GPU の診断（macOS）」の手順に MoltenVK のエラーが出る）を見る。
- Skia の GPU 版は GPU のスレッドだけで使い、キューに送るときは `VulkanDevice.QueueLock` を取る（FFmpeg の Vulkan Video も同じ錠を使う）。
- 性能の測定の結果は、測った条件（負荷・回数・揺れ）とともに performance.md に追記する。

## 6. 残っていること・わかっている問題（次の候補）

1. **次の段階（M3）の内容を利用者と決める。** README の「機能一覧」に、今後の機能（3D・AI・コンポジット・カラーグレーディングなど）が並んでいます。HDR の出力と広色域の作業空間は M8 の予定です。
2. **Vulkan Video**：FFmpeg 9.0.2 と Iris Xe のドライバーで、デコードの途中でプロセスごと落ちる（`ffmpeg -hwaccel vulkan` でも同じ）。FFmpeg が更新されたら試し直す（新しい版の取得は利用者に確かめる）。
3. **立ち絵**はまだ CPU で描いて送る。GPU の Skia で描くには、パーツの画像を GPU の画像として覚えておく仕組みが要る。
4. **確かめていない環境**：NVIDIA・AMD の GPU、macOS の実機、Linux の GPU のある実機（Linux の FFmpeg には VA-API を入れていない）、本物のマイクでの録音、実際の立ち絵の素材。
5. **配布物**は Windows x64 だけ（`tools/package.ps1`）。Linux・macOS の配布物はまだない。
6. **プレビューの表示**は、描いた結果を毎フレーム読み戻して WriteableBitmap に写している（1080p で 1 枚約 1ms で、今は間に合っている）。もっと速くするなら、Avalonia の GPU 連携で読み戻しをなくす。
7. **揺れるテスト**：`TimelinePerformanceTests.ScrollsThroughThreeThousandItems` は、ほかの処理が重いと失敗することがある（単独で回し直すと通る）。CI の Windows で、QSV の「Cannot allocate memory」がまれに出る（回し直すと通る）。

## 7. 参考

- 計画のファイル（リポジトリの外）：`C:\Users\yukin\.claude\plans\agile-wobbling-taco.md`（M2 の最後の作業 K-1〜K-4 の計画）、`stateful-jingling-rainbow.md`（M1）
- リリースノート：[release-notes/0.0.2-beta.md](release-notes/0.0.2-beta.md)
- ライセンス：[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。同梱するネイティブライブラリのライセンスの全文は、`tools/fetch-native`・`build-native` が `native/bin/<rid>/licenses/` に置く
