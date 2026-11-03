#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include <stdlib.h>
#include <string.h>

#include "nmm_audio.h"

#define NMM_AUDIO_API_VERSION 3

int nmm_audio_api_version(void)
{
    return NMM_AUDIO_API_VERSION;
}

const char* nmm_audio_miniaudio_version(void)
{
    return ma_version_string();
}

struct nmm_device_list
{
    ma_context context;
    ma_device_info* playback;
    ma_uint32 playback_count;
    ma_device_info* capture;
    ma_uint32 capture_count;
};

static ma_result nmm_context_init(int null_backend, ma_context* context)
{
    ma_backend backends[] = { ma_backend_null };
    return ma_context_init(null_backend ? backends : NULL, null_backend ? 1 : 0, NULL, context);
}

/* 名前が一致するデバイスの ID を探す。name が NULL なら既定のデバイスを使うので、探さずに MA_SUCCESS */
static ma_result nmm_find_device(ma_context* context, ma_device_type type, const char* name, ma_device_id* id, ma_bool32* found)
{
    ma_device_info* playback;
    ma_device_info* capture;
    ma_uint32 playback_count;
    ma_uint32 capture_count;
    ma_device_info* infos;
    ma_uint32 count;
    ma_uint32 i;
    ma_result result;

    *found = MA_FALSE;
    if (name == NULL) {
        return MA_SUCCESS;
    }

    result = ma_context_get_devices(context, &playback, &playback_count, &capture, &capture_count);
    if (result != MA_SUCCESS) {
        return result;
    }

    infos = type == ma_device_type_capture ? capture : playback;
    count = type == ma_device_type_capture ? capture_count : playback_count;
    for (i = 0; i < count; i++) {
        if (strcmp(infos[i].name, name) == 0) {
            *id = infos[i].id;
            *found = MA_TRUE;
            return MA_SUCCESS;
        }
    }

    return MA_NO_DEVICE;
}

int nmm_devices_open(int null_backend, nmm_device_list** out)
{
    nmm_device_list* list;
    ma_result result;

    if (out == NULL) {
        return MA_INVALID_ARGS;
    }

    *out = NULL;
    list = (nmm_device_list*)calloc(1, sizeof(nmm_device_list));
    if (list == NULL) {
        return MA_OUT_OF_MEMORY;
    }

    result = nmm_context_init(null_backend, &list->context);
    if (result == MA_SUCCESS) {
        result = ma_context_get_devices(&list->context, &list->playback, &list->playback_count, &list->capture, &list->capture_count);
        if (result != MA_SUCCESS) {
            ma_context_uninit(&list->context);
        }
    }

    if (result != MA_SUCCESS) {
        free(list);
        return result;
    }

    *out = list;
    return MA_SUCCESS;
}

uint32_t nmm_devices_count(nmm_device_list* list, int capture)
{
    return capture ? list->capture_count : list->playback_count;
}

const char* nmm_devices_name(nmm_device_list* list, int capture, uint32_t index)
{
    return capture ? list->capture[index].name : list->playback[index].name;
}

int nmm_devices_is_default(nmm_device_list* list, int capture, uint32_t index)
{
    return (capture ? list->capture[index].isDefault : list->playback[index].isDefault) ? 1 : 0;
}

void nmm_devices_close(nmm_device_list* list)
{
    if (list != NULL) {
        ma_context_uninit(&list->context);
        free(list);
    }
}

struct nmm_playback
{
    ma_context context;
    ma_device device;
    ma_pcm_rb buffer;
    ma_uint32 channels;
    ma_bool32 started;
    ma_uint64 played;
    ma_uint64 underrun;
    ma_uint32 clear_requested;
};

/* オーディオのスレッドで呼ばれる。リングバッファから読み、足りなければ無音で埋める */
static void nmm_playback_callback(ma_device* device, void* output, const void* input, ma_uint32 frame_count)
{
    nmm_playback* playback = (nmm_playback*)device->pUserData;
    float* destination = (float*)output;
    ma_uint32 remaining = frame_count;
    (void)input;

    if (ma_atomic_load_32(&playback->clear_requested)) {
        /* 書き込み側は、捨て終わるまで書き込まないので、ここで読める分をすべて捨てればよい */
        ma_uint32 available = ma_pcm_rb_available_read(&playback->buffer);
        while (available > 0) {
            ma_uint32 count = available;
            void* data;
            if (ma_pcm_rb_acquire_read(&playback->buffer, &count, &data) != MA_SUCCESS || count == 0) {
                break;
            }
            ma_pcm_rb_commit_read(&playback->buffer, count);
            available -= count;
        }
        ma_atomic_store_32(&playback->clear_requested, 0);
    }

    while (remaining > 0) {
        ma_uint32 count = remaining;
        void* data;
        if (ma_pcm_rb_acquire_read(&playback->buffer, &count, &data) != MA_SUCCESS || count == 0) {
            break;
        }
        memcpy(destination, data, (size_t)count * playback->channels * sizeof(float));
        ma_pcm_rb_commit_read(&playback->buffer, count);
        destination += (size_t)count * playback->channels;
        remaining -= count;
        ma_atomic_fetch_add_64(&playback->played, count);
    }

    if (remaining > 0) {
        memset(destination, 0, (size_t)remaining * playback->channels * sizeof(float));
        ma_atomic_fetch_add_64(&playback->underrun, remaining);
    }
}

