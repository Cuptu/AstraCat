# AstraCat Native Engines

AstraCat uses native C# and sherpa-onnx for in-process speech recognition and subtitle alignment.

Supported native onnx engines:
- `OpenAI Whisper` series (tiny, base, small, medium, large-v3, turbo)
- `Alibaba Qwen3-ASR` series (0.6B, 1.7B)
- `NVIDIA NeMo CTC Parakeet / Canary` series

Models are downloaded to `runtime/models` and executed directly via `SherpaSpeechEngine`.

