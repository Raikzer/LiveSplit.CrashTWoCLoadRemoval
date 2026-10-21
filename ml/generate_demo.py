"""Generate synthetic ROI images. These are NOT gameplay training/evaluation data."""
import argparse
import csv
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


def discover_fonts():
    # Use explicit font files and record hashes so rendering provenance is inspectable.
    candidates = [Path("C:/Windows/Fonts") / name for name in ("arial.ttf", "arialbd.ttf", "consola.ttf", "tahoma.ttf")]
    return [p for p in candidates if p.exists()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--groups", type=int, default=64)
    parser.add_argument("--per-group", type=int, default=12)
    parser.add_argument("--seed", type=int, default=86420)
    parser.add_argument("--font", type=Path, action="append", help="Explicit font files; repeat to vary fonts")
    args = parser.parse_args()
    if args.groups < 3 or args.per_group < 4:
        parser.error("Use at least 3 groups and 4 samples per group")
    if args.output.exists() and any(args.output.iterdir()):
        parser.error("Output must be absent or empty; avoids mixing datasets")
    args.output.mkdir(parents=True, exist_ok=True)
    fonts = args.font or discover_fonts()
    if not fonts:
        parser.error("No fonts found; supply --font path/to/font.ttf")
    rows = []
    negatives = ("LOAD", "LOADING?", "LO AD", "L O A D", "SAVING", "PAUSED", "LOA", "ADING", "SCORE", "LIVES", "LOADED", "")
    # All positive images contain exactly LOADING; avoid mislabeled words containing LOADING.
    negatives = tuple(word for word in negatives if "LOADING" not in word)
    for group in range(args.groups):
        group_seed = args.seed + group * 104729
        rng = np.random.default_rng(group_seed)
        group_name = f"synthetic-seed-{group_seed}"
        folder = args.output / group_name
        folder.mkdir()
        for sample in range(args.per_group):
            label = sample % 2
            yy, xx = np.mgrid[:100, :100]
            base = rng.uniform(0, 70, (1, 1, 3))
            gradient = (xx[..., None] * rng.uniform(-.25, .25) + yy[..., None] * rng.uniform(-.25, .25))
            background = np.clip(base + gradient + rng.normal(0, rng.uniform(1, 12), (100, 100, 3)), 0, 255).astype(np.uint8)
            image = Image.fromarray(background)
            draw = ImageDraw.Draw(image)
            if rng.random() < .35:
                for _ in range(int(rng.integers(1, 6))):
                    x, y = rng.integers(0, 90, size=2)
                    draw.rectangle((int(x), int(y), int(x + rng.integers(3, 10)), int(y + rng.integers(3, 10))),
                                   fill=tuple(int(v) for v in rng.integers(15, 100, size=3)))
            text = "LOADING" if label else negatives[int(rng.integers(len(negatives)))]
            font_path = fonts[int(rng.integers(len(fonts)))]
            font = ImageFont.truetype(str(font_path), int(rng.integers(12, 19)))
            box = draw.textbbox((0, 0), text, font=font)
            left = (100 - (box[2] - box[0])) / 2 + int(rng.integers(-5, 6))
            top = 43 + int(rng.integers(-13, 14)) - box[1]
            color = tuple(int(v) for v in rng.integers(165, 256, size=3))
            draw.text((left, top), text, font=font, fill=color, stroke_width=0)
            # A single truly black ROI, plus varied near-black negatives.
            if group == 0 and sample == 0:
                image = Image.new("RGB", (100, 100), (0, 0, 0))
            path = folder / f"{sample:03d}-{label}.png"
            image.save(path)
            rows.append({"path": path.relative_to(args.output).as_posix(), "label": label, "group": group_name})
    with (args.output / "samples.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=("path", "label", "group"))
        writer.writeheader()
        writer.writerows(rows)
    provenance = {"synthetic_demo": True, "generator": "generate_demo.py", "generator_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  "seed": args.seed, "groups": args.groups, "per_group": args.per_group,
                  "group_seed_rule": "seed + group_index * 104729; separate numpy generator per group",
                  "fonts": [{"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in fonts],
                  "limitation": "Artificial text/noise only. No game frames, capture artifacts or evidence of gameplay accuracy."}
    (args.output / "generator.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(rows)} synthetic images in {args.groups} groups: {args.output / 'samples.csv'}")


if __name__ == "__main__":
    main()
