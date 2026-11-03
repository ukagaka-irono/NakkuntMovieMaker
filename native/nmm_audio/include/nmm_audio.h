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
 * 再生。音は float32・インターリーブで、リングバッファに書き込むと、デバイスが順に鳴らす。
 * デバイスのコールバックはリングバッファから読むだけなので、.NET のコードはオーディオのスレッドで動かない。
 * 書き込み（write・clear）は 1 つのスレッドから行うこと。
 */
typedef struct nmm_playback nmm_playback;

/* 既定の出力デバイスを開く（まだ鳴らさない）。null_backend が 0 以外なら、音を出さずに実時間で消費するだけの
   仮のデバイス（テスト用）を使う。成功なら 0、失敗なら miniaudio のエラー（負の値） */
NMM_AUDIO_API int nmm_playback_open(uint32_t sample_rate, uint32_t channels, uint32_t buffer_frames, int null_backend, nmm_playback** out);

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

#ifdef __cplusplus
}
#endif

#endif /* NMM_AUDIO_H */
