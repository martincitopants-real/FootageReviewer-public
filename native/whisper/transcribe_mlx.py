"""
Footage Reviewer transcription sidecar — Apple Silicon (MLX).

Same CLI and same JSON contract as transcribe.py, so the app needs no changes:
  { "segments": [ { "start": <s>, "end": <s>, "text": "...", "words": [] } ] }

Why a separate sidecar: transcribe.py uses faster-whisper (CTranslate2), which has no Metal backend
and is therefore CPU-only on Apple Silicon — roughly 1x realtime, i.e. unusable across hours of
footage. This one runs on the GPU via MLX.

Engine is chosen by --model:
  * "parakeet" in the name  -> parakeet-mlx   (NVIDIA Parakeet TDT, ~31x realtime on an M3 Pro)
  * anything else           -> mlx-whisper    (Whisper, ~1.3x realtime with the same audio)

The app only reads start/end/text (see EngineSeg), so we don't pay for word-level alignment.
"""
import argparse
import json
import os
import shutil
import sys


def _ensure_ffmpeg_on_path() -> None:
    """parakeet-mlx decodes audio by shelling out to ffmpeg. A GUI app launched from Finder passes a
    minimal PATH to its children (no /opt/homebrew/bin), so ffmpeg goes missing even when it is
    installed. The host app also injects its own PATH; this is the belt to that braces."""
    if shutil.which("ffmpeg"):
        return
    extra = [d for d in ("/opt/homebrew/bin", "/usr/local/bin", "/usr/bin")
             if os.path.exists(os.path.join(d, "ffmpeg"))]
    if extra:
        os.environ["PATH"] = os.pathsep.join(extra + [os.environ.get("PATH", "")])


# Chunked streaming is mandatory for long clips. parakeet-mlx's Python API defaults chunk_duration to
# None (the CLI defaults it to 120), which means the whole file goes through attention in one pass —
# a 27-minute recording asks Metal for ~26 GB and dies. 120 s windows with 15 s overlap match the CLI.
CHUNK_SECONDS = 120.0
OVERLAP_SECONDS = 15.0


def _parakeet(audio, model_name):
    from parakeet_mlx import from_pretrained
    model = from_pretrained(model_name)
    result = model.transcribe(
        audio, chunk_duration=CHUNK_SECONDS, overlap_duration=OVERLAP_SECONDS)
    segments = []
    for s in getattr(result, "sentences", None) or []:
        text = (getattr(s, "text", "") or "").strip()
        if not text:
            continue
        segments.append({
            "start": float(getattr(s, "start", 0.0) or 0.0),
            "end": float(getattr(s, "end", 0.0) or 0.0),
            "text": text,
            "words": [],
        })
    # No sentence segmentation (very short clip, e.g. dictation): fall back to one whole-file segment.
    if not segments and (getattr(result, "text", "") or "").strip():
        segments.append({"start": 0.0, "end": 0.0, "text": result.text.strip(), "words": []})
    return {"segments": segments}


def _whisper(audio, model_name, language):
    import mlx_whisper
    result = mlx_whisper.transcribe(
        audio, path_or_hf_repo=model_name, language=language, verbose=None)
    segments = []
    for s in result.get("segments", []):
        text = (s.get("text") or "").strip()
        if not text:
            continue
        segments.append({
            "start": float(s.get("start", 0.0)),
            "end": float(s.get("end", 0.0)),
            "text": text,
            "words": [],
        })
    return {"segments": segments}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--model", default="mlx-community/parakeet-tdt-0.6b-v3")
    ap.add_argument("--device", default="gpu")    # accepted for CLI parity; MLX always uses the GPU
    ap.add_argument("--compute", default="")      # accepted for CLI parity; unused
    ap.add_argument("--lang", default="auto")
    args = ap.parse_args()

    language = None if args.lang in (None, "", "auto") else args.lang

    _ensure_ffmpeg_on_path()

    try:
        if "parakeet" in args.model.lower():
            out = _parakeet(args.audio, args.model)
        else:
            out = _whisper(args.audio, args.model, language)
    except ImportError as exc:
        sys.stderr.write(f"transcription engine not installed: {exc}\n")
        return 3
    except Exception as exc:  # noqa: BLE001
        sys.stderr.write(f"transcription failed: {exc}\n")
        return 4

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
