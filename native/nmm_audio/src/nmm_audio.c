#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#include "nmm_audio.h"

#define NMM_AUDIO_API_VERSION 1

int nmm_audio_api_version(void)
{
    return NMM_AUDIO_API_VERSION;
}

const char* nmm_audio_miniaudio_version(void)
{
    return ma_version_string();
}
