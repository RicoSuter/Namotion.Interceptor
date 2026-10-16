from pathlib import Path

import numpy as np
import pytest
import soundfile

from video_tts.transcribe import transcribe_all


def test_when_items_are_transcribed_then_results_keep_their_ids(tmp_path: Path) -> None:
    # Arrange
    file = tmp_path / "a.wav"
    soundfile.write(file, np.zeros(1600, dtype=np.float32), 16000)
    heard: list[int] = []

    def recognize(audio: np.ndarray) -> str:
        heard.append(len(audio))
        return "  Hello there. "

    # Act
    results = transcribe_all([{"id": "intro", "file": str(file)}], recognize)

    # Assert
    assert results == [{"id": "intro", "heard": "Hello there."}]
    assert heard == [1600]


def test_when_sample_rate_differs_then_raises(tmp_path: Path) -> None:
    # Arrange
    file = tmp_path / "a.wav"
    soundfile.write(file, np.zeros(2400, dtype=np.float32), 24000)

    # Act & Assert
    with pytest.raises(ValueError, match="24000 Hz"):
        transcribe_all([{"id": "intro", "file": str(file)}], lambda audio: "")
