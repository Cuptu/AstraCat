#include "astracore.h"

#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>
#include <libavutil/error.h>
#include <libavutil/channel_layout.h>
#include <libswresample/swresample.h>

#include <errno.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#endif

typedef struct AcInterruptContext {
    ac_cancel_callback callback;
    void *opaque;
} AcInterruptContext;

static int ac_check_interrupt(void *opaque)
{
    AcInterruptContext *ctx = (AcInterruptContext *)opaque;
    if (ctx && ctx->callback) {
        return ctx->callback(ctx->opaque) ? 1 : 0;
    }
    return 0;
}

static void ac_write_error(int code, char *buffer, size_t size)
{
    if (!buffer || size == 0)
        return;
    buffer[0] = '\0';
    if (code == AVERROR_EXIT) {
        snprintf(buffer, size, "Operation cancelled");
    } else if (code < 0) {
        av_strerror(code, buffer, size);
    }
    buffer[size - 1] = '\0';
}

#if defined(_WIN32)
static FILE *ac_fopen_utf8(const char *path, const char *mode)
{
    if (!path || !mode) return NULL;
    int path_len = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    int mode_len = MultiByteToWideChar(CP_UTF8, 0, mode, -1, NULL, 0);
    if (path_len <= 0 || mode_len <= 0) return NULL;
    wchar_t *wpath = (wchar_t *)malloc((size_t)path_len * sizeof(wchar_t));
    wchar_t *wmode = (wchar_t *)malloc((size_t)mode_len * sizeof(wchar_t));
    if (!wpath || !wmode) {
        free(wpath);
        free(wmode);
        return NULL;
    }
    MultiByteToWideChar(CP_UTF8, 0, path, -1, wpath, path_len);
    MultiByteToWideChar(CP_UTF8, 0, mode, -1, wmode, mode_len);
    FILE *f = _wfopen(wpath, wmode);
    free(wpath);
    free(wmode);
    return f;
}
#else
static inline FILE *ac_fopen_utf8(const char *path, const char *mode)
{
    return fopen(path, mode);
}
#endif

#pragma pack(push, 1)
typedef struct WavHeader {
    char riff[4];
    uint32_t overall_size;
    char wave[4];
    char fmt_chunk_marker[4];
    uint32_t length_of_fmt;
    uint16_t format_type;
    uint16_t channels;
    uint32_t sample_rate;
    uint32_t byterate;
    uint16_t block_align;
    uint16_t bits_per_sample;
    char data_chunk_header[4];
    uint32_t data_size;
} WavHeader;
#pragma pack(pop)

typedef struct WaveformAccumulator {
    float *peaks;
    int count;
    int max_peaks;
    long samples_in_bucket;
    int samples_per_peak;
    float bucket_max;
} WaveformAccumulator;

static inline void accum_add_sample(WaveformAccumulator *acc, int16_t s)
{
    float abs_val = fabsf((float)s / 32768.0f);
    if (abs_val > acc->bucket_max)
        acc->bucket_max = abs_val;
    acc->samples_in_bucket++;
    if (acc->samples_in_bucket >= acc->samples_per_peak) {
        if (acc->count < acc->max_peaks) {
            acc->peaks[acc->count++] = acc->bucket_max;
        } else {
            int compacted = 0;
            for (int i = 0; i < acc->count; i += 2) {
                float p1 = acc->peaks[i];
                float p2 = (i + 1 < acc->count) ? acc->peaks[i + 1] : p1;
                acc->peaks[compacted++] = (p1 > p2) ? p1 : p2;
            }
            acc->count = compacted;
            acc->samples_per_peak *= 2;
            if (acc->count < acc->max_peaks) {
                acc->peaks[acc->count++] = acc->bucket_max;
            }
        }
        acc->samples_in_bucket = 0;
        acc->bucket_max = 0.0f;
    }
}

uint32_t ac_abi_version(void)
{
    return AC_ABI_VERSION;
}

int ac_probe_cancel_utf8(
    const char *path,
    ac_media_info *result,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size)
{
    AVFormatContext *format = NULL;
    int code;

    if (!path || !result || result->struct_size < sizeof(*result)) {
        code = AVERROR(EINVAL);
        ac_write_error(code, error_buffer, error_buffer_size);
        return code;
    }

    memset((char *) result + sizeof(result->struct_size), 0, sizeof(*result) - sizeof(result->struct_size));

    AcInterruptContext int_ctx = { cancel_cb, cancel_opaque };
    if (cancel_cb) {
        format = avformat_alloc_context();
        if (!format) {
            code = AVERROR(ENOMEM);
            goto done;
        }
        format->interrupt_callback.callback = ac_check_interrupt;
        format->interrupt_callback.opaque = &int_ctx;
    }

    code = avformat_open_input(&format, path, NULL, NULL);
    if (code < 0)
        goto done;
    code = avformat_find_stream_info(format, NULL);
    if (code < 0)
        goto done;

    if (format->duration != AV_NOPTS_VALUE)
        result->duration_seconds = (double) format->duration / AV_TIME_BASE;
    result->bit_rate = format->bit_rate;

    for (unsigned int index = 0; index < format->nb_streams; ++index) {
        AVStream *stream = format->streams[index];
        AVCodecParameters *parameters = stream->codecpar;
        if (parameters->codec_type == AVMEDIA_TYPE_AUDIO)
            result->has_audio = 1;
        if (parameters->codec_type != AVMEDIA_TYPE_VIDEO)
            continue;

        result->has_video = 1;
        if (result->width != 0 || result->height != 0)
            continue;
        result->width = parameters->width;
        result->height = parameters->height;
        AVRational rate = av_guess_frame_rate(format, stream, NULL);
        if (rate.num > 0 && rate.den > 0)
            result->frame_rate = av_q2d(rate);
    }

    code = 0;

done:
    ac_write_error(code, error_buffer, error_buffer_size);
    if (format) avformat_close_input(&format);
    return code;
}

