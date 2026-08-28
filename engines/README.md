# AstraCat ASR Worker

The Avalonia host starts `runtime/python/Scripts/python.exe` with
`engines/asr_worker.py` and exchanges one JSON object per line over standard
input/output. Diagnostic logs go to standard error.

Supported proof-of-concept engines:

- `whisper-small` via faster-whisper
- `qwen3-asr-0.6b` via the official qwen-asr Transformers backend

Models are downloaded to `runtime/models` and are intentionally kept outside
the application executable.
