#ifndef NMM_AUDIO_H
#define NMM_AUDIO_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(NMM_AUDIO_BUILD)
#    define NMM_AUDIO_API __declspec(dllexport)
#  else
#    define NMM_AUDIO_API __declspec(dllimport)
#  endif
#else
#  define NMM_AUDIO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* このラッパーの API のバージョン。互換性のない変更をしたら上げる */
NMM_AUDIO_API int nmm_audio_api_version(void);

/* 組み込んでいる miniaudio のバージョン（例: "0.11.25"） */
NMM_AUDIO_API const char* nmm_audio_miniaudio_version(void);

/*
 * デバイスの一覧。capture が 0 なら出力、0 以外なら入力のデバイス。名前は、開くときにデバイスを選ぶのに使う。
 */
typedef struct nmm_device_list nmm_device_list;

NMM_AUDIO_API int nmm_devices_open(int null_backend, nmm_device_list** out);

NMM_AUDIO_API uint32_t nmm_devices_count(nmm_device_list* list, int capture);

/* デバイスの名前（UTF-8） */
NMM_AUDIO_API const char* nmm_devices_name(nmm_device_list* list, int capture, uint32_t index);

/* OS の既定のデバイスなら 1 */
NMM_AUDIO_API int nmm_devices_is_default(nmm_device_list* list, int capture, uint32_t index);

NMM_AUDIO_API void nmm_devices_close(nmm_device_list* list);

/*
 * 再生。音は float32・インターリーブで、リングバッファに書き込むと、デバイスが順に鳴らす。
 * デバイスのコールバックはリングバッファから読むだけなので、.NET のコードはオーディオのスレッドで動かない。
 * 書き込み（write・clear）は 1 つのスレッドから行うこと。
 */
typedef struct nmm_playback nmm_playback;

/* 出力デバイスを開く（まだ鳴らさない）。device_name が NULL なら既定のデバイス。
   null_backend が 0 以外なら、音を出さずに実時間で消費するだけの仮のデバイス（テスト用）を使う。
   成功なら 0、失敗なら miniaudio のエラー（負の値。名前のデバイスがなければ MA_NO_DEVICE） */
NMM_AUDIO_API int nmm_playback_open(uint32_t sample_rate, uint32_t channels, uint32_t buffer_frames, int null_backend, const char* device_name, nmm_playback** out);

NMM_AUDIO_API int nmm_playback_start(nmm_playback* playback);

NMM_AUDIO_API int nmm_playback_stop(nmm_playback* playback);

/* リングバッファに書き込めるフレームの数 */
NMM_AUDIO_API uint32_t nmm_playback_writable(nmm_playback* playback);

/* フレームを書き込む（待たない）。書き込めたフレームの数を返す */
NMM_AUDIO_API uint32_t nmm_playback_write(nmm_playback* playback, const float* frames, uint32_t frame_count);

/* デバイスに渡した（リングバッファから読み出された）フレームの合計。音が足りずに無音を出したぶんは数えない */
NMM_AUDIO_API uint64_t nmm_playback_frames_played(nmm_playback* playback);

/* 音が足りずに無音を出したフレームの合計 */
NMM_AUDIO_API uint64_t nmm_playback_underrun_frames(nmm_playback* playback);

/* デバイスに渡してから実際に鳴るまでの目安（デバイスの内部のバッファの大きさ、フレーム） */
NMM_AUDIO_API uint32_t nmm_playback_latency_frames(nmm_playback* playback);

/* まだ鳴らしていない音を捨てる（シークのとき）。止まっていればすぐに捨てて 1 を返す。
   鳴らしている途中なら、次のコールバックで捨てるよう頼んで 0 を返す（nmm_playback_clearing が 0 になるまで書き込まないこと） */
NMM_AUDIO_API int nmm_playback_clear(nmm_playback* playback);

/* 捨てるのを待っている間は 1 */
NMM_AUDIO_API int nmm_playback_clearing(nmm_playback* playback);

NMM_AUDIO_API void nmm_playback_close(nmm_playback* playback);

/*
 * 録音。デバイスのコールバックが入力の音をリングバッファに書き、読み出し（read）は 1 つのスレッドから行う。
 * リングバッファがいっぱいのときは、新しい音を捨てて数える。
 */
typedef struct nmm_capture nmm_capture;

/* 入力デバイスを開く（まだ録らない）。引数の意味は nmm_playback_open と同じ。
   null_backend の仮のデバイスは、実時間で無音を返す */
NMM_AUDIO_API int nmm_capture_open(uint32_t sample_rate, uint32_t channels, uint32_t buffer_frames, int null_backend, const char* device_name, nmm_capture** out);

NMM_AUDIO_API int nmm_capture_start(nmm_capture* capture);

NMM_AUDIO_API int nmm_capture_stop(nmm_capture* capture);

/* 録った音を読み出す（待たない）。読み出せたフレームの数を返す */
NMM_AUDIO_API uint32_t nmm_capture_read(nmm_capture* capture, float* frames, uint32_t frame_count);

/* リングバッファに書いたフレームの合計 */
NMM_AUDIO_API uint64_t nmm_capture_frames_captured(nmm_capture* capture);

/* リングバッファがいっぱいで捨てたフレームの合計 */
NMM_AUDIO_API uint64_t nmm_capture_overrun_frames(nmm_capture* capture);

/* 入力されてからコールバックに届くまでの目安（デバイスの内部のバッファの大きさ、フレーム） */
NMM_AUDIO_API uint32_t nmm_capture_latency_frames(nmm_capture* capture);

/* コールバック 1 回あたりのフレームの数の目安 */
NMM_AUDIO_API uint32_t nmm_capture_period_frames(nmm_capture* capture);

NMM_AUDIO_API void nmm_capture_close(nmm_capture* capture);

#ifdef __cplusplus
}
#endif

#endif /* NMM_AUDIO_H */
