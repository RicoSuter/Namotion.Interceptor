from pathlib import Path
from types import SimpleNamespace
from typing import Any

import numpy as np
import pytest

from video_tts.chatterbox_engine import ChatterboxEngine
from video_tts.kokoro_engine import KokoroEngine
from video_tts.voices import ChatterboxVoice, KokoroVoice, parse_voice


def test_when_request_names_chatterbox_then_voice_carries_settings() -> None:
    # Act
    voice = parse_voice({"engine": "chatterbox", "reference": None, "exaggeration": 0.35, "cfgWeight": 0.3})

    # Assert
    assert voice == ChatterboxVoice(reference=None, exaggeration=0.35, cfg_weight=0.3)


def test_when_request_has_reference_then_voice_clones_it() -> None:
    # Act
    voice = parse_voice({"engine": "chatterbox", "reference": "/voices/a.wav", "exaggeration": 0.5, "cfgWeight": 0.5})

    # Assert
    assert isinstance(voice, ChatterboxVoice)
    assert voice.reference == Path("/voices/a.wav")


def test_when_request_names_kokoro_then_voice_has_its_name() -> None:
    # Act & Assert
    assert parse_voice({"engine": "kokoro", "name": "af_heart"}) == KokoroVoice(name="af_heart", speed=1.0)


def test_when_kokoro_request_has_speed_then_voice_carries_it() -> None:
    # Act & Assert
    assert parse_voice({"engine": "kokoro", "name": "am_michael", "speed": 1.34}) == KokoroVoice(name="am_michael", speed=1.34)


def test_when_engine_is_unknown_then_raises() -> None:
    # Act & Assert
    with pytest.raises(ValueError, match="Unknown speech engine 'espeak'"):
        parse_voice({"engine": "espeak"})


class FakeChatterboxModel:
    sr = 24000

    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []

    def generate(self, text: str, **settings: Any) -> Any:
        import torch

        self.calls.append((text, settings))
        return torch.zeros(1, 10)


def test_when_chatterbox_generates_then_settings_and_reference_are_passed() -> None:
    # Arrange
    model = FakeChatterboxModel()
    engine = ChatterboxEngine(ChatterboxVoice(reference=Path("/voices/a.wav"), exaggeration=0.35, cfg_weight=0.3), model=model)

    # Act
    audio = engine.generate("Hello.", seed=1)

    # Assert
    assert audio.shape == (10,)
    assert engine.sample_rate == 24000
    assert model.calls == [("Hello.", {"audio_prompt_path": "/voices/a.wav", "exaggeration": 0.35, "cfg_weight": 0.3})]


def test_when_kokoro_returns_chunks_then_audio_is_concatenated() -> None:
    # Arrange
    calls: list[tuple[str, dict[str, Any]]] = []

    def pipeline(text: str, **options: Any) -> list[SimpleNamespace]:
        calls.append((text, options))
        return [SimpleNamespace(audio=np.ones(3)), SimpleNamespace(audio=None), SimpleNamespace(audio=np.zeros(2))]

    engine = KokoroEngine("bm_george", pipeline=pipeline)

    # Act
    audio = engine.generate("Hello.", seed=1)

    # Assert
    assert audio.tolist() == [1, 1, 1, 0, 0]
    assert audio.dtype == np.float32
    assert calls == [("Hello.", {"voice": "bm_george", "speed": 1.0})]


def test_when_kokoro_has_speed_then_pipeline_synthesizes_at_it() -> None:
    # Arrange
    calls: list[dict[str, Any]] = []

    def pipeline(text: str, **options: Any) -> list[SimpleNamespace]:
        calls.append(options)
        return [SimpleNamespace(audio=np.ones(3))]

    engine = KokoroEngine("am_michael", speed=1.34, pipeline=pipeline)

    # Act
    engine.generate("Hello.", seed=1)

    # Assert
    assert calls == [{"voice": "am_michael", "speed": 1.34}]


def test_when_kokoro_returns_no_audio_then_raises() -> None:
    # Arrange
    engine = KokoroEngine("af_heart", pipeline=lambda text, **options: [])

    # Act & Assert
    with pytest.raises(RuntimeError, match="no audio"):
        engine.generate("Hello.", seed=1)
