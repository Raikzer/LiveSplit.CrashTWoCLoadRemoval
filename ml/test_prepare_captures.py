import csv
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from PIL import Image

from data import read_dataset
from prepare_captures import LabelConflictError, prepare_captures


class PrepareCapturesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "combined.csv"
        self.report = self.root / "combined.report.json"

    def image(self, name, color, size=(100, 100)):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        Image.new("RGB", size, color).save(path)
        return path

    def manifest(self, name, rows):
        path = self.root / name / "samples.csv"
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", newline="", encoding="utf-8-sig") as stream:
            writer = csv.writer(stream)
            writer.writerow(("path", "label", "group"))
            writer.writerows(rows)
        return path

    def test_identical_tensors_across_encodings_sizes_and_groups_have_one_owner(self):
        first = self.image("a/first.png", "black")
        second = self.image("b/renamed.bmp", "black", (43, 75))
        a = self.manifest("a", [(first.name, 0, "session-a"), (first.name, 0, "session-a")])
        b = self.manifest("b", [(str(second), 0, "session-b")])
        original_a, original_b = a.read_bytes(), b.read_bytes()
        result = prepare_captures([a, b], self.output)
        with self.output.open(newline="", encoding="utf-8") as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual(rows, [{"path": str(first), "label": "0", "group": "session-a"}])
        self.assertEqual(result["counts"]["input_rows"], 3)
        self.assertEqual(result["counts"]["duplicate_rows"], 2)
        self.assertEqual(len(result["omissions"]), 2)
        self.assertEqual(result["omissions"][1]["omitted"]["path"], str(second))
        self.assertEqual(json.loads(self.report.read_text())["status"], "ready")
        self.assertEqual((a.read_bytes(), b.read_bytes()), (original_a, original_b))

    def test_conflicting_labels_report_all_source_paths_and_never_replace_manifest(self):
        first = self.image("a/first.png", "black")
        second = self.image("b/second.bmp", "black", (12, 20))
        third = self.image("c/third.png", "black")
        manifests = [self.manifest("a", [(first.name, 0, "a")]),
                     self.manifest("b", [(second.name, 1, "b")]),
                     self.manifest("c", [(third.name, 0, "c")])]
        with self.assertRaises(LabelConflictError):
            prepare_captures(manifests, self.output)
        self.assertFalse(self.output.exists())
        result = json.loads(self.report.read_text())
        self.assertFalse(result["manifest_written"])
        self.assertEqual(result["counts"]["conflicting_model_inputs"], 1)
        self.assertEqual(result["counts"]["output_rows"], 0)
        self.assertEqual({source["path"] for source in result["conflicts"][0]["sources"]},
                         {str(first), str(second), str(third)})
        self.output.write_text("previous cleaned data")
        with self.assertRaises(LabelConflictError):
            prepare_captures(manifests, self.output, force=True)
        self.assertEqual(self.output.read_text(), "previous cleaned data")

    def test_three_groups_with_both_classes_load_and_remain_exclusive(self):
        manifests = []
        for group in range(3):
            rows = []
            for label in (0, 1):
                path = self.image(f"session-{group}/frame-{label}.png", (20 + group * 60, label * 100, 17))
                rows.append((path.name, label, f"recording-{group}"))
            manifests.append(self.manifest(f"session-{group}", rows))
        prepare_captures(manifests, self.output)
        pixels, labels, rows, indices = read_dataset(self.output)
        self.assertEqual(pixels.shape, (6, 1, 96, 96))
        self.assertEqual(set(labels), {0, 1})
        groups = [{rows[i]["group"] for i in ids} for ids in indices.values()]
        self.assertTrue(all(len(group) == 1 for group in groups))
        self.assertEqual(len(set.union(*groups)), 3)
        self.assertTrue(all(Path(row["path"]).is_absolute() for row in rows))

    def test_empty_malformed_missing_images_and_split_columns_fail_without_outputs(self):
        self.image("source/frame.png", "white")
        manifest = self.manifest("source", [])
        invalid = ["", "path,label,group\n", "path,label\nframe.png,0\n",
                   "path,label,group\nframe.png,,session\n",
                   "path,label,group\nframe.png,2,session\n",
                   "path,label,group\nframe.png,0,\n",
                   "path,label,group\n,0,session\n",
                   "path,label,group\nframe.png,0\n",
                   "path,label,group\nframe.png,0,session,extra\n",
                   'path,label,group\n"unterminated,0,session\n',
                   "path,label,group\nmissing.png,0,session\n",
                   "path,label,group,split\nframe.png,0,session,train\n"]
        for content in invalid:
            with self.subTest(content=content):
                manifest.write_text(content)
                with self.assertRaises((ValueError, csv.Error)):
                    prepare_captures([manifest], self.output)
                self.assertFalse(self.output.exists())
                self.assertFalse(self.report.exists())
        with self.assertRaises(ValueError):
            prepare_captures([], self.output)

    def test_overwrite_requires_force_for_both_outputs_and_sources_are_protected(self):
        image = self.image("source/frame.png", "white")
        manifest = self.manifest("source", [(image.name, 1, "recording")])
        prepare_captures([manifest], self.output)
        saved = self.output.read_bytes(), self.report.read_bytes()
        with self.assertRaises(FileExistsError):
            prepare_captures([manifest], self.output)
        self.assertEqual((self.output.read_bytes(), self.report.read_bytes()), saved)
        self.output.unlink()
        with self.assertRaises(FileExistsError):
            prepare_captures([manifest], self.output)
        self.assertFalse(self.output.exists())
        prepare_captures([manifest], self.output, force=True)
        for output, report in ((manifest, None), (self.output, manifest),
                               (image, None), (self.output, image), (self.output, self.output)):
            with self.subTest(output=output, report=report):
                with self.assertRaises(ValueError):
                    prepare_captures([manifest], output, report, force=True)
        self.assertFalse(list(self.root.rglob("*.tmp")))

    def test_cli_single_recording_success_and_nonzero_conflict(self):
        image = self.image("source/frame.png", "black")
        positive = self.image("source/positive.png", "white")
        manifest = self.manifest("source", [(image.name, 0, "recording"),
                                             (positive.name, 1, "recording")])
        command = [sys.executable, str(Path(__file__).with_name("prepare_captures.py")),
                   str(manifest), "--output", str(self.output)]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("Prepared 2 samples", result.stdout)
        with self.assertRaisesRegex(ValueError, "three recording groups"):
            read_dataset(self.output)
        # A duplicate path with a different label also requires human review.
        self.manifest("source", [(image.name, 0, "recording"), (image.name, 1, "recording")])
        result = subprocess.run(command + ["--force"], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("conflicting labels", result.stderr)


if __name__ == "__main__":
    unittest.main()
