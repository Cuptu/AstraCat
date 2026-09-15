#include "astracore.h"

#include <stdio.h>

int main(int argc, char **argv)
{
    ac_media_info info = {0};
    char error[256];
    info.struct_size = sizeof(info);
    if (ac_abi_version() != AC_ABI_VERSION) {
        fprintf(stderr, "ABI version mismatch\n");
        return 1;
    }
    if (ac_probe_utf8("__astracore_missing_media__", &info, error, sizeof(error)) >= 0) {
        fprintf(stderr, "missing input unexpectedly succeeded\n");
        return 2;
    }
    if (error[0] == '\0') {
        fprintf(stderr, "probe did not return an error message\n");
        return 3;
    }
    if (ac_check_encoder("__invalid_encoder_xyz__") != 0) {
        fprintf(stderr, "invalid encoder unexpectedly succeeded\n");
        return 6;
    }
    if (ac_check_encoder("pcm_s16le") != 1 && ac_check_encoder("flac") != 1 && ac_check_encoder("libx264") != 1) {
        fprintf(stderr, "standard encoder check failed\n");
        return 7;
    }
    if (argc > 1) {
        info = (ac_media_info) {0};
        info.struct_size = sizeof(info);
        if (ac_probe_utf8(argv[1], &info, error, sizeof(error)) < 0) {
            fprintf(stderr, "media probe failed: %s\n", error);
            return 4;
        }
        if (!info.has_video && !info.has_audio) {
            fprintf(stderr, "media probe returned no streams\n");
            return 5;
        }
        printf("duration=%.6f video=%d audio=%d width=%d height=%d fps=%.6f\n",
               info.duration_seconds, info.has_video, info.has_audio,
               info.width, info.height, info.frame_rate);

        if (info.has_audio) {
            float peaks[512] = {0};
            double duration = 0;
            int peaks_count = ac_extract_waveform_peaks_utf8(argv[1], 4000, 100, peaks, 512, &duration, error, sizeof(error));
            if (peaks_count <= 0) {
                fprintf(stderr, "waveform extraction failed: %s\n", error);
                return 8;
            }
            printf("waveform peaks_count=%d duration=%.4f peak[0]=%.4f\n", peaks_count, duration, peaks[0]);

            const char *temp_wav = "test_smoke_out.wav";
            if (ac_extract_audio_wav_utf8(argv[1], temp_wav, 16000, 1, error, sizeof(error)) != 0) {
                fprintf(stderr, "audio wav extraction failed: %s\n", error);
                return 9;
            }
            FILE *tw = fopen(temp_wav, "rb");
            if (!tw) {
                fprintf(stderr, "failed to open extracted wav\n");
                return 10;
            }
            fseek(tw, 0, SEEK_END);
            long wav_sz = ftell(tw);
            fclose(tw);
            remove(temp_wav);
            if (wav_sz <= 44) {
                fprintf(stderr, "extracted wav file too small: %ld bytes\n", wav_sz);
                return 11;
            }
            printf("audio wav extraction ok: %ld bytes\n", wav_sz);
        }
    }
    return 0;
}
