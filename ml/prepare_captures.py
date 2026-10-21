"""Merge reviewed session exports without duplicate model inputs or split leakage."""
import argparse
import csv
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

from model import load_pixels


class LabelConflictError(ValueError):
    """Conflicts were reported; no cleaned manifest was written."""


def _atomic_write(path, write, force):
    """Publish one complete file; exclusive publication also prevents overwrite races."""
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="",
                                         dir=path.parent, prefix="." + path.name + ".",
                                         suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            write(stream)
            stream.flush()
            os.fsync(stream.fileno())
        if force:
            os.replace(temporary, path)
        else:
            os.link(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def prepare_captures(manifests, output, report=None, force=False):
    """Prepare one or more strict path,label,group CSVs, processing one image at a time.

    Manifest order and row order choose the sole owner of an identical input.
    Conflicting labels fail the entire merge and produce only a conflict report.
    """
    manifests = [Path(path).resolve() for path in manifests]
    if not manifests:
        raise ValueError("Provide at least one session manifest")
    output = Path(output).resolve()
    report = Path(report).resolve() if report is not None else output.with_suffix(".report.json")
    if output == report or output in manifests or report in manifests:
        raise ValueError("Output manifest and report must differ from each other and all source manifests")
    for destination in (output, report):
        if not destination.parent.is_dir():
            raise ValueError("Output directory does not exist: " + str(destination.parent))
        if destination.exists() and (not force or not destination.is_file()):
            raise FileExistsError("Refusing to overwrite " + str(destination) + "; use --force explicitly")

    inputs, omissions, input_rows = {}, [], 0
    for manifest in manifests:
        with manifest.open(encoding="utf-8-sig", newline="") as stream:
            reader = csv.DictReader(stream, strict=True)
            if (len(reader.fieldnames or []) != 3 or
                    set(reader.fieldnames) != {"path", "label", "group"}):
                raise ValueError(str(manifest) + ": requires exactly path,label,group columns; assign splits after preparation")
            manifest_rows = 0
            for row in reader:
                location = str(manifest) + ":" + str(reader.line_num)
                if None in row or any(value is None for value in row.values()):
                    raise ValueError(location + ": each row requires path,label,group")
                if row["label"].strip() not in ("0", "1"):
                    raise ValueError(location + ": labels must be exactly 0 or 1")
                if not row["path"].strip() or not row["group"].strip():
                    raise ValueError(location + ": image path and recording group must be nonempty")
                path = (manifest.parent / row["path"]).resolve()
                if path in (output, report):
                    raise ValueError("An output would overwrite a source image: " + str(path))
                source = {"manifest": str(manifest), "line": reader.line_num,
                          "path": str(path), "label": int(row["label"]),
                          "group": row["group"].strip()}
                try:
                    digest = hashlib.sha256(load_pixels(path).tobytes()).hexdigest()
                except (OSError, ValueError) as error:
                    raise ValueError(location + ": cannot preprocess " + str(path) + ": " + str(error)) from error
                input_rows += 1
                manifest_rows += 1
                if digest not in inputs:
                    inputs[digest] = [source]
                else:
                    owner = inputs[digest][0]
                    inputs[digest].append(source)
                    if owner["label"] == source["label"]:
                        omissions.append({"reason": "identical_model_input", "tensor_sha256": digest,
                                          "omitted": source, "retained": owner})
            if not manifest_rows:
                raise ValueError(str(manifest) + ": no labeled samples; manifest is empty")

    conflicts = [{"tensor_sha256": digest, "sources": sources}
                 for digest, sources in inputs.items()
                 if len({source["label"] for source in sources}) > 1]
    owners = [sources[0] for sources in inputs.values()]
    result = {
        "status": "label_conflicts" if conflicts else "ready",
        "output_manifest": str(output),
        "manifest_written": not bool(conflicts),
        "source_manifests": [str(path) for path in manifests],
        "counts": {"input_manifests": len(manifests), "input_rows": input_rows,
                   "unique_model_inputs": len(inputs), "duplicate_rows": len(omissions),
                   "conflicting_model_inputs": len(conflicts),
                   "conflicting_rows": sum(len(item["sources"]) for item in conflicts),
                   "output_rows": 0 if conflicts else len(owners),
                   "output_groups": 0 if conflicts else len({row["group"] for row in owners})},
        "omissions": omissions,
        "conflicts": conflicts,
    }

    if not conflicts:
        def write_manifest(stream):
            writer = csv.DictWriter(stream, fieldnames=("path", "label", "group"))
            writer.writeheader()
            writer.writerows({key: row[key] for key in ("path", "label", "group")} for row in owners)
        _atomic_write(output, write_manifest, force)
    _atomic_write(report, lambda stream: json.dump(result, stream, indent=2), force)
    if conflicts:
        raise LabelConflictError(str(len(conflicts)) + " identical model inputs have conflicting labels; "
                                 "no manifest written. Review " + str(report))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifests", nargs="+", help="Reviewed session samples.csv files, in ownership order")
    parser.add_argument("--output", required=True, help="Cleaned training CSV (absolute image paths)")
    parser.add_argument("--report", help="JSON report (default: OUTPUT stem + .report.json)")
    parser.add_argument("--force", action="store_true", help="Explicitly allow replacing generated output/report")
    args = parser.parse_args()
    try:
        result = prepare_captures(args.manifests, args.output, args.report, args.force)
    except (OSError, ValueError, csv.Error) as error:
        print("Preparation failed: " + str(error), file=sys.stderr)
        return 1
    counts = result["counts"]
    print(f"Prepared {counts['output_rows']} samples in {counts['output_groups']} recording groups; "
          f"omitted {counts['duplicate_rows']} duplicates. Report: "
          f"{Path(args.report).resolve() if args.report else Path(args.output).resolve().with_suffix('.report.json')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
