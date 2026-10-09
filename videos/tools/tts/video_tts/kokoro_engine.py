from __future__ import annotations

import os
from collections.abc import Callable, Iterable
from typing import Any

import numpy as np

KOKORO_REPOSITORY = "hexgrad/Kokoro-82M"


class KokoroEngine:
    sample_rate = 24000

    def __init__(self, name: str, pipeline: Callable[..., Iterable[Any]] | None = None) -> None:
        self._name = name
        self._pipeline = pipeline if pipeline is not None else _create_pipeline(lang_code=name[0])

    def generate(self, text: str, seed: int) -> np.ndarray:
        # Kokoro is deterministic, so the seed is not needed for reproducible lines.
        chunks = [_to_numpy(result.audio) for result in self._pipeline(text, voice=self._name, speed=1.0) if result.audio is not None]
        if not chunks:
            raise RuntimeError(f"Kokoro produced no audio for {text!r}")
        return np.concatenate(chunks)


def _to_numpy(audio: Any) -> np.ndarray:
    if hasattr(audio, "cpu"):
        audio = audio.cpu().numpy()
    return np.asarray(audio, dtype=np.float32)


def _create_pipeline(lang_code: str) -> Callable[..., Iterable[Any]]:
    _use_bundled_espeak_with_short_data_path()
    import torch
    from kokoro import KPipeline

    return KPipeline(lang_code=lang_code, repo_id=KOKORO_REPOSITORY, device="cuda" if torch.cuda.is_available() else "cpu")


def _use_bundled_espeak_with_short_data_path() -> None:
    import espeakng_loader
    import misaki.espeak  # noqa: F401 (selects the bundled espeak-ng library and its absolute data path on import)
    from phonemizer.backend.espeak.wrapper import EspeakWrapper

    # espeak-ng ignores a data path longer than about 160 characters, which a deep virtual environment exceeds, and
    # then exits the process. It reads ESPEAK_DATA_PATH when no path is passed, and a path relative to the working
    # directory (the TTS project) stays short.
    os.environ["ESPEAK_DATA_PATH"] = os.path.relpath(espeakng_loader.get_data_path())
    EspeakWrapper.set_data_path(None)