int nmm_playback_open(uint32_t sample_rate, uint32_t channels, uint32_t buffer_frames, int null_backend, const char* device_name, nmm_playback** out)
{
    nmm_playback* playback;
    ma_device_config config;
    ma_device_id device_id;
    ma_bool32 found;
    ma_result result;

    if (out == NULL || channels == 0 || sample_rate == 0 || buffer_frames == 0) {
        return MA_INVALID_ARGS;
    }

    *out = NULL;
    playback = (nmm_playback*)calloc(1, sizeof(nmm_playback));
    if (playback == NULL) {
        return MA_OUT_OF_MEMORY;
    }

    playback->channels = channels;
    result = nmm_context_init(null_backend, &playback->context);
    if (result != MA_SUCCESS) {
        free(playback);
        return result;
    }

    result = nmm_find_device(&playback->context, ma_device_type_playback, device_name, &device_id, &found);
    if (result != MA_SUCCESS) {
        ma_context_uninit(&playback->context);
        free(playback);
        return result;
    }

    result = ma_pcm_rb_init(ma_format_f32, channels, buffer_frames, NULL, NULL, &playback->buffer);
    if (result != MA_SUCCESS) {
        ma_context_uninit(&playback->context);
        free(playback);
        return result;
    }

    config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = ma_format_f32;
    config.playback.channels = channels;
    config.playback.pDeviceID = found ? &device_id : NULL;
    config.sampleRate = sample_rate;
    config.dataCallback = nmm_playback_callback;
    config.pUserData = playback;

    result = ma_device_init(&playback->context, &config, &playback->device);
    if (result != MA_SUCCESS) {
        ma_pcm_rb_uninit(&playback->buffer);
        ma_context_uninit(&playback->context);
        free(playback);
        return result;
    }

    *out = playback;
    return MA_SUCCESS;
}

int nmm_playback_start(nmm_playback* playback)
{
    ma_result result = ma_device_start(&playback->device);
    if (result == MA_SUCCESS) {
        playback->started = MA_TRUE;
    }
    return result;
}

int nmm_playback_stop(nmm_playback* playback)
{
    ma_result result = ma_device_stop(&playback->device);
    if (result == MA_SUCCESS) {
        playback->started = MA_FALSE;
    }
    return result;
}

uint32_t nmm_playback_writable(nmm_playback* playback)
{
    return ma_pcm_rb_available_write(&playback->buffer);
}

uint32_t nmm_playback_write(nmm_playback* playback, const float* frames, uint32_t frame_count)
{
    uint32_t written = 0;
    while (written < frame_count) {
        ma_uint32 count = frame_count - written;
        void* data;
        if (ma_pcm_rb_acquire_write(&playback->buffer, &count, &data) != MA_SUCCESS || count == 0) {
            break;
        }
        memcpy(data, frames + (size_t)written * playback->channels, (size_t)count * playback->channels * sizeof(float));
        ma_pcm_rb_commit_write(&playback->buffer, count);
        written += count;
    }
    return written;
}

uint64_t nmm_playback_frames_played(nmm_playback* playback)
{
    return ma_atomic_load_64(&playback->played);
}

uint64_t nmm_playback_underrun_frames(nmm_playback* playback)
{
    return ma_atomic_load_64(&playback->underrun);
}

uint32_t nmm_playback_latency_frames(nmm_playback* playback)
{
    /* 内部の周期の大きさ × 周期の数を、内部のサンプルレートから出力のサンプルレートに直す */
    ma_uint64 frames = (ma_uint64)playback->device.playback.internalPeriodSizeInFrames * playback->device.playback.internalPeriods;
    if (playback->device.playback.internalSampleRate != 0 && playback->device.sampleRate != 0) {
        frames = frames * playback->device.sampleRate / playback->device.playback.internalSampleRate;
    }
    return (uint32_t)frames;
}

int nmm_playback_clear(nmm_playback* playback)
{
    if (!playback->started) {
        /* コールバックが動いていないので、そのまま空にできる */
        ma_pcm_rb_reset(&playback->buffer);
        ma_atomic_store_32(&playback->clear_requested, 0);
        return 1;
    }

    ma_atomic_store_32(&playback->clear_requested, 1);
    return 0;
}

int nmm_playback_clearing(nmm_playback* playback)
{
    return ma_atomic_load_32(&playback->clear_requested) ? 1 : 0;
}

void nmm_playback_close(nmm_playback* playback)
{
    if (playback == NULL) {
        return;
    }

    ma_device_uninit(&playback->device);
    ma_pcm_rb_uninit(&playback->buffer);
    ma_context_uninit(&playback->context);
    free(playback);
}

struct nmm_capture
{
    ma_context context;
    ma_device device;
    ma_pcm_rb buffer;
    ma_uint32 channels;
    ma_uint64 captured;
    ma_uint64 overrun;
};

