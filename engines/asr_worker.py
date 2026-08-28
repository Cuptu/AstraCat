"""AstraCat local ASR worker.

Protocol: one JSON object per stdin line and one JSON object per stdout line.
Model/framework logs are redirected to stderr so the C# host can parse stdout.
"""

from __future__ import annotations

import gc
import importlib.metadata
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import traceback
import ctypes
from pathlib import Path
from typing import Any


APP_ROOT = Path(__file__).resolve().parents[1]
MODEL_ROOT = Path(os.environ.get("ASTRACAT_MODEL_HOME", APP_ROOT / "runtime" / "models"))
MODEL_ROOT.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("HF_HOME", str(MODEL_ROOT / "huggingface"))
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

_cuda_dll_handle: Any = None
if sys.platform == "win32":
    private_cuda_bin = os.environ.get("ASTRACAT_CUDA_BIN")
    if private_cuda_bin and Path(private_cuda_bin).is_dir():
        os.environ["PATH"] = private_cuda_bin + os.pathsep + os.environ.get("PATH", "")
        try:
            _cuda_dll_handle = os.add_dll_directory(private_cuda_bin)
        except (OSError, AttributeError):
            _cuda_dll_handle = None

_engine_name: str | None = None
_engine: Any = None
_engine_load_signature: str | None = None
_engine_load_warning: str | None = None

# File extensions that soundfile / librosa can open directly.
# Video containers (mp4, mkv, avi, webm, mov, ts, …) need FFmpeg extraction first.
_AUDIO_EXTENSIONS = frozenset({
    ".wav", ".wave", ".flac", ".ogg", ".opus", ".mp3",
    ".m4a", ".aac", ".wma", ".aiff", ".aif", ".au", ".caf",
})

# Keep track of temporary files created by ensure_audio_file() so they can be
# cleaned up when the worker exits.
_temp_audio_files: list[str] = []


def _cleanup_temp_audio() -> None:
    for path in _temp_audio_files:
        try:
            os.unlink(path)
        except OSError:
            pass
    _temp_audio_files.clear()


import atexit
atexit.register(_cleanup_temp_audio)


