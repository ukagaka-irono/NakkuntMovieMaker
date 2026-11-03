#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include <stdlib.h>
#include <string.h>

#include "nmm_audio.h"

#define NMM_AUDIO_API_VERSION 2

int nmm_audio_api_version(void)
{
    return NMM_AUDIO_API_VERSION;
}

const char* nmm_audio_miniaudio_version(void)
{
    return ma_version_string();
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

int nmm_playback_open(uint32_t sample_rate, uint32_t channels, uint32_t buffer_frames, int null_backend, nmm_playback** out)
{
    nmm_playback* playback;
    ma_backend backends[] = { ma_backend_null };
    ma_device_config config;
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
    result = ma_context_init(null_backend ? backends : NULL, null_backend ? 1 : 0, NULL, &playback->context);
    if (result != MA_SUCCESS) {
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
