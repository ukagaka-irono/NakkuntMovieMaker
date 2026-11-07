/* nmm_ocio の小さなテスト：組み込みの ACES の構成を読み、色空間とディスプレイの一覧を取り、CPU で変換できるか確かめる */
#include "nmm_ocio.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond)                                                                       \
    do {                                                                                  \
        if (!(cond)) {                                                                    \
            fprintf(stderr, "%s:%d: 失敗: %s（%s）\n", __FILE__, __LINE__, #cond,          \
                    nmm_ocio_last_error());                                               \
            failures++;                                                                   \
        }                                                                                 \
    } while (0)

static void check_srgb_encoding(nmm_ocio_config* config) {
    const char* linear = nmm_ocio_config_canonical_color_space(config, "Linear Rec.709 (sRGB)");
    const char* encoded = nmm_ocio_config_canonical_color_space(config, "sRGB Encoded Rec.709 (sRGB)");
    CHECK(linear && encoded);
    if (!linear || !encoded) return;

    nmm_ocio_processor* processor = NULL;
    CHECK(nmm_ocio_processor_create(config, linear, encoded, &processor) == NMM_OCIO_OK);
    if (!processor) return;
    CHECK(nmm_ocio_processor_is_noop(processor) == 0);
    CHECK(nmm_ocio_processor_has_channel_crosstalk(processor) == 0);

    /* sRGB の符号化: 0.18 → 1.055 * 0.18^(1/2.4) - 0.055 ≒ 0.46135 */
    float rgb[6] = {0.0f, 0.18f, 1.0f, 0.18f, 0.18f, 0.18f};
    CHECK(nmm_ocio_processor_apply_rgb(processor, rgb, 2) == NMM_OCIO_OK);
    CHECK(fabsf(rgb[0]) < 1e-5f);
    CHECK(fabsf(rgb[1] - 0.46135f) < 1e-4f);
    CHECK(fabsf(rgb[2] - 1.0f) < 1e-4f);
    CHECK(fabsf(rgb[4] - rgb[1]) < 1e-6f);
    nmm_ocio_processor_destroy(processor);
}

static void check_display_view(nmm_ocio_config* config) {
    const char* display = nmm_ocio_config_default_display(config);
    CHECK(display != NULL);
    if (!display) return;
    const char* view = nmm_ocio_config_default_view(config, display);
    CHECK(view != NULL);
    CHECK(nmm_ocio_config_view_count(config, display) > 0);
    if (!view) return;
    printf("既定のディスプレイ: %s / ビュー: %s\n", display, view);

    nmm_ocio_processor* processor = NULL;
    CHECK(nmm_ocio_processor_create_display_view(config, "ACEScg", display, view, 0, &processor) == NMM_OCIO_OK);
    if (!processor) return;
    CHECK(nmm_ocio_processor_is_noop(processor) == 0);
    CHECK(nmm_ocio_processor_has_channel_crosstalk(processor) == 1);
    float rgb[3] = {0.18f, 0.18f, 0.18f};
    CHECK(nmm_ocio_processor_apply_rgb(processor, rgb, 1) == NMM_OCIO_OK);
    CHECK(rgb[0] > 0.0f && rgb[0] < 1.0f && isfinite(rgb[0]));
    nmm_ocio_processor_destroy(processor);

    CHECK(nmm_ocio_processor_create_display_view(config, "ACEScg", display, view, 1, &processor) == NMM_OCIO_OK);
    nmm_ocio_processor_destroy(processor);
}

int main(void) {
    printf("nmm_ocio API %d / OpenColorIO %s\n", nmm_ocio_api_version(), nmm_ocio_version());
    CHECK(nmm_ocio_api_version() == 1);
    CHECK(strcmp(nmm_ocio_version(), "2.5.2") == 0);

    /* 組み込みの構成は、どれも読めること */
    int count = nmm_ocio_builtin_config_count();
    CHECK(count > 0);
    for (int i = 0; i < count; i++) {
        nmm_ocio_config* config = NULL;
        const char* name = nmm_ocio_builtin_config_name(i);
        CHECK(name != NULL && strstr(name, "aces") != NULL);
        CHECK(nmm_ocio_config_create_builtin(name, &config) == NMM_OCIO_OK);
        printf("組み込みの構成: %s（推奨 %d）\n", name, nmm_ocio_builtin_config_is_recommended(i));
        nmm_ocio_config_destroy(config);
    }
    CHECK(nmm_ocio_builtin_config_name(count) == NULL);

    /* 既定の構成（"ocio://" 付きの名前でも読める） */
    const char* default_name = nmm_ocio_default_builtin_config_name();
    CHECK(default_name != NULL);
    char uri[256];
    snprintf(uri, sizeof uri, "ocio://%s", default_name);
    nmm_ocio_config* config = NULL;
    CHECK(nmm_ocio_config_create_builtin(uri, &config) == NMM_OCIO_OK);
    if (config) {
        CHECK(strcmp(nmm_ocio_config_name(config), default_name) == 0);
        int spaces = nmm_ocio_config_color_space_count(config);
        CHECK(spaces > 0);
        for (int i = 0; i < spaces; i++) CHECK(nmm_ocio_config_color_space_name(config, i) != NULL);
        CHECK(nmm_ocio_config_color_space_name(config, spaces) == NULL);
        int displays = nmm_ocio_config_display_count(config);
        CHECK(displays > 0);
        for (int i = 0; i < displays; i++) CHECK(nmm_ocio_config_display_name(config, i) != NULL);
        printf("%s: 色空間 %d・ディスプレイ %d\n", default_name, spaces, displays);

        /* 同じ色空間への変換は、何もしない */
        nmm_ocio_processor* identity = NULL;
        CHECK(nmm_ocio_processor_create(config, "ACEScg", "ACEScg", &identity) == NMM_OCIO_OK);
        CHECK(identity && nmm_ocio_processor_is_noop(identity) == 1);
        nmm_ocio_processor_destroy(identity);

        check_srgb_encoding(config);
        check_display_view(config);

        /* ない色空間は失敗して、理由が残る */
        nmm_ocio_processor* missing = NULL;
        CHECK(nmm_ocio_processor_create(config, "ACEScg", "存在しない色空間", &missing) == NMM_OCIO_ERROR);
        CHECK(missing == NULL && nmm_ocio_last_error()[0] != '\0');
        nmm_ocio_config_destroy(config);
    }

    nmm_ocio_config* missing_config = NULL;
    CHECK(nmm_ocio_config_create_builtin("no-such-config", &missing_config) == NMM_OCIO_ERROR);
    CHECK(missing_config == NULL && nmm_ocio_last_error()[0] != '\0');

    if (failures) {
        fprintf(stderr, "%d 件の失敗\n", failures);
        return 1;
    }
    printf("nmm_ocio のテストは成功しました\n");
    return 0;
}