int ac_probe_utf8(const char *path, ac_media_info *result, char *error_buffer, size_t error_buffer_size)
{
    return ac_probe_cancel_utf8(path, result, NULL, NULL, error_buffer, error_buffer_size);
}

int ac_check_encoder(const char *encoder_name)
{
    if (!encoder_name || !*encoder_name)
        return 0;

    const AVCodec *codec = avcodec_find_encoder_by_name(encoder_name);
    if (!codec)
        return 0;

    AVCodecContext *ctx = avcodec_alloc_context3(codec);
    if (!ctx)
        return 0;

    if (codec->type == AVMEDIA_TYPE_VIDEO) {
        ctx->width = 256;
        ctx->height = 256;
        ctx->time_base = (AVRational){1, 30};
        ctx->framerate = (AVRational){30, 1};
        ctx->pix_fmt = AV_PIX_FMT_YUV420P;
#if LIBAVCODEC_VERSION_INT >= AV_VERSION_INT(61, 0, 0)
        const void *out_configs = NULL;
        if (avcodec_get_supported_config(NULL, codec, AV_CODEC_CONFIG_PIX_FORMAT, 0, &out_configs, NULL) >= 0 && out_configs) {
            const enum AVPixelFormat *fmts = (const enum AVPixelFormat *)out_configs;
            if (fmts[0] != AV_PIX_FMT_NONE) {
                ctx->pix_fmt = fmts[0];
            }
        }
#else
        if (codec->pix_fmts && codec->pix_fmts[0] != AV_PIX_FMT_NONE) {
            ctx->pix_fmt = codec->pix_fmts[0];
        }
#endif
    } else if (codec->type == AVMEDIA_TYPE_AUDIO) {
        ctx->sample_rate = 44100;
        AVChannelLayout layout = AV_CHANNEL_LAYOUT_STEREO;
        av_channel_layout_copy(&ctx->ch_layout, &layout);
        ctx->sample_fmt = AV_SAMPLE_FMT_S16;
#if LIBAVCODEC_VERSION_INT >= AV_VERSION_INT(61, 0, 0)
        const void *out_configs = NULL;
        if (avcodec_get_supported_config(NULL, codec, AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, &out_configs, NULL) >= 0 && out_configs) {
            const enum AVSampleFormat *fmts = (const enum AVSampleFormat *)out_configs;
            if (fmts[0] != AV_SAMPLE_FMT_NONE) {
                ctx->sample_fmt = fmts[0];
            }
        }
#else
        if (codec->sample_fmts && codec->sample_fmts[0] != AV_SAMPLE_FMT_NONE) {
            ctx->sample_fmt = codec->sample_fmts[0];
        }
#endif
    }

    int ret = avcodec_open2(ctx, codec, NULL);
    avcodec_free_context(&ctx);
    return (ret >= 0) ? 1 : 0;
}

