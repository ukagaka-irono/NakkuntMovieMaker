# テスト用の素材

FFmpeg の `lavfi`（色・テストパターン・正弦波を生成する機能）で作った素材です。著作物を含まないので、自由に使えます。
作り直すときは、次のコマンドを実行します（x264・x265・libvpx・libmp3lame を含む FFmpeg が必要。テストの素材を作るためだけに使い、アプリには同梱しない）。

```bash
G="-hide_banner -loglevel error -y"
O="-map_metadata -1 -fflags +bitexact -flags:v +bitexact -flags:a +bitexact"

# 赤 1 秒 → 青 1 秒の動画（320×240・30fps・H.264）と 440Hz の音（48kHz・ステレオ・AAC）
ffmpeg $G -f lavfi -i "color=c=red:s=320x240:r=30:d=1" -f lavfi -i "color=c=blue:s=320x240:r=30:d=1" \
  -f lavfi -i "sine=f=440:sample_rate=48000:d=2" -filter_complex "[0][1]concat=n=2:v=1:a=0[v]" \
  -map "[v]" -map 2 -ac 2 -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -g 15 \
  -c:a aac -b:a 48k -movflags +faststart $O red-blue-2s.mp4

# テストパターンの動画（320×180・25fps・VP9・音声なし）
ffmpeg $G -f lavfi -i "testsrc=s=320x180:r=25:d=1" -c:v libvpx-vp9 -b:v 100k -an $O testsrc-1s.webm

# 単色の画像（64×48）
ffmpeg $G -f lavfi -i "color=c=green:s=64x48" -frames:v 1 $O green-64x48.png
ffmpeg $G -f lavfi -i "color=c=yellow:s=64x48" -frames:v 1 -q:v 3 $O yellow-64x48.jpg

# 1000Hz の音（1 秒）
ffmpeg $G -f lavfi -i "sine=f=1000:sample_rate=48000:d=1" -c:a pcm_s16le $O tone-1s.wav
ffmpeg $G -f lavfi -i "sine=f=1000:sample_rate=44100:d=1" -ac 2 -c:a libmp3lame -b:a 64k $O tone-1s.mp3
```

## フレーム番号を色で埋め込んだ動画

各フレームの色から、そのフレームの番号 N を読み取れるようにした動画です（128×96、60 フレーム、H.264・B フレームあり）。
赤 = (N mod 8) × 32 + 16、緑 = floor(N ÷ 8) × 32 + 16、青 = 128。

```bash
IDX="format=rgb24,geq=r='mod(N\,8)*32+16':g='floor(N/8)*32+16':b='128'"

# 29.97fps
ffmpeg $G -f lavfi -i "color=c=black:s=128x96:r=30000/1001:d=3,$IDX" -frames:v 60 \
  -c:v libx264 -preset veryfast -crf 16 -g 12 -bf 3 -pix_fmt yuv420p $O frames-2997.mp4

# 10 秒・300 フレーム（30fps、キーフレームは 2 秒ごと）。番号が 64 以上になるので、青 = floor(N ÷ 64) × 32 + 16 も使う
ffmpeg $G -f lavfi -i "color=c=black:s=128x96:r=30:d=10,format=rgb24,geq=r='mod(N\,8)*32+16':g='mod(floor(N/8)\,8)*32+16':b='floor(N/64)*32+16'" \
  -frames:v 300 -c:v libx264 -preset veryfast -crf 16 -g 60 -bf 3 -pix_fmt yuv420p $O frames-long.mp4

# 可変フレームレート：30fps で 30 フレーム → 0.5 秒あけて → 30 フレーム
ffmpeg $G -f lavfi -i "color=c=black:s=128x96:r=30:d=3,$IDX,setpts='(N+if(gte(N\,30)\,15\,0))/30/TB'" \
  -frames:v 60 -fps_mode passthrough -c:v libx264 -preset veryfast -crf 16 -g 12 -bf 3 -pix_fmt yuv420p $O frames-vfr.mp4
```

## 色空間のタグ付きの動画

