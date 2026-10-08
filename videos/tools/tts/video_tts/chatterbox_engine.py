from __future__ import annotations

from pathlib import Path

import numpy as np


class ChatterboxEngine:
    def __init__(self, voice_prompt: Path | None) -> None:
        import torch
        from chatterbox.tts import ChatterboxTTS

        self._torch = torch
        self._model = ChatterboxTTS.from_pretrained(device="cuda" if torch.cuda.is_available() else "cpu")
        self._voice_prompt = str(voice_prompt) if voice_prompt else None
        self.sample_rate: int = self._model.sr

    def generate(self, text: str, seed: int) -> np.ndarray:
        # Seeding per line keeps re-synthesis of the same line reproducible.
        self._torch.manual_seed(seed)
        wav = self._model.generate(text, audio_prompt_path=self._voice_prompt)
        return wav.squeeze(0).cpu().numpy()
