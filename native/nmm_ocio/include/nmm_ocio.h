#ifndef NMM_OCIO_H
#define NMM_OCIO_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(NMM_OCIO_BUILD)
#    define NMM_OCIO_API __declspec(dllexport)
#  else
#    define NMM_OCIO_API __declspec(dllimport)
#  endif
#else
#  define NMM_OCIO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * OpenColorIO（C++）を .NET から使うための薄い C ラッパー。
 * 文字列はすべて UTF-8。返す文字列は、それを返したオブジェクト（構成）を閉じるまで有効（構成に関係しないものは、ずっと有効）。
 * 失敗した関数は負の値（NMM_OCIO_ERROR）か NULL を返し、理由は nmm_ocio_last_error() で取れる（スレッドごと）。
 * 構成と変換は、作ったあとは複数のスレッドから同時に読んでよい（OCIO の Config・CPUProcessor と同じ）。
 */

#define NMM_OCIO_OK 0
#define NMM_OCIO_ERROR (-1)

/* このラッパーの API のバージョン。互換性のない変更をしたら上げる */
NMM_OCIO_API int nmm_ocio_api_version(void);

/* 組み込んでいる OpenColorIO のバージョン（例: "2.5.2"） */
NMM_OCIO_API const char* nmm_ocio_version(void);

/* このスレッドで最後に失敗した理由（なければ空の文字列）。次にこのスレッドで nmm_ocio の関数を呼ぶまで有効 */
NMM_OCIO_API const char* nmm_ocio_last_error(void);

/*
 * OCIO に組み込まれた構成（ACES の cg-config・studio-config）。名前には版が入っている
 * （例: "cg-config-v4.0.0_aces-v2.0_ocio-v2.5"）。index は 0 から count - 1
 */
NMM_OCIO_API int nmm_ocio_builtin_config_count(void);

NMM_OCIO_API const char* nmm_ocio_builtin_config_name(int index);

/* 人が読むための説明 */
NMM_OCIO_API const char* nmm_ocio_builtin_config_ui_name(int index);

/* OCIO が今の版で勧める構成なら 1、古い版のために残しているものなら 0 */
NMM_OCIO_API int nmm_ocio_builtin_config_is_recommended(int index);

/* OCIO の既定の組み込みの構成の名前 */
NMM_OCIO_API const char* nmm_ocio_default_builtin_config_name(void);

/*
 * 構成
 */
typedef struct nmm_ocio_config nmm_ocio_config;

/* 組み込みの構成を、版付きの名前（または "ocio://<名前>"）で読み込む。成功なら 0。
   "default"・"cg-config-latest"・"studio-config-latest" も読めるが、OCIO の版で中身が変わるので、保存するのは版付きの名前にすること */
NMM_OCIO_API int nmm_ocio_config_create_builtin(const char* name, nmm_ocio_config** out);

NMM_OCIO_API void nmm_ocio_config_destroy(nmm_ocio_config* config);

/* 構成の名前（組み込みの構成では、版付きの名前） */
NMM_OCIO_API const char* nmm_ocio_config_name(nmm_ocio_config* config);

/* 色空間（シーンとディスプレイの両方。無効にされたものは除く）。失敗なら負の値・NULL */
NMM_OCIO_API int nmm_ocio_config_color_space_count(nmm_ocio_config* config);

NMM_OCIO_API const char* nmm_ocio_config_color_space_name(nmm_ocio_config* config, int index);

/* 名前・別名・ロールから、色空間の正式な名前を返す（ないなら NULL） */
NMM_OCIO_API const char* nmm_ocio_config_canonical_color_space(nmm_ocio_config* config, const char* name);

/* ディスプレイと、ディスプレイごとのビュー */
NMM_OCIO_API int nmm_ocio_config_display_count(nmm_ocio_config* config);

NMM_OCIO_API const char* nmm_ocio_config_display_name(nmm_ocio_config* config, int index);

NMM_OCIO_API const char* nmm_ocio_config_default_display(nmm_ocio_config* config);

NMM_OCIO_API int nmm_ocio_config_view_count(nmm_ocio_config* config, const char* display);

NMM_OCIO_API const char* nmm_ocio_config_view_name(nmm_ocio_config* config, const char* display, int index);

NMM_OCIO_API const char* nmm_ocio_config_default_view(nmm_ocio_config* config, const char* display);

/*
 * CPU で色を変換する（LUT を焼くためのもの）
 */
typedef struct nmm_ocio_processor nmm_ocio_processor;

/* 色空間 src から dst への変換。成功なら 0 */
NMM_OCIO_API int nmm_ocio_processor_create(nmm_ocio_config* config, const char* src, const char* dst, nmm_ocio_processor** out);

/* 色空間 src から、ディスプレイとビューへの変換。inverse が 0 以外なら、その逆（ディスプレイの値から src へ）。成功なら 0 */
NMM_OCIO_API int nmm_ocio_processor_create_display_view(
    nmm_ocio_config* config, const char* src, const char* display, const char* view, int inverse, nmm_ocio_processor** out);

NMM_OCIO_API void nmm_ocio_processor_destroy(nmm_ocio_processor* processor);

/* 何もしない変換なら 1、そうでなければ 0 */
NMM_OCIO_API int nmm_ocio_processor_is_noop(nmm_ocio_processor* processor);

/* ある色のチャンネルが、ほかのチャンネルの結果に影響する（行列など。3D の LUT が要る）なら 1、そうでなければ 0 */
NMM_OCIO_API int nmm_ocio_processor_has_channel_crosstalk(nmm_ocio_processor* processor);

/* float の RGB（3 つずつ詰めて並べたもの）を pixel_count 画素ぶん、その場で変換する。成功なら 0 */
NMM_OCIO_API int nmm_ocio_processor_apply_rgb(nmm_ocio_processor* processor, float* rgb, int64_t pixel_count);

#ifdef __cplusplus
}
#endif

#endif
