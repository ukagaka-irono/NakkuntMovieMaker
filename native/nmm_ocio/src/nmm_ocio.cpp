// OpenColorIO（C++）を .NET から使うための薄い C ラッパー（API は include/nmm_ocio.h）
#include "nmm_ocio.h"

#include <OpenColorIO/OpenColorIO.h>

#include <cstring>
#include <exception>
#include <string>

namespace OCIO = OCIO_NAMESPACE;

struct nmm_ocio_config {
    OCIO::ConstConfigRcPtr config;
};

struct nmm_ocio_processor {
    OCIO::ConstCPUProcessorRcPtr cpu;
};

namespace {

thread_local std::string last_error;

void set_error(const char* message) { last_error = message ? message : "不明なエラー"; }

// C++ の例外を C の境界の外に出さないように、本体を包む。失敗したら理由を残して fallback を返す
template <typename T, typename F>
T guarded(T fallback, F&& body) {
    last_error.clear();
    try {
        return body();
    } catch (const std::exception& e) {
        set_error(e.what());
    } catch (...) {
        set_error("不明な例外");
    }
    return fallback;
}

bool check_arguments(bool ok, const char* message) {
    if (!ok) set_error(message);
    return ok;
}

// 空の文字列は「ない」として NULL にする
const char* or_null(const char* text) { return text && *text ? text : nullptr; }

constexpr const char* uri_prefix = "ocio://";

// "ocio://" があってもなくても、組み込みの構成の名前にする。"default" などの特別な名前は、今の版の名前に解決する
std::string resolve_builtin_name(const char* name) {
    std::string uri = std::strncmp(name, uri_prefix, std::strlen(uri_prefix)) == 0 ? name : std::string(uri_prefix) + name;
    std::string resolved = OCIO::ResolveConfigPath(uri.c_str());
    return resolved.substr(std::strlen(uri_prefix));
}

}  // namespace

extern "C" {

NMM_OCIO_API int nmm_ocio_api_version(void) { return 1; }

NMM_OCIO_API const char* nmm_ocio_version(void) { return OCIO::GetVersion(); }

NMM_OCIO_API const char* nmm_ocio_last_error(void) { return last_error.c_str(); }

NMM_OCIO_API int nmm_ocio_builtin_config_count(void) {
    return guarded(NMM_OCIO_ERROR, [] { return static_cast<int>(OCIO::BuiltinConfigRegistry::Get().getNumBuiltinConfigs()); });
}

NMM_OCIO_API const char* nmm_ocio_builtin_config_name(int index) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(index >= 0, "index が負です")) return nullptr;
        return OCIO::BuiltinConfigRegistry::Get().getBuiltinConfigName(static_cast<size_t>(index));
    });
}

NMM_OCIO_API const char* nmm_ocio_builtin_config_ui_name(int index) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(index >= 0, "index が負です")) return nullptr;
        return OCIO::BuiltinConfigRegistry::Get().getBuiltinConfigUIName(static_cast<size_t>(index));
    });
}

NMM_OCIO_API int nmm_ocio_builtin_config_is_recommended(int index) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(index >= 0, "index が負です")) return NMM_OCIO_ERROR;
        return OCIO::BuiltinConfigRegistry::Get().isBuiltinConfigRecommended(static_cast<size_t>(index)) ? 1 : 0;
    });
}

NMM_OCIO_API const char* nmm_ocio_default_builtin_config_name(void) {
    return guarded<const char*>(nullptr, [] {
        static const std::string name = resolve_builtin_name("default");
        return name.c_str();
    });
}

NMM_OCIO_API int nmm_ocio_config_create_builtin(const char* name, nmm_ocio_config** out) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(name && out, "name と out が要ります")) return NMM_OCIO_ERROR;
        *out = nullptr;
        auto config = OCIO::Config::CreateFromBuiltinConfig(resolve_builtin_name(name).c_str());
        *out = new nmm_ocio_config{config};
        return NMM_OCIO_OK;
    });
}

NMM_OCIO_API void nmm_ocio_config_destroy(nmm_ocio_config* config) { delete config; }

NMM_OCIO_API const char* nmm_ocio_config_name(nmm_ocio_config* config) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(config, "config が要ります")) return nullptr;
        return config->config->getName();
    });
}

NMM_OCIO_API int nmm_ocio_config_color_space_count(nmm_ocio_config* config) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(config, "config が要ります")) return NMM_OCIO_ERROR;
        return config->config->getNumColorSpaces();
    });
}

NMM_OCIO_API const char* nmm_ocio_config_color_space_name(nmm_ocio_config* config, int index) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(config, "config が要ります")) return nullptr;
        auto name = or_null(config->config->getColorSpaceNameByIndex(index));
        if (!name) set_error("index が範囲の外です");
        return name;
    });
}

