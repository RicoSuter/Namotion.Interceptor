from __future__ import annotations

import json
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any

import numpy as np
import soundfile

WHISPER_MODEL = "openai/whisper-small.en"
SAMPLE_RATE = 16000


def transcribe_all(items: list[dict[str, str]], recognize: Callable[[np.ndarray], str]) -> list[dict[str, str]]:
    """Transcribes every item's 16 kHz mono WAV file and returns what was heard, by item id."""
    results = []
    for item in items:
        audio, rate = soundfile.read(item["file"], dtype="float32")
        if rate != SAMPLE_RATE:
            raise ValueError(f"{item['file']} has {rate} Hz; the request needs {SAMPLE_RATE} Hz")
        results.append({"id": item["id"], "heard": recognize(audio).strip()})
    return results


def _create_recognizer() -> Callable[[np.ndarray], str]:
    import torch
    from transformers import pipeline

    recognizer: Any = pipeline("automatic-speech-recognition", model=WHISPER_MODEL, device=0 if torch.cuda.is_available() else -1)
    return lambda audio: recognizer({"raw": audio, "sampling_rate": SAMPLE_RATE})["text"]


def main() -> None:
    request = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    results = transcribe_all(request["items"], _create_recognizer())
    Path(request["outputFile"]).write_text(json.dumps(results, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