int ac_extract_waveform_peaks_cancel_utf8(
    const char *path,
    int target_sample_rate,
    int samples_per_peak,
    float *out_peaks,
    int max_peaks,
    double *out_duration,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size)
{
    AVFormatContext *format_ctx = NULL;
    AVCodecContext *codec_ctx = NULL;
    SwrContext *swr_ctx = NULL;
    AVPacket *packet = NULL;
    AVFrame *frame = NULL;
    int16_t *resample_buf = NULL;
    int max_out_samples = 8192;
    int code = 0;
    int peaks_count = 0;

    if (out_duration)
        *out_duration = 0.0;

    if (!path || !out_peaks || max_peaks < 2 || target_sample_rate <= 0) {
        code = AVERROR(EINVAL);
        ac_write_error(code, error_buffer, error_buffer_size);
        return code;
    }
    if (samples_per_peak <= 0)
        samples_per_peak = 1;

    AcInterruptContext int_ctx = { cancel_cb, cancel_opaque };
    if (cancel_cb) {
        format_ctx = avformat_alloc_context();
        if (!format_ctx) {
            code = AVERROR(ENOMEM);
            goto cleanup;
        }
        format_ctx->interrupt_callback.callback = ac_check_interrupt;
        format_ctx->interrupt_callback.opaque = &int_ctx;
    }

    code = avformat_open_input(&format_ctx, path, NULL, NULL);
    if (code < 0)
        goto cleanup;

    code = avformat_find_stream_info(format_ctx, NULL);
    if (code < 0)
        goto cleanup;

    int audio_idx = av_find_best_stream(format_ctx, AVMEDIA_TYPE_AUDIO, -1, -1, NULL, 0);
    if (audio_idx < 0) {
        code = audio_idx;
        goto cleanup;
    }

    AVStream *audio_stream = format_ctx->streams[audio_idx];
    double duration = 0.0;
    if (audio_stream->duration != AV_NOPTS_VALUE && audio_stream->time_base.den > 0)
        duration = (double)audio_stream->duration * av_q2d(audio_stream->time_base);
    else if (format_ctx->duration != AV_NOPTS_VALUE)
        duration = (double)format_ctx->duration / AV_TIME_BASE;

    if (out_duration)
        *out_duration = duration;

    const AVCodec *decoder = avcodec_find_decoder(audio_stream->codecpar->codec_id);
    if (!decoder) {
        code = AVERROR_DECODER_NOT_FOUND;
        goto cleanup;
    }

    codec_ctx = avcodec_alloc_context3(decoder);
    if (!codec_ctx) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    code = avcodec_parameters_to_context(codec_ctx, audio_stream->codecpar);
    if (code < 0)
        goto cleanup;

    code = avcodec_open2(codec_ctx, decoder, NULL);
    if (code < 0)
        goto cleanup;

    AVChannelLayout out_layout = AV_CHANNEL_LAYOUT_MONO;
    code = swr_alloc_set_opts2(
        &swr_ctx,
        &out_layout,
        AV_SAMPLE_FMT_S16,
        target_sample_rate,
        &codec_ctx->ch_layout,
        codec_ctx->sample_fmt,
        codec_ctx->sample_rate,
        0, NULL);
    if (code < 0 || !swr_ctx) {
        if (code >= 0) code = AVERROR(ENOMEM);
        goto cleanup;
    }

    code = swr_init(swr_ctx);
    if (code < 0)
        goto cleanup;

    packet = av_packet_alloc();
    frame = av_frame_alloc();
    resample_buf = (int16_t *)av_malloc((size_t)max_out_samples * sizeof(int16_t));
    if (!packet || !frame || !resample_buf) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    WaveformAccumulator acc = {
        .peaks = out_peaks,
        .count = 0,
        .max_peaks = max_peaks,
        .samples_in_bucket = 0,
        .samples_per_peak = samples_per_peak,
        .bucket_max = 0.0f
    };

    while (av_read_frame(format_ctx, packet) >= 0) {
        if (cancel_cb && cancel_cb(cancel_opaque)) {
            code = AVERROR_EXIT;
            av_packet_unref(packet);
            goto cleanup;
        }
        if (packet->stream_index == audio_idx) {
            int send_ret = avcodec_send_packet(codec_ctx, packet);
            if (send_ret == 0) {
                while (1) {
                    int r = avcodec_receive_frame(codec_ctx, frame);
                    if (r == AVERROR(EAGAIN) || r == AVERROR_EOF)
                        break;
                    if (r < 0) {
                        code = r;
                        av_packet_unref(packet);
                        goto cleanup;
                    }
                    if (cancel_cb && cancel_cb(cancel_opaque)) {
                        code = AVERROR_EXIT;
                        av_packet_unref(packet);
                        goto cleanup;
                    }
                    int delay = (int)swr_get_delay(swr_ctx, codec_ctx->sample_rate);
                    int out_samples = (int)av_rescale_rnd(delay + frame->nb_samples, target_sample_rate, codec_ctx->sample_rate, AV_ROUND_UP);
                    if (out_samples > max_out_samples) {
                        int new_max = out_samples * 2;
                        int16_t *new_buf = (int16_t *)av_realloc(resample_buf, (size_t)new_max * sizeof(int16_t));
                        if (!new_buf) {
                            code = AVERROR(ENOMEM);
                            av_packet_unref(packet);
                            goto cleanup;
                        }
                        resample_buf = new_buf;
                        max_out_samples = new_max;
                    }
                    int converted = swr_convert(swr_ctx, (uint8_t **)&resample_buf, max_out_samples,
                                                (const uint8_t **)frame->data, frame->nb_samples);
                    if (converted < 0) {
                        code = converted;
                        av_packet_unref(packet);
                        goto cleanup;
                    }
                    for (int s = 0; s < converted; s++) {
                        accum_add_sample(&acc, resample_buf[s]);
                    }
                }
            }
        }
        av_packet_unref(packet);
    }

    // Flush decoder
    avcodec_send_packet(codec_ctx, NULL);
    while (1) {
        int r = avcodec_receive_frame(codec_ctx, frame);
        if (r == AVERROR_EOF || r == AVERROR(EAGAIN))
            break;
        if (r < 0) {
            code = r;
            goto cleanup;
        }
        if (cancel_cb && cancel_cb(cancel_opaque)) {
            code = AVERROR_EXIT;
            goto cleanup;
        }
        int delay = (int)swr_get_delay(swr_ctx, codec_ctx->sample_rate);
        int out_samples = (int)av_rescale_rnd(delay + frame->nb_samples, target_sample_rate, codec_ctx->sample_rate, AV_ROUND_UP);
        if (out_samples > max_out_samples) {
            int new_max = out_samples * 2;
            int16_t *new_buf = (int16_t *)av_realloc(resample_buf, (size_t)new_max * sizeof(int16_t));
            if (!new_buf) {
                code = AVERROR(ENOMEM);
                goto cleanup;
            }
            resample_buf = new_buf;
            max_out_samples = new_max;
        }
        int converted = swr_convert(swr_ctx, (uint8_t **)&resample_buf, max_out_samples,
                                    (const uint8_t **)frame->data, frame->nb_samples);
        if (converted < 0) {
            code = converted;
            goto cleanup;
        }
        for (int s = 0; s < converted; s++) {
            accum_add_sample(&acc, resample_buf[s]);
        }
    }

    // Flush swresample
    while (1) {
        int converted = swr_convert(swr_ctx, (uint8_t **)&resample_buf, max_out_samples, NULL, 0);
        if (converted <= 0) {
            if (converted < 0) code = converted;
            break;
        }
        for (int s = 0; s < converted; s++) {
            accum_add_sample(&acc, resample_buf[s]);
        }
    }
    if (code < 0) goto cleanup;

    if (acc.samples_in_bucket > 0 && acc.count < acc.max_peaks) {
        acc.peaks[acc.count++] = acc.bucket_max;
    }
    peaks_count = acc.count;
    code = 0;

cleanup:
    if (packet) av_packet_free(&packet);
    if (frame) av_frame_free(&frame);
    if (resample_buf) av_freep(&resample_buf);
    if (swr_ctx) swr_free(&swr_ctx);
    if (codec_ctx) avcodec_free_context(&codec_ctx);
    if (format_ctx) avformat_close_input(&format_ctx);

    ac_write_error(code, error_buffer, error_buffer_size);
    return (code < 0) ? code : peaks_count;
}