RGB (200, 60, 40) の単色を、BT.709・リミテッドレンジとしてタグを付けて書き出した動画です。

```bash
ffmpeg $G -f lavfi -i "color=c=0xC83C28:s=128x96:r=30:d=0.2" -vf "scale=out_color_matrix=bt709:out_range=tv" \
  -c:v libx264 -preset veryfast -crf 10 -pix_fmt yuv420p \
  -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv $O color-c83c28-bt709.mp4
```

次の 4 つも、同じ R'G'B' (200, 60, 40) を、それぞれの行列・範囲で YUV にした動画です（`VideoColorTests`）。
タグは `setparams`（H.264）と x265 の設定（HEVC）で付けます。BT.601 は 720p にして、大きさからの推測（BT.709）ではなくタグが使われることを、
BT.709 フルレンジは 128×96 にして、推測（BT.601）ではなくタグが使われることを確かめます。
PQ の動画には HDR のメタデータ（マスタリングのディスプレイと、コンテンツの明るさ）を付けます。x265 で 2 回書き出すのは、
1 回目では SEI にしか入らず、2 回目でコンテナ（MP4 の mdcv・clli）にも入るためです。

```bash
C="color=c=0xC83C28:r=30:d=0.2"

# BT.601（SMPTE 170M）・リミテッド・1280×720
ffmpeg $G -f lavfi -i "$C:s=1280x720"   -vf "scale=out_color_matrix=bt601:out_range=tv,setparams=range=tv:color_primaries=smpte170m:color_trc=smpte170m:colorspace=smpte170m"   -c:v libx264 -preset veryfast -crf 10 -pix_fmt yuv420p $O color-c83c28-bt601-720p.mp4

# BT.709・フルレンジ・128×96
ffmpeg $G -f lavfi -i "$C:s=128x96"   -vf "scale=out_color_matrix=bt709:out_range=pc,setparams=range=pc:color_primaries=bt709:color_trc=bt709:colorspace=bt709"   -c:v libx264 -preset veryfast -crf 10 -pix_fmt yuv420p $O color-c83c28-bt709-full.mp4

# BT.2020・PQ・10 ビット（HEVC Main 10）。マスタリングのディスプレイは P3-D65・0.005〜1000cd/m²、MaxCLL 1000・MaxFALL 400
ffmpeg $G -f lavfi -i "$C:s=128x96" -vf "scale=out_color_matrix=bt2020:out_range=tv,format=yuv420p10le"   -c:v libx265 -preset veryfast -crf 10   -x265-params "log-level=error:hdr10=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"   -tag:v hvc1 $O pq-sei.mp4
ffmpeg $G -i pq-sei.mp4 -c:v libx265 -preset veryfast -crf 10 -x265-params log-level=error -tag:v hvc1 $O color-c83c28-bt2020-pq.mp4

# BT.2020・HLG・10 ビット（HEVC Main 10）
ffmpeg $G -f lavfi -i "$C:s=128x96" -vf "scale=out_color_matrix=bt2020:out_range=tv,format=yuv420p10le"   -c:v libx265 -preset veryfast -crf 10 -x265-params "log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc"   -colorspace bt2020nc -color_primaries bt2020 -color_trc arib-std-b67 -color_range tv -tag:v hvc1 $O color-c83c28-bt2020-hlg.mp4
```

## 映像より遅れて始まる音声

映像（64×48・30fps・1 秒）は 0 秒から、1000Hz の音（48kHz・モノラル・PCM）は 0.5 秒から 0.5 秒間です。
PCM キャッシュが先頭に無音を入れて、映像と位置を合わせるかを確かめます。

```bash
ffmpeg $G -f lavfi -i "color=c=black:s=64x48:r=30:d=1" -itsoffset 0.5 -f lavfi -i "sine=f=1000:sample_rate=48000:d=0.5" \
  -map 0 -map 1 -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -c:a pcm_s16le $O late-audio.mkv
```

`not-media.txt` は、メディアではないファイルを開いたときのエラーを確かめるためのものです。
