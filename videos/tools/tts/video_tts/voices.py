from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from video_tts.synthesize import Engine


@dataclass(frozen=True)
class ChatterboxVoice:
    """Chatterbox's built-in voice, or the voice of a reference recording when `reference` is set."""

    reference: Path | None
    exaggeration: float
    cfg_weight: float


@dataclass(frozen=True)
class KokoroVoice:
    """A Kokoro English voice such as `af_heart`; the first letter selects American (`a`) or British (`b`) English."""

    name: str


Voice = ChatterboxVoice | KokoroVoice


def parse_voice(data: dict[str, Any]) -> Voice:
    """Parses the `voice` of a synthesis request."""
    engine = data.get("engine")
    if engine == "chatterbox":
        reference = data.get("reference")
        return ChatterboxVoice(
            reference=Path(reference) if reference else None,
            exaggeration=float(data["exaggeration"]),
            cfg_weight=float(data["cfgWeight"]),
        )
    if engine == "kokoro":
        return KokoroVoice(name=str(data["name"]))
    raise ValueError(f"Unknown speech engine {engine!r}")


def create_engine(voice: Voice) -> Engine:
    # Engines import their model libraries on creation, so a fully cached run never loads them.
    if isinstance(voice, KokoroVoice):
        from video_tts.kokoro_engine import KokoroEngine

        return KokoroEngine(voice.name)
    from video_tts.chatterbox_engine import ChatterboxEngine

    return ChatterboxEngine(voice)
