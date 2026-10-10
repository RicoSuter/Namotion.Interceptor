from pathlib import Path

import numpy as np

from video_tts.synthesize import Item, synthesize_all


class FakeEngine:
    sample_rate = 1000

    def __init__(self) -> None:
        self.generated: list[tuple[str, int]] = []

    def generate(self, text: str, seed: int) -> np.ndarray:
        self.generated.append((text, seed))
        return np.zeros(len(text) * 100, dtype=np.float32)


def test_when_items_are_new_then_wavs_are_written_and_durations_returned(tmp_path: Path) -> None:
    # Arrange
    engine = FakeEngine()
    items = [Item(key="00000000000000aa", text="Hello"), Item(key="00000000000000bb", text="Hi")]

    # Act
    durations = synthesize_all(items, tmp_path, lambda: engine)

    # Assert
    assert durations == {"00000000000000aa": 0.5, "00000000000000bb": 0.2}
    assert (tmp_path / "00000000000000aa.wav").exists()
    assert not list(tmp_path.glob("*.tmp.wav"))
    assert engine.generated[0] == ("Hello", 0)


def test_when_all_items_are_cached_then_engine_is_never_created(tmp_path: Path) -> None:
    # Arrange
    items = [Item(key="00000000000000aa", text="Hello")]
    synthesize_all(items, tmp_path, FakeEngine)
    created: list[FakeEngine] = []

    def factory() -> FakeEngine:
        created.append(FakeEngine())
        return created[-1]

    # Act
    durations = synthesize_all(items, tmp_path, factory)

    # Assert
    assert created == []
    assert durations == {"00000000000000aa": 0.5}


def test_when_key_differs_then_seed_is_derived_from_key(tmp_path: Path) -> None:
    # Arrange
    engine = FakeEngine()

    # Act
    synthesize_all([Item(key="0000000f00000000", text="Hey")], tmp_path, lambda: engine)

    # Assert
    assert engine.generated == [("Hey", 15)]
