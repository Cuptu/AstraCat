"""AstraCat local download & inspection worker.

Protocol: one JSON object per stdin line and one JSON object per stdout line.
Logs and banners are redirected to stderr so the C# host can reliably parse stdout.
"""

from __future__ import annotations

import atexit
import json
import os
import signal
import sys
import traceback
from pathlib import Path
from typing import Any

# Ensure unbuffered stdout for immediate JSON-RPC delivery
sys.stdout.reconfigure(line_buffering=True)
sys.stderr.reconfigure(line_buffering=True)

try:
    import yt_dlp
except ImportError:
    yt_dlp = None  # type: ignore

_active_download_paths: set[str] = set()


def _cleanup_active_downloads() -> None:
    for path_str in list(_active_download_paths):
        try:
            p = Path(path_str)
            if p.exists():
                p.unlink(missing_ok=True)
            part = Path(str(p) + ".part")
            if part.exists():
                part.unlink(missing_ok=True)
            ytdl = Path(str(p) + ".ytdl")
            if ytdl.exists():
                ytdl.unlink(missing_ok=True)
        except OSError:
            pass
    _active_download_paths.clear()


atexit.register(_cleanup_active_downloads)


def _handle_exit_signal(signum: int, frame: Any) -> None:
    _cleanup_active_downloads()
    sys.exit(0)


try:
    signal.signal(signal.SIGTERM, _handle_exit_signal)
    signal.signal(signal.SIGINT, _handle_exit_signal)
except (ValueError, AttributeError):
    pass


def send_response(data: dict[str, Any]) -> None:
    """Send a single JSON line to stdout."""
    line = json.dumps(data, ensure_ascii=False)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def send_progress(req_id: str, percent: float, downloaded: int, total: int, speed: float | None, eta: float | None, status: str) -> None:
    send_response({
        "event": "progress",
        "id": req_id,
        "percent": round(percent, 2),
        "downloaded_bytes": downloaded,
        "total_bytes": total,
        "speed": round(speed, 2) if speed is not None else None,
        "eta": round(eta, 1) if eta is not None else None,
        "status": status,
    })


class WorkerLogger:
    def debug(self, msg: str) -> None:
        sys.stderr.write(f"[DEBUG] {msg}\n")

    def info(self, msg: str) -> None:
        sys.stderr.write(f"[INFO] {msg}\n")

    def warning(self, msg: str) -> None:
        sys.stderr.write(f"[WARN] {msg}\n")

    def error(self, msg: str) -> None:
        sys.stderr.write(f"[ERROR] {msg}\n")


def handle_ping(req_id: str, _: dict[str, Any]) -> None:
    version = (getattr(yt_dlp, "__version__", None) or getattr(getattr(yt_dlp, "version", None), "__version__", None)) if yt_dlp else None
    send_response({
        "id": req_id,
        "ok": True,
        "result": {
            "ready": yt_dlp is not None,
            "version": version or "not-installed"
        }
    })


def handle_inspect(req_id: str, payload: dict[str, Any]) -> None:
    if yt_dlp is None:
        send_response({"id": req_id, "ok": False, "error": "yt-dlp 模块未安装"})
        return

    url = payload.get("url", "").strip()
    if not url:
        send_response({"id": req_id, "ok": False, "error": "缺少 URL 参数"})
        return

    proxy = payload.get("proxy")
    ydl_opts: dict[str, Any] = {
        "quiet": True,
        "no_warnings": True,
        "logger": WorkerLogger(),
        "skip_download": True,
        "extract_flat": False,
        "windowsfilenames": True,
        "noplaylist": True,
    }
    if proxy:
        ydl_opts["proxy"] = proxy

    cookies_from_browser = payload.get("cookies_from_browser")
    if cookies_from_browser:
        ydl_opts["cookiesfrombrowser"] = (cookies_from_browser,)

    try:
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(url, download=False)
            if not info:
                send_response({"id": req_id, "ok": False, "error": "无法嗅探此链接的媒体信息"})
                return

            subtitles = list(info.get("subtitles", {}).keys())
            auto_subs = list(info.get("automatic_captions", {}).keys())

            send_response({
                "id": req_id,
                "ok": True,
                "result": {
                    "id": info.get("id"),
                    "title": info.get("title") or "未命名媒体",
                    "duration": float(info.get("duration") or 0),
                    "thumbnail": info.get("thumbnail"),
                    "uploader": info.get("uploader"),
                    "webpage_url": info.get("webpage_url") or url,
                    "subtitles": subtitles,
                    "automatic_captions": auto_subs,
                    "has_subtitles": bool(subtitles or auto_subs),
                }
            })
    except Exception as ex:
        sys.stderr.write(traceback.format_exc())
        send_response({"id": req_id, "ok": False, "error": f"解析失败: {str(ex)}"})


