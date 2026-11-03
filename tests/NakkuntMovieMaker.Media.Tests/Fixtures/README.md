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

`not-media.txt` は、メディアではないファイルを開いたときのエラーを確かめるためのものです。
