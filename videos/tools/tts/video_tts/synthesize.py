from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import numpy as np
import soundfile


class Engine(Protocol):
    sample_rate: int

    def generate(self, text: str, seed: int) -> np.ndarray: ...


@dataclass(frozen=True)
class Item:
    key: str
    text: str


def synthesize_all(items: list[Item], output_directory: Path, engine_factory: Callable[[], Engine]) -> dict[str, float]:
    """Synthesizes missing items into <key>.wav and returns the duration in seconds of every item."""
    output_directory.mkdir(parents=True, exist_ok=True)
    engine: Engine | None = None
    durations: dict[str, float] = {}
    for item in items:
        path = output_directory / f"{item.key}.wav"
        if not path.exists():
            # The model is loaded only when something is missing, so fully cached runs stay fast.
            if engine is None:
                engine = engine_factory()
            audio = engine.generate(item.text, seed=int(item.key[:8], 16))
            # Write then rename so an interrupted run never leaves a truncated file in the cache.
            temporary = path.with_suffix(".tmp.wav")
            soundfile.write(temporary, audio, engine.sample_rate)
            temporary.replace(path)
        info = soundfile.info(path)
        durations[item.key] = info.frames / info.samplerate
    return durations
