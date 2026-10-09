from __future__ import annotations

from typing import Any

import numpy as np

from video_tts.voices import ChatterboxVoice


class ChatterboxEngine:
    def __init__(self, voice: ChatterboxVoice, model: Any | None = None) -> None:
        import torch

        self._torch = torch
        if model is None:
            from chatterbox.tts import ChatterboxTTS

            model = ChatterboxTTS.from_pretrained(device="cuda" if torch.cuda.is_available() else "cpu")
        self._model = model
        self._voice = voice
        self.sample_rate: int = model.sr

    def generate(self, text: str, seed: int) -> np.ndarray:
        # Seeding per line keeps re-synthesis of the same line reproducible.
        self._torch.manual_seed(seed)
        wav = self._model.generate(
            text,
            audio_prompt_path=str(self._voice.reference) if self._voice.reference else None,
            exaggeration=self._voice.exaggeration,
            cfg_weight=self._voice.cfg_weight,
        )
        return wav.squeeze(0).cpu().numpy()
