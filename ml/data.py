"""Validated manifest loading and deterministic group-exclusive splits."""
import csv
import hashlib
import re
from pathlib import Path

import numpy as np
from model import load_pixels

SPLITS = ("train", "val", "test")


def read_dataset(manifest, seed=1234, *, temporal_holdout=False, temporal_gap_frames=90):
    if isinstance(temporal_gap_frames, bool) or not isinstance(temporal_gap_frames, int) or temporal_gap_frames < 1:
        raise ValueError("temporal_gap_frames must be a positive integer")
    manifest = Path(manifest).resolve()
    with manifest.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if not {"path", "label", "group"}.issubset(reader.fieldnames or []):
            raise ValueError("Manifest requires path,label,group columns")
        rows = list(reader)
    if not rows:
        raise ValueError("No data: manifest is empty")
    if temporal_holdout:
        previous_index, previous_split = -1, -1
        for row in rows:
            frame_index = (row.get("frame_index") or "").strip()
            if re.fullmatch(r"[0-9]+", frame_index) is None:
                raise ValueError("Temporal holdout requires a nonnegative integer frame_index on every row")
            row["frame_index"] = int(frame_index)
            if row["frame_index"] <= previous_index:
                raise ValueError("Temporal frame_index must be unique and strictly increasing in chronological row order")
            previous_index = row["frame_index"]
            split = (row.get("split") or "").strip()
            if split not in SPLITS:
                raise ValueError("Temporal holdout requires explicit train, val, test split on every row")
            split_index = SPLITS.index(split)
            if split_index < previous_split:
                raise ValueError("Temporal splits must be contiguous and ordered train then val then test; no interleaving")
            previous_split = split_index
    paths, contents, group_split = {}, {}, {}
    tensors, labels = [], []
    for row in rows:
        path = (manifest.parent / row["path"]).resolve()
        if row["label"].strip() not in ("0", "1"):
            raise ValueError("Labels must be exactly 0 or 1")
        row["label"] = int(row["label"])
        row["group"] = row["group"].strip()
        row["split"] = (row.get("split") or "").strip()
        if not row["group"]:
            raise ValueError("Every sample needs a recording/session group")
        if row["split"] not in ("", *SPLITS):
            raise ValueError("Split must be train, val, test or omitted")
        if str(path).casefold() in paths:
            raise ValueError("Duplicate image path: " + str(path))
        paths[str(path).casefold()] = row
        pixels = load_pixels(path)
        digest = hashlib.sha256(pixels.tobytes()).hexdigest()
        # Reject repeated model inputs even if renamed, re-encoded or differently sized.
        if digest in contents:
            raise ValueError("Duplicate image content/model input: " + str(path))
        contents[digest] = row
        row["tensor_sha256"] = digest
        tensors.append(pixels)
        labels.append(row["label"])
        if not temporal_holdout and row["group"] in group_split and group_split[row["group"]] != row["split"]:
            raise ValueError("A group spans explicit splits or partially specified splits")
        group_split[row["group"]] = row["split"]
    if len(set(labels)) != 2:
        raise ValueError("Dataset must contain both classes")
    groups = sorted(group_split)
    if temporal_holdout and len(groups) != 1:
        raise ValueError("Temporal holdout requires exactly one original recording/session group")
    if not temporal_holdout and len(groups) < 3:
        raise ValueError("At least three recording groups are required for train/val/test")
    specified = [bool(row["split"]) for row in rows]
    if any(specified) and not all(specified):
        raise ValueError("Specify split for every row or omit it for every row")
    if not any(specified):
        rng = np.random.default_rng(seed)
        n_val = n_test = max(1, round(len(groups) * .15))
        for _ in range(1000):
            order = list(rng.permutation(groups))
            assignments = {group: ("val" if i < n_val else "test" if i < n_val + n_test else "train")
                           for i, group in enumerate(order)}
            if all({r["label"] for r in rows if assignments[r["group"]] == split} == {0, 1}
                   for split in SPLITS):
                break
        else:
            raise ValueError("Cannot create both-class group splits; add groups or provide explicit splits")
        for row in rows:
            row["split"] = assignments[row["group"]]
    indices = {split: np.array([i for i, row in enumerate(rows) if row["split"] == split], dtype=np.int64)
               for split in SPLITS}
    for split, ids in indices.items():
        if {labels[i] for i in ids} != {0, 1}:
            raise ValueError(split + " split must contain both classes")
    if temporal_holdout:
        for earlier, later in zip(SPLITS, SPLITS[1:]):
            excluded = rows[indices[later][0]]["frame_index"] - rows[indices[earlier][-1]]["frame_index"] - 1
            if excluded < temporal_gap_frames:
                raise ValueError(f"Temporal holdout requires at least {temporal_gap_frames} excluded frame indices between {earlier} and {later}")
    return np.stack(tensors), np.array(labels, dtype=np.float32), rows, indices