/* オーディオのスレッドで呼ばれる。入力の音をリングバッファに書き、入りきらない分は捨てて数える */
static void nmm_capture_callback(ma_device* device, void* output, const void* input, ma_uint32 frame_count)
{
    nmm_capture* capture = (nmm_capture*)device->pUserData;
    const float* source = (const float*)input;
    ma_uint32 remaining = frame_count;
    (void)output;

    while (remaining > 0) {
        ma_uint32 count = remaining;
        void* data;
        if (ma_pcm_rb_acquire_write(&capture->buffer, &count, &data) != MA_SUCCESS || count == 0) {
            break;
        }
        memcpy(data, source, (size_t)count * capture->channels * sizeof(float));
        ma_pcm_rb_commit_write(&capture->buffer, count);
        source += (size_t)count * capture->channels;
        remaining -= count;
        ma_atomic_fetch_add_64(&capture->captured, count);
    }

    if (remaining > 0) {
        ma_atomic_fetch_add_64(&capture->overrun, remaining);
    }
}

int nmm_capture_open(uint32_t sample_rate, uint32_t channels, uint32_t buffer_frames, int null_backend, const char* device_name, nmm_capture** out)
{
    nmm_capture* capture;
    ma_device_config config;
    ma_device_id device_id;
    ma_bool32 found;
    ma_result result;

    if (out == NULL || channels == 0 || sample_rate == 0 || buffer_frames == 0) {
        return MA_INVALID_ARGS;
    }

    *out = NULL;
    capture = (nmm_capture*)calloc(1, sizeof(nmm_capture));
    if (capture == NULL) {
        return MA_OUT_OF_MEMORY;
    }

    capture->channels = channels;
    result = nmm_context_init(null_backend, &capture->context);
    if (result != MA_SUCCESS) {
        free(capture);
        return result;
    }

    result = nmm_find_device(&capture->context, ma_device_type_capture, device_name, &device_id, &found);
    if (result == MA_SUCCESS) {
        result = ma_pcm_rb_init(ma_format_f32, channels, buffer_frames, NULL, NULL, &capture->buffer);
    }

    if (result != MA_SUCCESS) {
        ma_context_uninit(&capture->context);
        free(capture);
        return result;
    }

    config = ma_device_config_init(ma_device_type_capture);
    config.capture.format = ma_format_f32;
    config.capture.channels = channels;
    config.capture.pDeviceID = found ? &device_id : NULL;
    config.sampleRate = sample_rate;
    config.dataCallback = nmm_capture_callback;
    config.pUserData = capture;

    result = ma_device_init(&capture->context, &config, &capture->device);
    if (result != MA_SUCCESS) {
        ma_pcm_rb_uninit(&capture->buffer);
        ma_context_uninit(&capture->context);
        free(capture);
        return result;
    }

    *out = capture;
    return MA_SUCCESS;
}

int nmm_capture_start(nmm_capture* capture)
{
    return ma_device_start(&capture->device);
}

int nmm_capture_stop(nmm_capture* capture)
{
    return ma_device_stop(&capture->device);
}

uint32_t nmm_capture_read(nmm_capture* capture, float* frames, uint32_t frame_count)
{
    uint32_t read = 0;
    while (read < frame_count) {
        ma_uint32 count = frame_count - read;
        void* data;
        if (ma_pcm_rb_acquire_read(&capture->buffer, &count, &data) != MA_SUCCESS || count == 0) {
            break;
        }
        memcpy(frames + (size_t)read * capture->channels, data, (size_t)count * capture->channels * sizeof(float));
        ma_pcm_rb_commit_read(&capture->buffer, count);
        read += count;
    }
    return read;
}

uint64_t nmm_capture_frames_captured(nmm_capture* capture)
{
    return ma_atomic_load_64(&capture->captured);
}

uint64_t nmm_capture_overrun_frames(nmm_capture* capture)
{
    return ma_atomic_load_64(&capture->overrun);
}

/* 内部のフレームの数を、出力のサンプルレートに直す */
static ma_uint64 nmm_capture_to_output_rate(nmm_capture* capture, ma_uint64 frames)
{
    if (capture->device.capture.internalSampleRate != 0 && capture->device.sampleRate != 0) {
        frames = frames * capture->device.sampleRate / capture->device.capture.internalSampleRate;
    }
    return frames;
}

uint32_t nmm_capture_latency_frames(nmm_capture* capture)
{
    return (uint32_t)nmm_capture_to_output_rate(capture, (ma_uint64)capture->device.capture.internalPeriodSizeInFrames * capture->device.capture.internalPeriods);
}

uint32_t nmm_capture_period_frames(nmm_capture* capture)
{
    return (uint32_t)nmm_capture_to_output_rate(capture, capture->device.capture.internalPeriodSizeInFrames);
}

void nmm_capture_close(nmm_capture* capture)
{
    if (capture == NULL) {
        return;
    }

    ma_device_uninit(&capture->device);
    ma_pcm_rb_uninit(&capture->buffer);
    ma_context_uninit(&capture->context);
    free(capture);
}