int ac_extract_waveform_peaks_utf8(
    const char *path,
    int target_sample_rate,
    int samples_per_peak,
    float *out_peaks,
    int max_peaks,
    double *out_duration,
    char *error_buffer,
    size_t error_buffer_size)
{
    return ac_extract_waveform_peaks_cancel_utf8(
        path, target_sample_rate, samples_per_peak, out_peaks, max_peaks,
        out_duration, NULL, NULL, error_buffer, error_buffer_size);
}

int ac_extract_audio_wav_cancel_utf8(
    const char *input_path,
    const char *output_wav_path,
    int target_sample_rate,
    int channels,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size)
{
    AVFormatContext *format_ctx = NULL;
    AVCodecContext *codec_ctx = NULL;
    SwrContext *swr_ctx = NULL;
    AVPacket *packet = NULL;
    AVFrame *frame = NULL;
    int16_t *resample_buf = NULL;
    int max_out_samples = 8192;
    FILE *out_file = NULL;
    int code = 0;
    uint32_t total_pcm_bytes = 0;

    if (!input_path || !output_wav_path) {
        code = AVERROR(EINVAL);
        ac_write_error(code, error_buffer, error_buffer_size);
        return code;
    }
    if (target_sample_rate <= 0) target_sample_rate = 16000;
    if (channels <= 0 || channels > 2) channels = 1;

    AcInterruptContext int_ctx = { cancel_cb, cancel_opaque };
    if (cancel_cb) {
        format_ctx = avformat_alloc_context();
        if (!format_ctx) {
            code = AVERROR(ENOMEM);
            goto cleanup;
        }
        format_ctx->interrupt_callback.callback = ac_check_interrupt;
        format_ctx->interrupt_callback.opaque = &int_ctx;
    }

    code = avformat_open_input(&format_ctx, input_path, NULL, NULL);
    if (code < 0) goto cleanup;

    code = avformat_find_stream_info(format_ctx, NULL);
    if (code < 0) goto cleanup;

    int audio_idx = av_find_best_stream(format_ctx, AVMEDIA_TYPE_AUDIO, -1, -1, NULL, 0);
    if (audio_idx < 0) {
        code = audio_idx;
        goto cleanup;
    }

    AVStream *audio_stream = format_ctx->streams[audio_idx];
    const AVCodec *decoder = avcodec_find_decoder(audio_stream->codecpar->codec_id);
    if (!decoder) {
        code = AVERROR_DECODER_NOT_FOUND;
        goto cleanup;
    }

    codec_ctx = avcodec_alloc_context3(decoder);
    if (!codec_ctx) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    code = avcodec_parameters_to_context(codec_ctx, audio_stream->codecpar);
    if (code < 0) goto cleanup;

    code = avcodec_open2(codec_ctx, decoder, NULL);
    if (code < 0) goto cleanup;

    AVChannelLayout out_layout;
    if (channels == 1) {
        out_layout = (AVChannelLayout)AV_CHANNEL_LAYOUT_MONO;
    } else {
        out_layout = (AVChannelLayout)AV_CHANNEL_LAYOUT_STEREO;
    }
    code = swr_alloc_set_opts2(
        &swr_ctx,
        &out_layout,
        AV_SAMPLE_FMT_S16,
        target_sample_rate,
        &codec_ctx->ch_layout,
        codec_ctx->sample_fmt,
        codec_ctx->sample_rate,
        0, NULL);
    if (code < 0 || !swr_ctx) {
        if (code >= 0) code = AVERROR(ENOMEM);
        goto cleanup;
    }

    code = swr_init(swr_ctx);
    if (code < 0) goto cleanup;

    out_file = ac_fopen_utf8(output_wav_path, "wb");
    if (!out_file) {
        code = AVERROR(errno ? errno : EIO);
        goto cleanup;
    }

    WavHeader header;
    memcpy(header.riff, "RIFF", 4);
    header.overall_size = 0;
    memcpy(header.wave, "WAVE", 4);
    memcpy(header.fmt_chunk_marker, "fmt ", 4);
    header.length_of_fmt = 16;
    header.format_type = 1; // PCM
    header.channels = (uint16_t)channels;
    header.sample_rate = (uint32_t)target_sample_rate;
    header.byterate = (uint32_t)(target_sample_rate * channels * sizeof(int16_t));
    header.block_align = (uint16_t)(channels * sizeof(int16_t));
    header.bits_per_sample = 16;
    memcpy(header.data_chunk_header, "data", 4);
    header.data_size = 0;

    if (fwrite(&header, sizeof(header), 1, out_file) != 1) {
        code = AVERROR(EIO);
        goto cleanup;
    }

    packet = av_packet_alloc();
    frame = av_frame_alloc();
    resample_buf = (int16_t *)av_malloc((size_t)(max_out_samples * channels) * sizeof(int16_t));
    if (!packet || !frame || !resample_buf) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    while (av_read_frame(format_ctx, packet) >= 0) {
        if (cancel_cb && cancel_cb(cancel_opaque)) {
            code = AVERROR_EXIT;
            av_packet_unref(packet);
            goto cleanup;
        }
        if (packet->stream_index == audio_idx) {
            int send_ret = avcodec_send_packet(codec_ctx, packet);
            if (send_ret == 0) {
                while (1) {
                    int r = avcodec_receive_frame(codec_ctx, frame);
                    if (r == AVERROR(EAGAIN) || r == AVERROR_EOF)
                        break;
                    if (r < 0) {
                        code = r;
                        av_packet_unref(packet);
                        goto cleanup;
                    }
                    if (cancel_cb && cancel_cb(cancel_opaque)) {
                        code = AVERROR_EXIT;
                        av_packet_unref(packet);
                        goto cleanup;
                    }
                    int delay = (int)swr_get_delay(swr_ctx, codec_ctx->sample_rate);
                    int out_samples = (int)av_rescale_rnd(delay + frame->nb_samples, target_sample_rate, codec_ctx->sample_rate, AV_ROUND_UP);
                    if (out_samples > max_out_samples) {
                        int new_max = out_samples * 2;
                        int16_t *new_buf = (int16_t *)av_realloc(resample_buf, (size_t)(new_max * channels) * sizeof(int16_t));
                        if (!new_buf) {
                            code = AVERROR(ENOMEM);
                            av_packet_unref(packet);
                            goto cleanup;
                        }
                        resample_buf = new_buf;
                        max_out_samples = new_max;
                    }
                    int converted = swr_convert(swr_ctx, (uint8_t **)&resample_buf, max_out_samples,
                                                (const uint8_t **)frame->data, frame->nb_samples);
                    if (converted < 0) {
                        code = converted;
                        av_packet_unref(packet);
                        goto cleanup;
                    }
                    if (converted > 0) {
                        size_t bytes = (size_t)converted * (size_t)channels * sizeof(int16_t);
                        if (fwrite(resample_buf, 1, bytes, out_file) != bytes) {
                            code = AVERROR(EIO);
                            av_packet_unref(packet);
                            goto cleanup;
                        }
                        total_pcm_bytes += (uint32_t)bytes;
                    }
                }
            }
        }
        av_packet_unref(packet);
    }

    // Flush decoder
    avcodec_send_packet(codec_ctx, NULL);
    while (1) {
        int r = avcodec_receive_frame(codec_ctx, frame);
        if (r == AVERROR_EOF || r == AVERROR(EAGAIN))
            break;
        if (r < 0) {
            code = r;
            goto cleanup;
        }
        if (cancel_cb && cancel_cb(cancel_opaque)) {
            code = AVERROR_EXIT;
            goto cleanup;
        }
        int delay = (int)swr_get_delay(swr_ctx, codec_ctx->sample_rate);
        int out_samples = (int)av_rescale_rnd(delay + frame->nb_samples, target_sample_rate, codec_ctx->sample_rate, AV_ROUND_UP);
        if (out_samples > max_out_samples) {
            int new_max = out_samples * 2;
            int16_t *new_buf = (int16_t *)av_realloc(resample_buf, (size_t)(new_max * channels) * sizeof(int16_t));
            if (!new_buf) {
                code = AVERROR(ENOMEM);
                goto cleanup;
            }
            resample_buf = new_buf;
            max_out_samples = new_max;
        }
        int converted = swr_convert(swr_ctx, (uint8_t **)&resample_buf, max_out_samples,
                                    (const uint8_t **)frame->data, frame->nb_samples);
        if (converted < 0) {
            code = converted;
            goto cleanup;
        }
        if (converted > 0) {
            size_t bytes = (size_t)converted * (size_t)channels * sizeof(int16_t);
            if (fwrite(resample_buf, 1, bytes, out_file) != bytes) {
                code = AVERROR(EIO);
                goto cleanup;
            }
            total_pcm_bytes += (uint32_t)bytes;
        }
    }

    // Flush swr
    while (1) {
        int converted = swr_convert(swr_ctx, (uint8_t **)&resample_buf, max_out_samples, NULL, 0);
        if (converted <= 0) {
            if (converted < 0) code = converted;
            break;
        }
        size_t bytes = (size_t)converted * (size_t)channels * sizeof(int16_t);
        if (fwrite(resample_buf, 1, bytes, out_file) != bytes) {
            code = AVERROR(EIO);
            goto cleanup;
        }
        total_pcm_bytes += (uint32_t)bytes;
    }
    if (code < 0) goto cleanup;

    // Update header sizes
    header.overall_size = total_pcm_bytes + 36;
    header.data_size = total_pcm_bytes;
    if (fseek(out_file, 0, SEEK_SET) == 0) {
        fwrite(&header, sizeof(header), 1, out_file);
    }
    fflush(out_file);
    code = 0;

cleanup:
    if (out_file) fclose(out_file);
    if (packet) av_packet_free(&packet);
    if (frame) av_frame_free(&frame);
    if (resample_buf) av_freep(&resample_buf);
    if (swr_ctx) swr_free(&swr_ctx);
    if (codec_ctx) avcodec_free_context(&codec_ctx);
    if (format_ctx) avformat_close_input(&format_ctx);

    ac_write_error(code, error_buffer, error_buffer_size);
    return code;
}

