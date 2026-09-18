"""
Footage Reviewer transcription sidecar.

Reads a mono 16 kHz WAV and writes JSON:
  { "segments": [ { "start": <s>, "end": <s>, "text": "...",
                    "words": [ { "start": <s>, "end": <s>, "word": "..." } ] } ] }

Times are RELATIVE to the input clip; the app shifts them onto the global timeline.
Engine: faster-whisper (CTranslate2). Runs on the GPU by default.
"""
import argparse
import json
import os
import sys


def _add_cuda_dll_dirs() -> None:
    """ctranslate2 needs cuBLAS/cuDNN. The pip nvidia-*-cu12 packages ship the DLLs but don't put
    them on the search path. ctranslate2 loads them by BARE name (LoadLibrary("cublas64_12.dll")),
    which on Python 3.8+ does NOT consult os.add_dll_directory — so we must also prepend the bin
    dirs to PATH. Do both, before faster_whisper (and thus ctranslate2) is imported."""
    try:
        import importlib.util
        bins = []
        for pkg in ("nvidia.cublas", "nvidia.cudnn"):
            spec = importlib.util.find_spec(pkg)
            if not spec or not spec.submodule_search_locations:
                continue
            for loc in spec.submodule_search_locations:
                binp = os.path.join(loc, "bin")
                if os.path.isdir(binp):
                    bins.append(binp)
        for binp in bins:
            if hasattr(os, "add_dll_directory"):
                os.add_dll_directory(binp)
        if bins:
            os.environ["PATH"] = os.pathsep.join(bins) + os.pathsep + os.environ.get("PATH", "")
    except Exception as exc:  # noqa: BLE001
        sys.stderr.write(f"cuda dll dir setup failed: {exc}\n")


def _run(audio, model_name, device, compute, language):
    from faster_whisper import WhisperModel
    model = WhisperModel(model_name, device=device, compute_type=compute)
    segments, _info = model.transcribe(
        audio,
        language=language,
        word_timestamps=True,
        vad_filter=True,
        vad_parameters=dict(min_silence_duration_ms=500),
    )
    out = {"segments": []}
    for s in segments:  # iterating is what actually runs inference
        words = []
        if s.words:
            for w in s.words:
                words.append({"start": w.start, "end": w.end, "word": w.word})
        out["segments"].append(
            {"start": s.start, "end": s.end, "text": s.text.strip(), "words": words}
        )
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--model", default="medium")
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--compute", default="float16")
    ap.add_argument("--lang", default="auto")
    args = ap.parse_args()

    _add_cuda_dll_dirs()

    try:
        from faster_whisper import WhisperModel  # noqa: F401
    except ImportError:
        sys.stderr.write("faster-whisper is not installed\n")
        return 3

    language = None if args.lang in (None, "", "auto") else args.lang

    # Any GPU failure (missing CUDA lib, OOM, driver) → fall back to CPU int8 rather than crashing.
    try:
        out = _run(args.audio, args.model, args.device, args.compute, language)
    except Exception as exc:  # noqa: BLE001
        sys.stderr.write(f"GPU path failed ({exc}); retrying on CPU int8\n")
        out = _run(args.audio, args.model, "cpu", "int8", language)

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
