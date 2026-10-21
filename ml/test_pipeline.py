import csv
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from data import read_dataset
from model import LoadingNet, export_model, load_pixels, preprocess_rgb, read_model, reference_predict


class PipelineTests(unittest.TestCase):
    def test_preprocess_centers_edges_and_float_luminance(self):
        rgb = np.array([[[255, 0, 0], [0, 255, 0]], [[0, 0, 255], [255, 255, 255]]], dtype=np.uint8)
        pixels = preprocess_rgb(rgb)
        self.assertEqual(pixels.shape, (1, 96, 96))
        self.assertEqual(pixels.dtype, np.float32)
        self.assertAlmostEqual(float(pixels[0, 0, 0]), .299, places=7)
        self.assertAlmostEqual(float(pixels[0, 95, 95]), 1, places=7)
        # Independent scalar formula at one interpolated location.
        x, y = (40.5 * 2 / 96 - .5), (60.5 * 2 / 96 - .5)
        color = rgb[0, 0] * (1-x)*(1-y) + rgb[0, 1]*x*(1-y) + rgb[1, 0]*(1-x)*y + rgb[1, 1]*x*y
        expected = float(color @ np.array([.299, .587, .114]) / 255)
        self.assertAlmostEqual(float(pixels[0, 60, 40]), expected, places=7)

    def test_export_roundtrip_and_independent_reference(self):
        torch.manual_seed(44)
        torch.set_num_threads(2)
        model = LoadingNet().eval()
        pixels = np.random.default_rng(123).random((1, 96, 96), dtype=np.float32)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "test.twml"
            state = export_model(model, path)
            self.assertEqual(path.stat().st_size, 41892)
            with torch.no_grad():
                expected = torch.sigmoid(model(torch.from_numpy(pixels[None]))).item()
            self.assertAlmostEqual(reference_predict(state, pixels), expected, places=5)
            raw = path.read_bytes()
            for broken in (raw[:-1], raw + b"\0", b"BADMAGIC" + raw[8:], raw[:32] + struct.pack("<f", float("nan")) + raw[36:]):
                path.write_bytes(broken)
                with self.assertRaises(ValueError):
                    read_model(path)
            for invalid in (np.full_like(pixels, np.nan), np.full_like(pixels, 1.1), pixels[:, :95]):
                with self.assertRaises(ValueError):
                    reference_predict(state, invalid)

    def make_dataset(self, directory):
        rng = np.random.default_rng(22)
        rows = []
        for group in range(9):
            for label in (0, 1):
                path = f"{group}-{label}.png"
                Image.fromarray(rng.integers(0, 256, (100, 100, 3), dtype=np.uint8)).save(directory / path)
                rows.append({"path": path, "label": label, "group": f"recording-{group}"})
        return rows

    def write_manifest(self, directory, rows):
        path = directory / "samples.csv"
        fields = list(rows[0]) if rows else ["path", "label", "group"]
        with path.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=fields)
            writer.writeheader()
            writer.writerows(rows)
        return path

    def test_splits_reproducible_and_group_exclusive(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            path = self.write_manifest(directory, self.make_dataset(directory))
            a = read_dataset(path, 12)
            b = read_dataset(path, 12)
            self.assertEqual([r["split"] for r in a[2]], [r["split"] for r in b[2]])
            sets = [{r["group"] for r in a[2] if r["split"] == split} for split in ("train", "val", "test")]
            self.assertFalse(sets[0] & sets[1] or sets[1] & sets[2] or sets[0] & sets[2])

    def test_rejects_empty_singleclass_and_leaks(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            original = self.make_dataset(directory)
            cases = [[], [r for r in original if r["label"] == 0], original + [original[0].copy()]]
            for rows in cases:
                with self.assertRaises(ValueError):
                    read_dataset(self.write_manifest(directory, rows))
            # Renaming/re-encoding an existing image cannot evade model-input hashing.
            with Image.open(directory / original[0]["path"]) as image:
                image.save(directory / "copy.bmp")
            rows = original + [{"path": "copy.bmp", "label": 0, "group": "other"}]
            with self.assertRaisesRegex(ValueError, "content"):
                read_dataset(self.write_manifest(directory, rows))
            rows = [dict(row, split=("train" if i % 2 else "test")) for i, row in enumerate(original)]
            with self.assertRaisesRegex(ValueError, "group"):
                read_dataset(self.write_manifest(directory, rows))

    def make_temporal_dataset(self, directory):
        rows = self.make_dataset(directory)[:6]
        for row, split, frame_index in zip(rows, ("train", "train", "val", "val", "test", "test"),
                                            (0, 1, 92, 93, 184, 185)):
            row.update(group="original-session", split=split, frame_index=frame_index)
        return rows

    def test_temporal_holdout_accepts_explicit_chronology_without_inventing_groups(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            rows = self.make_temporal_dataset(directory)
            _, _, loaded, indices = read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)
            self.assertEqual([row["frame_index"] for row in loaded], [0, 1, 92, 93, 184, 185])
            for split in ("train", "val", "test"):
                self.assertEqual(len(indices[split]), 2)
                self.assertEqual({loaded[i]["group"] for i in indices[split]}, {"original-session"})

    def test_temporal_holdout_requires_opt_in_and_one_original_group(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            rows = self.make_temporal_dataset(directory)
            with self.assertRaisesRegex(ValueError, "group"):
                read_dataset(self.write_manifest(directory, rows))
            for row in rows:
                row.pop("split")
            with self.assertRaisesRegex(ValueError, "three recording groups"):
                read_dataset(self.write_manifest(directory, rows))
            rows = self.make_temporal_dataset(directory)
            rows[-1]["group"] = "second-recording"
            with self.assertRaisesRegex(ValueError, "exactly one"):
                read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)

    def test_temporal_holdout_rejects_missing_invalid_and_unordered_frame_indices(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            original = self.make_temporal_dataset(directory)
            for invalid in ("", -1, "1.5", "nan", "1e2", "abc", 0, 100):
                rows = [row.copy() for row in original]
                rows[1]["frame_index"] = invalid
                with self.subTest(frame_index=invalid), self.assertRaisesRegex(ValueError, "frame_index"):
                    read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)
            rows = [dict(row) for row in original]
            for row in rows:
                row.pop("frame_index")
            with self.assertRaisesRegex(ValueError, "frame_index"):
                read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)

    def test_temporal_holdout_rejects_missing_backwards_and_interleaved_splits(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            original = self.make_temporal_dataset(directory)
            for splits in (("train", "", "val", "val", "test", "test"),
                           ("train", "validation", "val", "val", "test", "test"),
                           ("val", "val", "train", "train", "test", "test"),
                           ("train", "val", "train", "val", "test", "test")):
                rows = [dict(row, split=split) for row, split in zip(original, splits)]
                with self.subTest(splits=splits), self.assertRaisesRegex(ValueError, "split"):
                    read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)
            for row in original:
                row.pop("split")
            with self.assertRaisesRegex(ValueError, "explicit"):
                read_dataset(self.write_manifest(directory, original), temporal_holdout=True)

    def test_temporal_holdout_enforces_excluded_frame_gap_at_both_boundaries(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            original = self.make_temporal_dataset(directory)
            for position in (2, 4):
                rows = [row.copy() for row in original]
                rows[position]["frame_index"] -= 1  # Only 89 excluded indices, despite distance 90.
                with self.subTest(boundary=position), self.assertRaisesRegex(ValueError, "90 excluded"):
                    read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)
            path = self.write_manifest(directory, original)
            with self.assertRaisesRegex(ValueError, "91 excluded"):
                read_dataset(path, temporal_holdout=True, temporal_gap_frames=91)
            read_dataset(path, temporal_holdout=True, temporal_gap_frames=89)
            for gap in (0, -1, 1.5, True):
                with self.subTest(gap=gap), self.assertRaisesRegex(ValueError, "positive integer"):
                    read_dataset(path, temporal_holdout=True, temporal_gap_frames=gap)

    def test_temporal_holdout_still_rejects_duplicates_and_single_class_splits(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            original = self.make_temporal_dataset(directory)
            rows = [row.copy() for row in original]
            rows[-1]["path"] = rows[0]["path"]
            with self.assertRaisesRegex(ValueError, "Duplicate image path"):
                read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)
            with Image.open(directory / original[0]["path"]) as image:
                image.save(directory / "temporal-copy.bmp")
            rows[-1]["path"] = "temporal-copy.bmp"
            with self.assertRaisesRegex(ValueError, "Duplicate image content"):
                read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)
            for split in ("train", "val", "test"):
                rows = [dict(row, label=0 if row["split"] == split else row["label"]) for row in original]
                with self.subTest(split=split), self.assertRaisesRegex(ValueError, split + " split must contain both classes"):
                    read_dataset(self.write_manifest(directory, rows), temporal_holdout=True)

    def test_training_cli_rejects_nonpositive_temporal_gap(self):
        result = subprocess.run([sys.executable, str(Path(__file__).with_name("train.py")),
                                 "unused.csv", "--output", "unused", "--temporal-gap-frames", "0"],
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("temporal-gap-frames must be positive", result.stderr)

    def test_predict_stdout_is_one_float(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            model_path = directory / "model.twml"
            export_model(LoadingNet(), model_path)
            image_path = directory / "roi.png"
            Image.new("RGB", (100, 100), "black").save(image_path)
            result = subprocess.run([sys.executable, str(Path(__file__).with_name("predict.py")),
                                     str(model_path), str(image_path)], capture_output=True, text=True, check=True)
            self.assertEqual(len(result.stdout.split()), 1)
            self.assertTrue(0 <= float(result.stdout) <= 1)


if __name__ == "__main__":
    unittest.main()