int ac_extract_audio_wav_utf8(
    const char *input_path,
    const char *output_wav_path,
    int target_sample_rate,
    int channels,
    char *error_buffer,
    size_t error_buffer_size)
{
    return ac_extract_audio_wav_cancel_utf8(
        input_path, output_wav_path, target_sample_rate, channels,
        NULL, NULL, error_buffer, error_buffer_size);
}

int ac_trim_media_cancel_utf8(
    const char *input_path,
    const char *output_path,
    double start_seconds,
    double duration_seconds,
    int stream_copy,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size)
{
    if (!input_path || !*input_path || !output_path || !*output_path) {
        ac_write_error(AVERROR(EINVAL), error_buffer, error_buffer_size);
        return AVERROR(EINVAL);
    }
    if (start_seconds < 0.0) start_seconds = 0.0;

    int code = 0;
    AVFormatContext *in_fmt = NULL;
    AVFormatContext *out_fmt = NULL;
    AVPacket *pkt = NULL;
    int64_t *stream_start_pts = NULL;
    int *stream_mapping = NULL;
    int stream_mapping_size = 0;
    int *seen_first_pts = NULL;

    AcInterruptContext ic = { cancel_cb, cancel_opaque };
    AVIOInterruptCB interrupt_cb = { ac_check_interrupt, &ic };

    in_fmt = avformat_alloc_context();
    if (!in_fmt) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }
    in_fmt->interrupt_callback = interrupt_cb;

    code = avformat_open_input(&in_fmt, input_path, NULL, NULL);
    if (code < 0) goto cleanup;

    code = avformat_find_stream_info(in_fmt, NULL);
    if (code < 0) goto cleanup;

    code = avformat_alloc_output_context2(&out_fmt, NULL, NULL, output_path);
    if (code < 0 || !out_fmt) {
        code = (code < 0) ? code : AVERROR(ENOMEM);
        goto cleanup;
    }
    out_fmt->interrupt_callback = interrupt_cb;

    stream_mapping_size = (int)in_fmt->nb_streams;
    stream_mapping = (int *)av_calloc(stream_mapping_size, sizeof(int));
    stream_start_pts = (int64_t *)av_calloc(stream_mapping_size, sizeof(int64_t));
    seen_first_pts = (int *)av_calloc(stream_mapping_size, sizeof(int));
    if (!stream_mapping || !stream_start_pts || !seen_first_pts) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    int stream_idx = 0;
    for (int i = 0; i < stream_mapping_size; i++) {
        AVStream *in_stream = in_fmt->streams[i];
        AVCodecParameters *codecpar = in_stream->codecpar;
        if (codecpar->codec_type != AVMEDIA_TYPE_AUDIO &&
            codecpar->codec_type != AVMEDIA_TYPE_VIDEO &&
            codecpar->codec_type != AVMEDIA_TYPE_SUBTITLE) {
            stream_mapping[i] = -1;
            continue;
        }

        stream_mapping[i] = stream_idx++;
        AVStream *out_stream = avformat_new_stream(out_fmt, NULL);
        if (!out_stream) {
            code = AVERROR(ENOMEM);
            goto cleanup;
        }

        code = avcodec_parameters_copy(out_stream->codecpar, in_stream->codecpar);
        if (code < 0) goto cleanup;
        out_stream->codecpar->codec_tag = 0;
    }

    if (!(out_fmt->oformat->flags & AVFMT_NOFILE)) {
        code = avio_open2(&out_fmt->pb, output_path, AVIO_FLAG_WRITE, &interrupt_cb, NULL);
        if (code < 0) goto cleanup;
    }

    code = avformat_write_header(out_fmt, NULL);
    if (code < 0) goto cleanup;

    int64_t seek_target = (int64_t)(start_seconds * AV_TIME_BASE);
    if (start_seconds > 0.0) {
        avformat_seek_file(in_fmt, -1, INT64_MIN, seek_target, seek_target, 0);
    }

    pkt = av_packet_alloc();
    if (!pkt) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    int64_t end_time_us = (duration_seconds > 0.0) ? (int64_t)((start_seconds + duration_seconds) * AV_TIME_BASE) : -1;

    while (1) {
        if (cancel_cb && cancel_cb(cancel_opaque)) {
            code = AVERROR_EXIT;
            break;
        }

        code = av_read_frame(in_fmt, pkt);
        if (code < 0) {
            if (code == AVERROR_EOF) code = 0;
            break;
        }

        if (pkt->stream_index >= stream_mapping_size || stream_mapping[pkt->stream_index] < 0) {
            av_packet_unref(pkt);
            continue;
        }

        AVStream *in_stream = in_fmt->streams[pkt->stream_index];
        AVStream *out_stream = out_fmt->streams[stream_mapping[pkt->stream_index]];

        int64_t pkt_time_us = av_rescale_q(pkt->pts != AV_NOPTS_VALUE ? pkt->pts : pkt->dts,
                                           in_stream->time_base, (AVRational){1, AV_TIME_BASE});

        if (pkt_time_us < (int64_t)(start_seconds * AV_TIME_BASE)) {
            av_packet_unref(pkt);
            continue;
        }

        if (end_time_us > 0 && pkt_time_us > end_time_us) {
            av_packet_unref(pkt);
            break;
        }

        if (!seen_first_pts[pkt->stream_index]) {
            seen_first_pts[pkt->stream_index] = 1;
            stream_start_pts[pkt->stream_index] = pkt->pts != AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
        }

        int64_t base_pts = stream_start_pts[pkt->stream_index];
        if (pkt->pts != AV_NOPTS_VALUE && pkt->pts >= base_pts) {
            pkt->pts = av_rescale_q_rnd(pkt->pts - base_pts, in_stream->time_base, out_stream->time_base, (enum AVRounding)(AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX));
        } else {
            pkt->pts = AV_NOPTS_VALUE;
        }

        if (pkt->dts != AV_NOPTS_VALUE && pkt->dts >= base_pts) {
            pkt->dts = av_rescale_q_rnd(pkt->dts - base_pts, in_stream->time_base, out_stream->time_base, (enum AVRounding)(AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX));
        } else {
            pkt->dts = pkt->pts;
        }

        if (pkt->pts != AV_NOPTS_VALUE && pkt->dts != AV_NOPTS_VALUE && pkt->dts > pkt->pts) {
            pkt->dts = pkt->pts;
        }

        pkt->duration = av_rescale_q(pkt->duration, in_stream->time_base, out_stream->time_base);
        pkt->pos = -1;
        pkt->stream_index = stream_mapping[pkt->stream_index];

        code = av_interleaved_write_frame(out_fmt, pkt);
        av_packet_unref(pkt);
        if (code < 0) break;
    }

    if (code >= 0) {
        av_write_trailer(out_fmt);
    }

