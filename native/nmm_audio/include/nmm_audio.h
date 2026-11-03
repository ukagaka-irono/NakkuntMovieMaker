#ifndef NMM_AUDIO_H
#define NMM_AUDIO_H

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

#ifdef __cplusplus
}
#endif

#endif /* NMM_AUDIO_H */