NMM_OCIO_API const char* nmm_ocio_config_canonical_color_space(nmm_ocio_config* config, const char* name) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(config && name, "config と name が要ります")) return nullptr;
        auto canonical = or_null(config->config->getCanonicalName(name));
        if (!canonical) set_error("その名前の色空間はありません");
        return canonical;
    });
}

NMM_OCIO_API int nmm_ocio_config_display_count(nmm_ocio_config* config) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(config, "config が要ります")) return NMM_OCIO_ERROR;
        return config->config->getNumDisplays();
    });
}

NMM_OCIO_API const char* nmm_ocio_config_display_name(nmm_ocio_config* config, int index) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(config, "config が要ります")) return nullptr;
        auto name = or_null(config->config->getDisplay(index));
        if (!name) set_error("index が範囲の外です");
        return name;
    });
}

NMM_OCIO_API const char* nmm_ocio_config_default_display(nmm_ocio_config* config) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(config, "config が要ります")) return nullptr;
        return or_null(config->config->getDefaultDisplay());
    });
}

NMM_OCIO_API int nmm_ocio_config_view_count(nmm_ocio_config* config, const char* display) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(config && display, "config と display が要ります")) return NMM_OCIO_ERROR;
        return config->config->getNumViews(display);
    });
}

NMM_OCIO_API const char* nmm_ocio_config_view_name(nmm_ocio_config* config, const char* display, int index) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(config && display, "config と display が要ります")) return nullptr;
        auto name = or_null(config->config->getView(display, index));
        if (!name) set_error("index が範囲の外です");
        return name;
    });
}

NMM_OCIO_API const char* nmm_ocio_config_default_view(nmm_ocio_config* config, const char* display) {
    return guarded<const char*>(nullptr, [&]() -> const char* {
        if (!check_arguments(config && display, "config と display が要ります")) return nullptr;
        return or_null(config->config->getDefaultView(display));
    });
}

NMM_OCIO_API int nmm_ocio_processor_create(nmm_ocio_config* config, const char* src, const char* dst, nmm_ocio_processor** out) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(config && src && dst && out, "config・src・dst・out が要ります")) return NMM_OCIO_ERROR;
        *out = nullptr;
        auto processor = config->config->getProcessor(src, dst);
        *out = new nmm_ocio_processor{processor->getDefaultCPUProcessor()};
        return NMM_OCIO_OK;
    });
}

NMM_OCIO_API int nmm_ocio_processor_create_display_view(
    nmm_ocio_config* config, const char* src, const char* display, const char* view, int inverse, nmm_ocio_processor** out) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(config && src && display && view && out, "config・src・display・view・out が要ります")) return NMM_OCIO_ERROR;
        *out = nullptr;
        auto transform = OCIO::DisplayViewTransform::Create();
        transform->setSrc(src);
        transform->setDisplay(display);
        transform->setView(view);
        auto processor = config->config->getProcessor(transform, inverse ? OCIO::TRANSFORM_DIR_INVERSE : OCIO::TRANSFORM_DIR_FORWARD);
        *out = new nmm_ocio_processor{processor->getDefaultCPUProcessor()};
        return NMM_OCIO_OK;
    });
}

NMM_OCIO_API void nmm_ocio_processor_destroy(nmm_ocio_processor* processor) { delete processor; }

NMM_OCIO_API int nmm_ocio_processor_is_noop(nmm_ocio_processor* processor) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(processor, "processor が要ります")) return NMM_OCIO_ERROR;
        return processor->cpu->isNoOp() ? 1 : 0;
    });
}

NMM_OCIO_API int nmm_ocio_processor_has_channel_crosstalk(nmm_ocio_processor* processor) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(processor, "processor が要ります")) return NMM_OCIO_ERROR;
        return processor->cpu->hasChannelCrosstalk() ? 1 : 0;
    });
}

NMM_OCIO_API int nmm_ocio_processor_apply_rgb(nmm_ocio_processor* processor, float* rgb, int64_t pixel_count) {
    return guarded(NMM_OCIO_ERROR, [&]() -> int {
        if (!check_arguments(processor && (rgb || pixel_count == 0) && pixel_count >= 0, "processor と rgb が要ります")) return NMM_OCIO_ERROR;
        // PackedImageDesc の幅は long（Windows では 32 ビット）なので、分けて変換する
        constexpr int64_t chunk = int64_t{1} << 24;
        for (int64_t start = 0; start < pixel_count; start += chunk) {
            auto count = static_cast<long>(pixel_count - start < chunk ? pixel_count - start : chunk);
            OCIO::PackedImageDesc image(rgb + start * 3, count, 1, 3);
            processor->cpu->apply(image);
        }
        return NMM_OCIO_OK;
    });
}

}  // extern "C"