cleanup:
    if (pkt) av_packet_free(&pkt);
    if (stream_mapping) av_free(stream_mapping);
    if (stream_start_pts) av_free(stream_start_pts);
    if (seen_first_pts) av_free(seen_first_pts);
    if (out_fmt) {
        if (!(out_fmt->oformat->flags & AVFMT_NOFILE) && out_fmt->pb) {
            avio_closep(&out_fmt->pb);
        }
        avformat_free_context(out_fmt);
    }
    if (in_fmt) avformat_close_input(&in_fmt);

    ac_write_error(code, error_buffer, error_buffer_size);
    return code;
}

int ac_trim_media_utf8(
    const char *input_path,
    const char *output_path,
    double start_seconds,
    double duration_seconds,
    int stream_copy,
    char *error_buffer,
    size_t error_buffer_size)
{
    return ac_trim_media_cancel_utf8(
        input_path, output_path, start_seconds, duration_seconds, stream_copy,
        NULL, NULL, error_buffer, error_buffer_size);
}

int ac_extract_audio_stream_utf8(
    const char *input_media,
    const char *output_audio,
    int stream_index,
    char *error_buffer,
    size_t error_buffer_size)
{
    if (!input_media || !*input_media || !output_audio || !*output_audio) {
        ac_write_error(AVERROR(EINVAL), error_buffer, error_buffer_size);
        return AVERROR(EINVAL);
    }

    int code = 0;
    AVFormatContext *in_fmt = NULL;
    AVFormatContext *out_fmt = NULL;
    AVPacket *pkt = NULL;
    int target_stream_idx = -1;

    code = avformat_open_input(&in_fmt, input_media, NULL, NULL);
    if (code < 0) goto cleanup;

    code = avformat_find_stream_info(in_fmt, NULL);
    if (code < 0) goto cleanup;

    if (stream_index >= 0 && stream_index < (int)in_fmt->nb_streams &&
        in_fmt->streams[stream_index]->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) {
        target_stream_idx = stream_index;
    } else {
        target_stream_idx = av_find_best_stream(in_fmt, AVMEDIA_TYPE_AUDIO, -1, -1, NULL, 0);
    }

    if (target_stream_idx < 0) {
        code = AVERROR_STREAM_NOT_FOUND;
        goto cleanup;
    }

    AVStream *in_stream = in_fmt->streams[target_stream_idx];

    code = avformat_alloc_output_context2(&out_fmt, NULL, NULL, output_audio);
    if (code < 0 || !out_fmt) {
        code = (code < 0) ? code : AVERROR(ENOMEM);
        goto cleanup;
    }

    AVStream *out_stream = avformat_new_stream(out_fmt, NULL);
    if (!out_stream) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    code = avcodec_parameters_copy(out_stream->codecpar, in_stream->codecpar);
    if (code < 0) goto cleanup;
    out_stream->codecpar->codec_tag = 0;

    if (!(out_fmt->oformat->flags & AVFMT_NOFILE)) {
        code = avio_open2(&out_fmt->pb, output_audio, AVIO_FLAG_WRITE, NULL, NULL);
        if (code < 0) goto cleanup;
    }

    code = avformat_write_header(out_fmt, NULL);
    if (code < 0) goto cleanup;

    pkt = av_packet_alloc();
    if (!pkt) {
        code = AVERROR(ENOMEM);
        goto cleanup;
    }

    int64_t first_pts = AV_NOPTS_VALUE;

    while (av_read_frame(in_fmt, pkt) >= 0) {
        if (pkt->stream_index != target_stream_idx) {
            av_packet_unref(pkt);
            continue;
        }

        if (first_pts == AV_NOPTS_VALUE) {
            first_pts = pkt->pts != AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
        }

        if (pkt->pts != AV_NOPTS_VALUE && pkt->pts >= first_pts) {
            pkt->pts = av_rescale_q_rnd(pkt->pts - first_pts, in_stream->time_base, out_stream->time_base, (enum AVRounding)(AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX));
        }
        if (pkt->dts != AV_NOPTS_VALUE && pkt->dts >= first_pts) {
            pkt->dts = av_rescale_q_rnd(pkt->dts - first_pts, in_stream->time_base, out_stream->time_base, (enum AVRounding)(AV_ROUND_NEAR_INF | AV_ROUND_PASS_MINMAX));
        } else {
            pkt->dts = pkt->pts;
        }

        pkt->duration = av_rescale_q(pkt->duration, in_stream->time_base, out_stream->time_base);
        pkt->pos = -1;
        pkt->stream_index = 0;

        code = av_interleaved_write_frame(out_fmt, pkt);
        av_packet_unref(pkt);
        if (code < 0) break;
    }

    if (code >= 0) {
        av_write_trailer(out_fmt);
    }

cleanup:
    if (pkt) av_packet_free(&pkt);
    if (out_fmt) {
        if (!(out_fmt->oformat->flags & AVFMT_NOFILE) && out_fmt->pb) {
            avio_closep(&out_fmt->pb);
        }
        avformat_free_context(out_fmt);
    }
    if (in_fmt) avformat_close_input(&in_fmt);

    ac_write_error(code, error_buffer, error_buffer_size);
    return code;
}

int ac_change_audio_speed_cancel_utf8(
    const char *input_path,
    const char *output_wav_path,
    double speed_factor,
    ac_cancel_callback cancel_cb,
    void *cancel_opaque,
    char *error_buffer,
    size_t error_buffer_size)
{
    if (speed_factor < 0.25 || speed_factor > 4.0) {
        ac_write_error(AVERROR(EINVAL), error_buffer, error_buffer_size);
        return AVERROR(EINVAL);
    }

    // Extract decoded 16kHz mono audio as base
    int code = ac_extract_audio_wav_cancel_utf8(
        input_path, output_wav_path, 16000, 1,
        cancel_cb, cancel_opaque, error_buffer, error_buffer_size);
    return code;
}

int ac_change_audio_speed_utf8(
    const char *input_path,
    const char *output_wav_path,
    double speed_factor,
    char *error_buffer,
    size_t error_buffer_size)
{
    return ac_change_audio_speed_cancel_utf8(
        input_path, output_wav_path, speed_factor,
        NULL, NULL, error_buffer, error_buffer_size);
}
