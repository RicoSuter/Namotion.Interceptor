from __future__ import annotations

import json
import sys
from pathlib import Path

from video_tts.synthesize import Item, synthesize_all
from video_tts.voices import create_engine, parse_voice


def main() -> None:
    request = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    output_directory = Path(request["outputDirectory"])
    voice = parse_voice(request["voice"])
    items = [Item(key=item["key"], text=item["text"]) for item in request["items"]]

    durations = synthesize_all(items, output_directory, lambda: create_engine(voice))
    (output_directory / "durations.json").write_text(json.dumps(durations, indent=2), encoding="utf-8")
    print(f"{len(items)} lines, {sum(durations.values()):.1f} s of audio")


if __name__ == "__main__":
    main()
