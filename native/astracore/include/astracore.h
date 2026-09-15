#ifndef ASTRACORE_H
#define ASTRACORE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(ASTRACORE_BUILD)
#    define AC_API __declspec(dllexport)
#  else
#    define AC_API __declspec(dllimport)
#  endif
#else
#  define AC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define AC_ABI_VERSION 4u

/*
 * Cancellation callback: returns nonzero when cancellation is requested.
 */
typedef int (*ac_cancel_callback)(void *opaque);

typedef struct ac_media_info {
    uint32_t struct_size;
    double duration_seconds;
    int64_t bit_rate;
    int32_t width;
    int32_t height;
    double frame_rate;
    int32_t has_video;
    int32_t has_audio;
} ac_media_info;

AC_API uint32_t ac_abi_version(void);

/*
 * Reads container and stream metadata without decoding frames.
 * Returns 0 on success or a negative libav error code. All paths are UTF-8.
 * error_buffer is optional and always NUL-terminated when its size is nonzero.
 */
AC_API int ac_probe_utf8(
    const char *path,
    ac_media_info *result,
    char *error_buffer,
    size_t error_buffer_size);

AC_API int ac_probe_cancel_utf8(
    const char *path,
    ac_media_info *result,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size);

/*
 * Checks whether the specified encoder (e.g. "h264_nvenc", "hevc_nvenc", "av1_nvenc", "libx264")
 * is compiled into libavcodec and can be successfully initialized by the driver/library.
 * Returns 1 if available and operational, 0 if unsupported or failed to initialize.
 */
AC_API int ac_check_encoder(const char *encoder_name);

/*
 * Decodes the first audio stream of the given file, resamples to mono at target_sample_rate (e.g. 4000),
 * computes peak absolute amplitudes in buckets of samples_per_peak,
 * and writes up to max_peaks into out_peaks (values normalized in [0.0f, 1.0f]).
 * out_duration receives the audio stream duration in seconds.
 * Returns the number of peaks written on success, or a negative libav error code on failure.
 * error_buffer is optional and always NUL-terminated when error_buffer_size > 0.
 */
AC_API int ac_extract_waveform_peaks_utf8(
    const char *path,
    int target_sample_rate,
    int samples_per_peak,
    float *out_peaks,
    int max_peaks,
    double *out_duration,
    char *error_buffer,
    size_t error_buffer_size);

AC_API int ac_extract_waveform_peaks_cancel_utf8(
    const char *path,
    int target_sample_rate,
    int samples_per_peak,
    float *out_peaks,
    int max_peaks,
    double *out_duration,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size);

/*
 * Decodes the first audio stream of input_path, resamples to target_sample_rate (e.g. 16000)
 * mono (or channels) 16-bit signed integer PCM, and writes a standard RIFF/WAV file to output_wav_path.
 * Returns 0 on success, or a negative libav error code on failure.
 * error_buffer is optional and always NUL-terminated when error_buffer_size > 0.
 */
AC_API int ac_extract_audio_wav_utf8(
    const char *input_path,
    const char *output_wav_path,
    int target_sample_rate,
    int channels,
    char *error_buffer,
    size_t error_buffer_size);

AC_API int ac_extract_audio_wav_cancel_utf8(
    const char *input_path,
    const char *output_wav_path,
    int target_sample_rate,
    int channels,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size);

/*
 * Adjusts audio tempo (speed) using atempo filter without altering pitch.
 * speed_factor must be in range [0.25, 4.0]. Writes a 16-bit PCM WAV file.
 * Returns 0 on success, or a negative libav error code on failure.
 */
AC_API int ac_change_audio_speed_cancel_utf8(
    const char *input_path,
    const char *output_wav_path,
    double speed_factor,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size);

AC_API int ac_change_audio_speed_utf8(
    const char *input_path,
    const char *output_wav_path,
    double speed_factor,
    char *error_buffer,
    size_t error_buffer_size);

/*
 * Trims a media file from start_seconds with duration_seconds.
 * When stream_copy is 1, performs fast lossless packet copy with timestamp rewriting.
 * When stream_copy is 0, decodes and re-encodes frames.
 * Returns 0 on success, or a negative libav error code on failure.
 */
AC_API int ac_trim_media_cancel_utf8(
    const char *input_path,
    const char *output_path,
    double start_seconds,
    double duration_seconds,
    int stream_copy,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size);

AC_API int ac_trim_media_utf8(
    const char *input_path,
    const char *output_path,
    double start_seconds,
    double duration_seconds,
    int stream_copy,
    char *error_buffer,
    size_t error_buffer_size);

/*
 * Demuxes and copies the specified audio stream index (or first audio stream if < 0)
 * into a standalone audio file without re-encoding.
 * Returns 0 on success, or a negative libav error code on failure.
 */
AC_API int ac_extract_audio_stream_utf8(
    const char *input_media,
    const char *output_audio,
    int stream_index,
    char *error_buffer,
    size_t error_buffer_size);

#ifdef __cplusplus
}
#endif

#endif