def _find_ffmpeg() -> str | None:
    """Locate ffmpeg: check runtime/tools/ffmpeg first, then PATH."""
    candidates = [
        APP_ROOT / "runtime" / "tools" / "ffmpeg" / ("ffmpeg.exe" if sys.platform == "win32" else "ffmpeg"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return str(candidate)
    return shutil.which("ffmpeg")


def ensure_audio_file(source: str) -> str:
    """Return a path that soundfile / librosa can read.

    If *source* is already a pure audio file (wav, flac, mp3, …) it is
    returned unchanged.  For video containers (mp4, mkv, avi, …)
    ``ffmpeg`` is used to extract a 16 kHz mono WAV to a temp file.
    """
    ext = Path(source).suffix.lower()
    if ext in _AUDIO_EXTENSIONS:
        return source

    ffmpeg = _find_ffmpeg()
    if ffmpeg is None:
        # No FFmpeg available — return the original path and let the
        # downstream library fail with its own error message.
        print(
            f"[asr_worker] WARNING: 无法找到 FFmpeg，无法从视频文件提取音频。"
            f"请将 ffmpeg 放入 runtime/tools/ffmpeg 或加入 PATH。",
            file=sys.stderr, flush=True,
        )
        return source

    fd, wav_path = tempfile.mkstemp(suffix=".wav", prefix="astracat_asr_")
    os.close(fd)
    _temp_audio_files.append(wav_path)

    cmd = [
        ffmpeg, "-hide_banner", "-loglevel", "error",
        "-i", source,
        "-vn",               # drop video
        "-ac", "1",          # mono
        "-ar", "16000",      # 16 kHz — native for Qwen / Parakeet
        "-sample_fmt", "s16",
        "-f", "wav",
        "-y", wav_path,
    ]
    try:
        subprocess.run(cmd, check=True, capture_output=True, timeout=600)
    except subprocess.CalledProcessError as exc:
        stderr = (exc.stderr or b"").decode(errors="replace").strip()
        raise RuntimeError(
            f"FFmpeg 音频提取失败：{stderr or '未知错误'}"
        ) from exc
    except FileNotFoundError:
        raise RuntimeError("无法启动 FFmpeg，请确认已正确安装") from None

    return wav_path


def emit(message: dict[str, Any]) -> None:
    print(json.dumps(message, ensure_ascii=False), flush=True)


def unload() -> None:
    global _engine_name, _engine, _engine_load_signature, _engine_load_warning
    old_engine = _engine
    _engine = None
    _engine_name = None
    _engine_load_signature = None
    _engine_load_warning = None
    # Drop the last strong reference before collecting.  Some Torch backends
    # release CUDA allocations from their destructors.
    del old_engine
    gc.collect()
    try:
        import torch

        if torch.cuda.is_available():
            try:
                torch.cuda.synchronize()
                torch.cuda.empty_cache()
                torch.cuda.ipc_collect()
            except (RuntimeError, AssertionError):
                pass
    except ImportError:
        pass


def read_model_config(name: str) -> dict[str, Any]:
    config_id = {
        "qwen3-asr-0.6b": "qwen-0.6b",
        "qwen3-asr-1.7b": "qwen-1.7b",
        "nvidia-parakeet-tdt-0.6b-v3": "nvidia-parakeet-v3",
    }.get(name, name)
    path = APP_ROOT / "runtime" / "config" / f"{config_id}.json"
    if not path.is_file():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def advanced(config: dict[str, Any]) -> dict[str, Any]:
    value = config.get("advanced", {})
    return value if isinstance(value, dict) else {}


def torch_cuda_status() -> dict[str, Any]:
    """Return the CUDA state seen by Torch without raising during health checks."""
    status: dict[str, Any] = {"installed": False, "available": False, "deviceCount": 0, "devices": []}
    try:
        import torch
        status["installed"] = True
        status["version"] = torch.__version__
        status["cudaVersion"] = torch.version.cuda
        if not torch.cuda.is_available():
            return status
        count = int(torch.cuda.device_count())
        status.update({
            "available": count > 0,
            "deviceCount": count,
            "devices": [torch.cuda.get_device_name(index) for index in range(count)],
        })
    except (ImportError, RuntimeError, OSError, AssertionError) as exc:
        status["error"] = str(exc)
    return status


def ctranslate2_cuda_status() -> dict[str, Any]:
    """Return the CUDA state required by faster-whisper/CTranslate2."""
    status: dict[str, Any] = {"installed": False, "available": False, "deviceCount": 0}
    try:
        import ctranslate2
        status["installed"] = True
        status["version"] = getattr(ctranslate2, "__version__", None)
        count = int(ctranslate2.get_cuda_device_count())
        status["deviceCount"] = count
        if count <= 0:
            return status
        if sys.platform == "win32":
            # CTranslate2 can see the NVIDIA driver before its CUDA runtime is
            # usable. Loading a model is also lazy, so verify the DLLs needed
            # by faster-whisper before selecting CUDA for actual inference.
            ctypes.WinDLL("cublas64_12.dll")
            ctypes.WinDLL("cudnn64_9.dll")
        status["available"] = True
    except (ImportError, RuntimeError, OSError) as exc:
        status["error"] = str(exc)
    return status


def cuda_available(backend: str | None = None) -> bool:
    """Detect CUDA for a specific inference backend (or either backend)."""
    if backend == "torch":
        return bool(torch_cuda_status()["available"])
    if backend == "ctranslate2":
        return bool(ctranslate2_cuda_status()["available"])
    return bool(torch_cuda_status()["available"] or ctranslate2_cuda_status()["available"])


def selected_device(config: dict[str, Any], backend: str) -> tuple[str, int, str | None]:
    label = str(config.get("device", "自动选择"))
    requested_index = max(0, int(advanced(config).get("deviceIndex", 0)))
    if label == "CPU":
        return "cpu", 0, None

    status = torch_cuda_status() if backend == "torch" else ctranslate2_cuda_status()
    if not status["available"]:
        warning = "CUDA 运行库不完整，已自动切换为 CPU 识别" if label == "CUDA 显卡" else None
        return "cpu", 0, warning

    count = int(status["deviceCount"])
    if requested_index >= count:
        return "cuda", 0, f"GPU 索引 {requested_index} 无效（检测到 {count} 张显卡），已改用 GPU 0"
    return "cuda", requested_index, None


def combine_warnings(*warnings: str | None) -> str | None:
    return "；".join(dict.fromkeys(value for value in warnings if value)) or None


def normalize_language(value: Any, qwen: bool = False) -> str | None:
    if not value or str(value) in {"自动检测", "Auto", "自动"}:
        return None
    language = str(value).strip()
    aliases = {
        "中文": ("zh", "Chinese"), "英语": ("en", "English"), "英文": ("en", "English"),
        "粤语": ("yue", "Cantonese"), "日语": ("ja", "Japanese"), "韩语": ("ko", "Korean"),
        "俄语": ("ru", "Russian"), "法语": ("fr", "French"), "德语": ("de", "German"),
        "西班牙语": ("es", "Spanish"), "葡萄牙语": ("pt", "Portuguese"),
        "意大利语": ("it", "Italian"), "阿拉伯语": ("ar", "Arabic"),
        "印地语": ("hi", "Hindi"), "越南语": ("vi", "Vietnamese"), "泰语": ("th", "Thai"),
        "zh": ("zh", "Chinese"), "en": ("en", "English"), "yue": ("yue", "Cantonese"),
        "ja": ("ja", "Japanese"), "ko": ("ko", "Korean"), "ru": ("ru", "Russian"),
    }
    if language in aliases:
        whisper_code, qwen_name = aliases[language]
        return qwen_name if qwen else whisper_code
    return language


def version_tuple(value: str) -> tuple[int, ...]:
    parts = []
    for part in value.split("."):
        digits = "".join(character for character in part if character.isdigit())
        if not digits:
            break
        parts.append(int(digits))
    return tuple(parts)


def local_model_snapshot(model_root: Path) -> Path | None:
    """Resolve current downloads or assemble a legacy split cache without copying weights."""
    required_processor_files = {
        "preprocessor_config.json", "tokenizer_config.json", "vocab.json", "merges.txt",
    }

    def complete(candidate: Path) -> bool:
        return (
            (candidate / "config.json").is_file()
            and any(candidate.glob("*.safetensors"))
            and all((candidate / filename).is_file() for filename in required_processor_files)
        )

    candidates = [model_root, model_root / ".resolved", *model_root.glob("models--*--*/snapshots/*")]
    for candidate in sorted(candidates, key=lambda path: path.stat().st_mtime if path.exists() else 0, reverse=True):
        if complete(candidate):
            return candidate

    repo_name = "models--Qwen--Qwen3-ASR-0.6B" if model_root.name.endswith("0.6b") else "models--Qwen--Qwen3-ASR-1.7B"
    shared_candidates = list((MODEL_ROOT / "huggingface" / "hub" / repo_name / "snapshots").glob("*"))
    weight_source = next((candidate for candidate in candidates
                          if (candidate / "config.json").is_file() and any(candidate.glob("*.safetensors"))), None)
    processor_source = next((candidate for candidate in [*candidates, *shared_candidates]
                             if all((candidate / filename).is_file() for filename in required_processor_files)), None)
    if weight_source is None or processor_source is None:
        return None

    resolved = model_root / ".resolved"
    resolved.mkdir(parents=True, exist_ok=True)
    for source in (weight_source, processor_source):
        for source_file in source.iterdir():
            if not source_file.is_file():
                continue
            destination = resolved / source_file.name
            if destination.exists() and destination.stat().st_size == source_file.stat().st_size:
                continue
            if destination.exists():
                destination.unlink()
            if source_file.suffix == ".safetensors":
                try:
                    os.link(source_file, destination)
                except OSError as exc:
                    raise RuntimeError("旧版 Qwen 权重无法就地迁移，请在模型管理中重新下载") from exc
            else:
                shutil.copy2(source_file, destination)
    if complete(resolved):
        return resolved
    return None


def load_engine(name: str, config: dict[str, Any] | None = None) -> dict[str, Any]:
    global _engine_name, _engine, _engine_load_signature, _engine_load_warning
    config = config or read_model_config(name)
    extra = advanced(config)
    load_keys = {
        "device": config.get("device"), "precision": config.get("precision"),
        "deviceIndex": extra.get("deviceIndex"), "cpuThreads": extra.get("cpuThreads"),
        "numWorkers": extra.get("numWorkers"), "maxInferenceBatchSize": extra.get("maxInferenceBatchSize"),
        "lowCpuMemoryUsage": extra.get("lowCpuMemoryUsage"),
        "attentionImplementation": extra.get("attentionImplementation"),
        "forcedAligner": extra.get("forcedAligner"), "maxTokens": config.get("maxTokens"),
    }
    signature = json.dumps(load_keys, sort_keys=True, ensure_ascii=False)
    if _engine_name == name and _engine is not None and _engine_load_signature == signature:
        return {"engine": name, "cached": True, "warning": _engine_load_warning}

    unload()
    started = time.perf_counter()

    if name.startswith("whisper-"):
        from faster_whisper import WhisperModel

        device, device_index, device_warning = selected_device(config, "ctranslate2")
        precision = str(config.get("precision", "自动"))
        compute_type = {
            "Float16": "float16", "Int8": "int8", "Int8 Float16": "int8_float16",
            "Float32": "float32",
        }.get(precision, "float16" if device == "cuda" else "int8")
        if device == "cpu" and compute_type in {"float16", "int8_float16"}:
            compute_type = "int8"
            device_warning = combine_warnings(device_warning, "CPU 不支持所选半精度，已改用 Int8")
        local_model = MODEL_ROOT / name
        remote_name = name.removeprefix("whisper-")
        model_source = str(local_model) if (local_model / "model.bin").is_file() else remote_name
        _engine = WhisperModel(
            model_source,
            device=device,
            device_index=device_index,
            compute_type=compute_type,
            cpu_threads=max(0, int(extra.get("cpuThreads", 0))),
            num_workers=max(1, int(extra.get("numWorkers", 1))),
            download_root=str(local_model),
            local_files_only=(model_source == str(local_model)),
        )
    elif name in {"qwen3-asr-0.6b", "qwen3-asr-1.7b"}:
        import torch
        from qwen_asr import Qwen3ASRModel

        device, device_index, device_warning = selected_device(config, "torch")
        cuda = device == "cuda"
        precision = str(config.get("precision", "自动"))
        dtype = {
            "BFloat16": torch.bfloat16, "Float16": torch.float16, "Float32": torch.float32,
        }.get(precision, torch.bfloat16 if cuda else torch.float32)
        if not cuda and dtype != torch.float32:
            dtype = torch.float32
            device_warning = combine_warnings(device_warning, "CPU 推理已改用 Float32 以确保兼容性")
        attention = {
            "SDPA": "sdpa", "Eager": "eager", "Flash Attention 2": "flash_attention_2",
        }.get(str(extra.get("attentionImplementation", "SDPA")), "sdpa")
        forced_aligner = str(extra.get("forcedAligner", "")).strip() or None
        local_model = MODEL_ROOT / name
        snapshot = local_model_snapshot(local_model)
        if snapshot is None:
            raise FileNotFoundError(f"Qwen 模型文件不完整，请在模型管理中重新下载：{local_model}")
        _engine = Qwen3ASRModel.from_pretrained(
            str(snapshot),
            forced_aligner=forced_aligner,
            dtype=dtype,
            device_map=f"cuda:{device_index}" if cuda else "cpu",
            low_cpu_mem_usage=bool(extra.get("lowCpuMemoryUsage", True)),
            attn_implementation=attention,
            cache_dir=str(local_model),
            max_inference_batch_size=int(extra.get("maxInferenceBatchSize", 1)),
            max_new_tokens=max(1, int(config.get("maxTokens", 512))),
        )
    elif name == "nvidia-parakeet-tdt-0.6b-v3":
        transformers_version = importlib.metadata.version("transformers")
        if version_tuple(transformers_version) < (5, 9):
            raise RuntimeError(
                f"NVIDIA Parakeet 需要 Transformers 5.9 或更高版本，当前为 {transformers_version}；"
                "请在模型管理中修复 NVIDIA 推理环境"
            )

        import torch
        from transformers import pipeline

        device, device_index, device_warning = selected_device(config, "torch")
        cuda = device == "cuda"
        precision = str(config.get("precision", "自动"))
        dtype = {
            "BFloat16": torch.bfloat16, "Float16": torch.float16, "Float32": torch.float32,
        }.get(precision, torch.float16 if cuda else torch.float32)
        if not cuda and dtype != torch.float32:
            dtype = torch.float32
            device_warning = combine_warnings(device_warning, "CPU 推理已改用 Float32 以确保兼容性")
        local_model = MODEL_ROOT / name
        model_source = str(local_model) if any(local_model.glob("*.safetensors")) else "nvidia/parakeet-tdt-0.6b-v3"
        _engine = pipeline(
            "automatic-speech-recognition",
            model=model_source,
            device=device_index if cuda else -1,
            dtype=dtype,
        )
    else:
        raise ValueError(f"Unsupported engine: {name}")

    _engine_name = name
    _engine_load_signature = signature
    _engine_load_warning = device_warning
    return {
        "engine": name,
        "cached": False,
        "warning": device_warning,
        "loadSeconds": round(time.perf_counter() - started, 3),
    }


def emit_progress(
    request: dict[str, Any], percent: int, message: str, log_line: str | None = None
) -> None:
    payload = {
        "event": "progress",
        "id": request.get("id"),
        "percent": max(0, min(100, int(percent))),
        "message": message,
    }
    if log_line:
        payload["log"] = log_line
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def format_log_time(seconds: float) -> str:
    milliseconds = max(0, int(round(seconds * 1000)))
    hours, remainder = divmod(milliseconds, 3_600_000)
    minutes, remainder = divmod(remainder, 60_000)
    secs, millis = divmod(remainder, 1000)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}.{millis:03d}"


def join_transcripts(parts: list[str]) -> str:
    output = ""
    for part in parts:
        cleaned = part.strip()
        if not cleaned:
            continue
        if output and output[-1].isascii() and output[-1].isalnum() and cleaned[0].isascii() and cleaned[0].isalnum():
            output += " "
        output += cleaned
    return output


def timestamp_segments(chunks: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Normalize Transformers timestamp chunks and group word chunks into subtitle-sized lines."""
    normalized: list[dict[str, Any]] = []
    for chunk in chunks:
        text = str(chunk.get("text", "")).strip()
        timestamp = chunk.get("timestamp") or chunk.get("timestamps")
        if not text or not isinstance(timestamp, (list, tuple)) or len(timestamp) != 2:
            continue
        start, end = timestamp
        if start is None or end is None:
            continue
        normalized.append({"start": float(start), "end": float(end), "text": text})

    grouped: list[dict[str, Any]] = []
    current: list[dict[str, Any]] = []
    for item in normalized:
        current.append(item)
        combined = join_transcripts([part["text"] for part in current])
        duration = item["end"] - current[0]["start"]
        sentence_end = combined.endswith(("。", "！", "？", ".", "!", "?"))
        if sentence_end or duration >= 6 or len(combined) >= 72:
            grouped.append({
                "start": round(current[0]["start"], 3),
                "end": round(item["end"], 3),
                "text": combined,
            })
            current = []
    if current:
        grouped.append({
            "start": round(current[0]["start"], 3),
            "end": round(current[-1]["end"], 3),
            "text": join_transcripts([part["text"] for part in current]),
        })
    return grouped


def transcribe(request: dict[str, Any]) -> dict[str, Any]:
    name = str(request["engine"])
    audio = str(Path(request["audio"]).resolve())
    # Qwen and Parakeet use librosa / soundfile which cannot read video
    # containers (mp4, mkv, avi, …).  Extract audio via FFmpeg first.
    if not name.startswith("whisper-"):
        audio = ensure_audio_file(audio)
    language = request.get("language")
    config = read_model_config(name)
    if isinstance(request.get("config"), dict):
        config.update(request["config"])
    if request.get("device"):
        config["device"] = request["device"]
    extra = advanced(config)
    language = language or normalize_language(config.get("language"), qwen=name.startswith("qwen"))
    emit_progress(request, 3, "正在准备音频与识别环境…")
    load_info = load_engine(name, config)
    device_warning = load_info.get("warning")
    emit_progress(
        request,
        15,
        f"{device_warning}，模型已加载，正在识别音频…" if device_warning else "模型已加载，正在识别音频…",
        device_warning,
    )
    started = time.perf_counter()

    if name.startswith("whisper-"):
        def setting(key: str, default: Any = None) -> Any:
            return config[key] if key in config else extra.get(key, default)

        def optional_positive(key: str) -> int | None:
            value = int(setting(key, 0))
            return value if value > 0 else None

        def optional_threshold(key: str, default: float = -1) -> float | None:
            value = float(setting(key, default))
            return value if value >= 0 else None

        temperature_source = config.get("temperature")
        if temperature_source is None:
            temperature_source = extra.get("temperatureFallback", "0, 0.2, 0.4, 0.6, 0.8, 1.0")
        temperatures = [float(value.strip()) for value in str(temperature_source).split(",") if value.strip()]
        suppress_tokens = [int(value.strip()) for value in str(extra.get("suppressTokens", "-1")).split(",") if value.strip()]
        vad_parameters = {
            "threshold": float(setting("vadThreshold", 0.3)),
            "neg_threshold": optional_threshold("vadNegThreshold"),
            "min_speech_duration_ms": max(0, int(extra.get("vadMinSpeechMs", 0))),
            "max_speech_duration_s": float(extra.get("vadMaxSpeechSeconds", 0)) or float("inf"),
            "min_silence_duration_ms": max(0, int(config.get(
                "vadMinSilence", extra.get("vadMinSilenceMs", 2000)))),
            "speech_pad_ms": max(0, int(config.get(
                "vadSpeechPad", extra.get("vadSpeechPadMs", 400)))),
        }
        segments, info = _engine.transcribe(
            audio, language=normalize_language(language),
            task="translate" if extra.get("task") == "翻译为英文" else "transcribe",
            beam_size=max(1, int(config.get("beamSize", 5))),
            best_of=max(1, int(extra.get("bestOf", 5))),
            patience=float(extra.get("patience", 1)), length_penalty=float(extra.get("lengthPenalty", 1)),
            repetition_penalty=float(extra.get("repetitionPenalty", 1)),
            no_repeat_ngram_size=max(0, int(extra.get("noRepeatNgramSize", 0))), temperature=temperatures,
            compression_ratio_threshold=optional_threshold("compressionRatioThreshold", 2.4),
            log_prob_threshold=float(extra.get("logProbThreshold", -1)),
            no_speech_threshold=optional_threshold("noSpeechThreshold", 0.6),
            condition_on_previous_text=bool(extra.get("conditionOnPreviousText", True)),
            prompt_reset_on_temperature=float(extra.get("promptResetOnTemperature", 0.5)),
            initial_prompt=str(extra.get("initialPrompt", "")) or None, prefix=str(extra.get("prefix", "")) or None,
            suppress_blank=bool(extra.get("suppressBlank", True)), suppress_tokens=suppress_tokens,
            without_timestamps=bool(extra.get("withoutTimestamps", False)),
            max_initial_timestamp=float(extra.get("maxInitialTimestamp", 1)),
            word_timestamps=bool(config.get("timestamps", True)),
            prepend_punctuations=str(extra.get("prependPunctuations", "\"'“¿([{-")),
            append_punctuations=str(extra.get("appendPunctuations", "\"'.。,，!！?？:：”)]}、")),
            multilingual=bool(extra.get("multilingual", False)), vad_filter=bool(config.get("vad", True)),
            vad_parameters=vad_parameters, max_new_tokens=optional_positive("maxNewTokens"),
            chunk_length=optional_positive("chunkSeconds") or optional_positive("chunkLength"),
            clip_timestamps=str(extra.get("clipTimestamps", "0")),
            hallucination_silence_threshold=optional_threshold("hallucinationSilenceThreshold"),
            hotwords=str(config.get("hotwords", "")) or None,
            language_detection_threshold=optional_threshold("languageDetectionThreshold", 0.5),
            language_detection_segments=max(1, int(extra.get("languageDetectionSegments", 1))),
        )
        output_segments = []
        duration = max(float(getattr(info, "duration", 0) or 0), 0.001)
        last_percent = 15
        for segment in segments:
            segment_text = segment.text.strip()
            output_segments.append({
                "start": round(segment.start, 3),
                "end": round(segment.end, 3),
                "text": segment_text,
            })
            percent = min(95, 15 + int(80 * max(0.0, float(segment.end)) / duration))
            last_percent = max(last_percent, percent)
            time_range = f"{format_log_time(segment.start)} → {format_log_time(segment.end)}"
            emit_progress(
                request,
                last_percent,
                f"正在识别音频… {last_percent} %",
                f"[{time_range}] {segment_text}",
            )
        emit_progress(request, 96, "识别完成，正在整理字幕…")
        result_language = info.language
        text = "".join(segment["text"] for segment in output_segments)
    elif name.startswith("qwen"):
        import torch

        qwen_language = normalize_language(language, qwen=True)
        return_timestamps = bool(config.get("timestamps", False)) and bool(str(extra.get("forcedAligner", "")).strip())
        context_parts = [str(extra.get("context", "")).strip()]
        hotwords = str(config.get("hotwords", "")).strip()
        if hotwords:
            context_parts.append(f"Important terms: {hotwords}")
        qwen_context = "\n".join(part for part in context_parts if part)
        output_segments = []
        detected_languages: list[str] = []
        if return_timestamps:
            with torch.inference_mode():
                result = _engine.transcribe(
                    audio=audio, context=qwen_context, language=qwen_language,
                    return_time_stamps=True)[0]
            detected_languages.append(result.language)
            alignment_items = list(getattr(result.time_stamps, "items", []) or [])
            output_segments = timestamp_segments([{
                "text": item.text,
                "timestamp": (item.start_time, item.end_time),
            } for item in alignment_items])
            if not output_segments and result.text.strip():
                output_segments = [{"start": None, "end": None, "text": result.text.strip()}]
        else:
            # Qwen only exposes timestamps when a forced aligner is installed. Split the
            # source ourselves so every subtitle still receives a truthful time range.
            from qwen_asr.inference.qwen3_asr import normalize_audios

            waveform = normalize_audios(audio)[0]
            sample_rate = 16000
            chunk_seconds = min(120, max(5, int(extra.get(
                "chunkLengthSeconds", extra.get("chunkSeconds", config.get("chunkSeconds", 30))))))
            chunk_samples = chunk_seconds * sample_rate
            total_samples = len(waveform)
            for offset in range(0, total_samples, chunk_samples):
                chunk = waveform[offset:min(total_samples, offset + chunk_samples)]
                if len(chunk) == 0:
                    continue
                with torch.inference_mode():
                    result = _engine.transcribe(
                        audio=(chunk, sample_rate), context=qwen_context,
                        language=qwen_language, return_time_stamps=False)[0]
                detected_languages.append(result.language)
                chunk_text = result.text.strip()
                start_time = offset / sample_rate
                end_time = min(total_samples, offset + len(chunk)) / sample_rate
                if chunk_text:
                    output_segments.append({
                        "start": round(start_time, 3), "end": round(end_time, 3), "text": chunk_text,
                    })
                    percent = min(95, 15 + int(80 * (offset + len(chunk)) / max(1, total_samples)))
                    emit_progress(
                        request, percent, f"正在识别音频… {percent} %",
                        f"[{format_log_time(start_time)} → {format_log_time(end_time)}] {chunk_text}",
                    )
                del result, chunk
            # Long recordings can otherwise remain referenced until the next
            # request in a persistent worker.
            del waveform
        emit_progress(request, 96, "识别完成，正在整理字幕…")
        result_language = ",".join(dict.fromkeys(language for language in detected_languages if language))
        text = join_transcripts([segment["text"] for segment in output_segments])
        if return_timestamps:
            for segment in output_segments:
                if segment["start"] is not None and segment["end"] is not None:
                    emit_progress(
                        request, 96, "识别完成，正在整理字幕…",
                        f"[{format_log_time(segment['start'])} → {format_log_time(segment['end'])}] {segment['text']}",
                    )
    else:
        import torch

        chunk_seconds = min(120, max(5, int(extra.get(
            "chunkLengthSeconds", extra.get("chunkSeconds", config.get("chunkSeconds", 30))))))
        batch_size = min(128, max(1, int(extra.get(
            "batchSize", extra.get("maxInferenceBatchSize", 1)))))
        with torch.inference_mode():
            result = _engine(
                audio,
                return_timestamps="word",
                chunk_length_s=chunk_seconds,
                batch_size=batch_size,
            )
        text = str(result.get("text", "")).strip()
        output_segments = timestamp_segments(list(result.get("chunks", []) or []))
        if not output_segments and text:
            output_segments = [{"start": None, "end": None, "text": text}]
        result_language = normalize_language(language) or ""
        for index, segment in enumerate(output_segments):
            percent = min(95, 15 + int(80 * (index + 1) / max(1, len(output_segments))))
            log_line = segment["text"]
            if segment["start"] is not None and segment["end"] is not None:
                log_line = (
                    f"[{format_log_time(segment['start'])} → {format_log_time(segment['end'])}] "
                    f"{segment['text']}"
                )
            emit_progress(request, percent, f"正在识别音频… {percent} %", log_line)
        emit_progress(request, 96, "识别完成，正在整理字幕…")

    return {
        "engine": name,
        "language": result_language,
        "text": text,
        "segments": output_segments,
        "load": load_info,
        "inferSeconds": round(time.perf_counter() - started, 3),
    }


def handle(request: dict[str, Any]) -> dict[str, Any]:
    command = request.get("command")
    if command == "health":
        torch_status = torch_cuda_status()
        ctranslate2_status = ctranslate2_cuda_status()

        def package_version(name: str) -> str | None:
            try:
                return importlib.metadata.version(name)
            except importlib.metadata.PackageNotFoundError:
                return None

        devices = list(torch_status.get("devices", []))
        torch_ready = bool(torch_status["installed"])
        whisper_package = package_version("faster-whisper")
        transformers_package = package_version("transformers")
        qwen_package = package_version("qwen-asr")
        parakeet_version_ok = bool(
            transformers_package and version_tuple(transformers_package) >= (5, 9))

        return {
            "python": sys.version.split()[0],
            # Preserve the original summary fields for older hosts.
            "torch": torch_status.get("version"),
            "cuda": bool(torch_status["available"] or ctranslate2_status["available"]),
            "gpu": devices[0] if devices else None,
            "loadedEngine": _engine_name,
            "backends": {
                "torch": torch_status,
                "ctranslate2": ctranslate2_status,
                "whisper": {
                    "installed": whisper_package is not None,
                    "version": whisper_package,
                    "cpuReady": whisper_package is not None,
                    "cudaReady": bool(whisper_package and ctranslate2_status["available"]),
                },
                "qwen": {
                    "installed": qwen_package is not None,
                    "version": qwen_package,
                    "cpuReady": bool(qwen_package and torch_ready),
                    "cudaReady": bool(qwen_package and torch_status["available"]),
                },
                "parakeet": {
                    "installed": transformers_package is not None,
                    "transformersVersion": transformers_package,
                    "versionCompatible": parakeet_version_ok,
                    "cpuReady": bool(torch_ready and parakeet_version_ok),
                    "cudaReady": bool(torch_status["available"] and parakeet_version_ok),
                },
            },
        }
    if command == "load":
        return load_engine(str(request["engine"]))
    if command == "transcribe":
        return transcribe(request)
    if command == "unload":
        unload()
        return {"unloaded": True}
    if command == "shutdown":
        unload()
        return {"shutdown": True}
    raise ValueError(f"Unsupported command: {command}")


def main() -> None:
    for raw_line in sys.stdin:
        # PowerShell and a few Windows launchers may prefix the first JSON line
        # with a UTF-8 BOM.  Accept it without weakening the line protocol.
        raw_line = raw_line.lstrip("\ufeff")
        if not raw_line.strip():
            continue
        request: dict[str, Any] = {}
        try:
            request = json.loads(raw_line)
            response = {"ok": True, "id": request.get("id"), "result": handle(request)}
        except Exception as exc:  # Keep worker alive and report structured failures.
            traceback.print_exc(file=sys.stderr)
            response = {
                "ok": False,
                "id": request.get("id"),
                "error": {"type": type(exc).__name__, "message": str(exc)},
            }
        emit(response)
        if request.get("command") == "shutdown":
            return


if __name__ == "__main__":
    main()
