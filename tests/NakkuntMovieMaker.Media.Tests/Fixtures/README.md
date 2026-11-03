# テスト用の素材

FFmpeg の `lavfi`（色・テストパターン・正弦波を生成する機能）で作った素材です。著作物を含まないので、自由に使えます。
作り直すときは、次のコマンドを実行します（x264・libvpx・libmp3lame を含む FFmpeg が必要）。

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

## 映像より遅れて始まる音声

映像（64×48・30fps・1 秒）は 0 秒から、1000Hz の音（48kHz・モノラル・PCM）は 0.5 秒から 0.5 秒間です。
PCM キャッシュが先頭に無音を入れて、映像と位置を合わせるかを確かめます。

```bash
ffmpeg $G -f lavfi -i "color=c=black:s=64x48:r=30:d=1" -itsoffset 0.5 -f lavfi -i "sine=f=1000:sample_rate=48000:d=0.5" \
  -map 0 -map 1 -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p -c:a pcm_s16le $O late-audio.mkv
```

`not-media.txt` は、メディアではないファイルを開いたときのエラーを確かめるためのものです。