def handle_download(req_id: str, payload: dict[str, Any]) -> None:
    if yt_dlp is None:
        send_response({"id": req_id, "ok": False, "error": "yt-dlp 模块未安装"})
        return

    url = payload.get("url", "").strip()
    out_dir = payload.get("output_dir", "").strip()
    if not url or not out_dir:
        send_response({"id": req_id, "ok": False, "error": "缺少 url 或 output_dir 参数"})
        return

    Path(out_dir).mkdir(parents=True, exist_ok=True)

    audio_only = bool(payload.get("audio_only", False))
    write_subs = bool(payload.get("write_subtitles", True))
    sub_langs = payload.get("sub_langs", "zh.*,en.*")
    proxy = payload.get("proxy")
    ffmpeg_loc = payload.get("ffmpeg_location")
    cookies_from_browser = payload.get("cookies_from_browser")

    def progress_hook(d: dict[str, Any]) -> None:
        status = d.get("status", "downloading")
        filename = d.get("filename")
        if filename:
            _active_download_paths.add(filename)

        if status == "downloading":
            total = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
            downloaded = d.get("downloaded_bytes") or 0
            speed = d.get("speed")
            eta = d.get("eta")
            pct = (downloaded / total * 100.0) if total > 0 else 0.0
            send_progress(req_id, pct, downloaded, total, speed, eta, status)
        elif status == "finished":
            send_progress(req_id, 100.0, d.get("total_bytes", 0), d.get("total_bytes", 0), None, 0, status)

    ydl_opts: dict[str, Any] = {
        "quiet": True,
        "no_warnings": True,
        "ignoreerrors": "only_download",
        "logger": WorkerLogger(),
        "windowsfilenames": True,
        "noplaylist": True,
        "progress_hooks": [progress_hook],
        "outtmpl": os.path.join(out_dir, "%(title)s.%(ext)s"),
    }

    if ffmpeg_loc:
        ydl_opts["ffmpeg_location"] = ffmpeg_loc

    if proxy:
        ydl_opts["proxy"] = proxy

    if cookies_from_browser:
        ydl_opts["cookiesfrombrowser"] = (cookies_from_browser,)

    if audio_only:
        ydl_opts["format"] = "bestaudio/best"
        ydl_opts["postprocessors"] = [{
            "key": "FFmpegExtractAudio",
            "preferredcodec": "mp3",
            "preferredquality": "192",
        }]
    else:
        ydl_opts["format"] = "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4] / bv*+ba/b"

    if write_subs:
        ydl_opts["writesubtitles"] = True
        ydl_opts["writeautomaticsub"] = True
        ydl_opts["subtitleslangs"] = [l.strip() for l in sub_langs.split(",") if l.strip()]
        ydl_opts["subtitlesformat"] = "srt/best"

    try:
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(url, download=True)
            if not info:
                send_response({"id": req_id, "ok": False, "error": "下载失败: 未获取到媒体信息"})
                return

            primary_path: str | None = None
            if "requested_downloads" in info:
                for req in info["requested_downloads"]:
                    fp = req.get("filepath")
                    if fp and Path(fp).exists():
                        primary_path = fp
                        break

            if not primary_path:
                fp = ydl.prepare_filename(info)
                if audio_only:
                    mp3_fp = str(Path(fp).with_suffix(".mp3"))
                    if Path(mp3_fp).exists():
                        primary_path = mp3_fp
                if not primary_path and Path(fp).exists():
                    primary_path = fp

            sub_path: str | None = None
            if primary_path:
                base_stem = Path(primary_path).stem
                candidates = list(Path(out_dir).glob(f"{base_stem}*.srt")) + list(Path(out_dir).glob(f"{base_stem}*.vtt"))
                if candidates:
                    sub_path = str(candidates[0])

            _active_download_paths.clear()

            send_response({
                "id": req_id,
                "ok": True,
                "result": {
                    "media_path": primary_path,
                    "subtitle_path": sub_path,
                    "title": info.get("title"),
                    "duration": float(info.get("duration") or 0),
                }
            })
    except Exception as ex:
        _cleanup_active_downloads()
        sys.stderr.write(traceback.format_exc())
        send_response({"id": req_id, "ok": False, "error": f"下载发生异常: {str(ex)}"})


def main() -> None:
    while True:
        try:
            line = sys.stdin.readline()
            if not line:
                break
            line = line.strip().lstrip('\ufeff')
            if not line:
                continue

            request = json.loads(line)
            req_id = request.get("id", "req-0")
            action = request.get("action", "")

            if action == "ping":
                handle_ping(req_id, request)
            elif action == "inspect":
                handle_inspect(req_id, request)
            elif action == "download":
                handle_download(req_id, request)
            elif action == "shutdown":
                _cleanup_active_downloads()
                send_response({"id": req_id, "ok": True, "result": "bye"})
                break
            else:
                send_response({"id": req_id, "ok": False, "error": f"未知 action: {action}"})
        except json.JSONDecodeError as err:
            send_response({"id": "unknown", "ok": False, "error": f"请求 JSON 格式错误: {str(err)}"})
        except Exception as ex:
            sys.stderr.write(traceback.format_exc())
            send_response({"id": "unknown", "ok": False, "error": f"Worker 未捕获异常: {str(ex)}"})

    _cleanup_active_downloads()


if __name__ == "__main__":
    main()


