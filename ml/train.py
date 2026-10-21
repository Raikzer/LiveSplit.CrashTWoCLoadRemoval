"""Deterministic, CPU-only training with group-exclusive held-out evaluation."""
import argparse
import hashlib
import json
import platform
import random
from pathlib import Path

import numpy as np
import PIL
import torch
from torch import nn
from data import read_dataset, SPLITS
from model import LoadingNet, export_model, reference_predict


def metrics(labels, confidences, threshold):
    predicted = confidences >= threshold
    positive = labels == 1
    tp, tn = int((predicted & positive).sum()), int((~predicted & ~positive).sum())
    fp, fn = int((predicted & ~positive).sum()), int((~predicted & positive).sum())
    precision = tp / max(1, tp + fp)
    recall = tp / max(1, tp + fn)
    return {"count": len(labels), "threshold": float(threshold), "tp": tp, "tn": tn, "fp": fp, "fn": fn,
            "accuracy": (tp + tn) / len(labels), "precision": precision, "recall": recall,
            "f1": 2 * precision * recall / max(1e-12, precision + recall),
            "balanced_accuracy": .5 * (recall + tn / max(1, tn + fp))}


def augment(pixels):
    # Applied only to training batches. Keep the small, already-cropped word visible.
    count = pixels.shape[0]
    affine = torch.eye(2, 3).unsqueeze(0).repeat(count, 1, 1)
    affine[:, 0, 2] = (torch.rand(count) - .5) * .06
    affine[:, 1, 2] = (torch.rand(count) - .5) * .06
    grid = nn.functional.affine_grid(affine, pixels.size(), align_corners=False)
    pixels = nn.functional.grid_sample(pixels, grid, padding_mode="border", align_corners=False)
    gain = .8 + torch.rand(count, 1, 1, 1) * .4
    return (pixels * gain + torch.randn_like(pixels) * .012).clamp(0, 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--epochs", type=int, default=15)
    parser.add_argument("--patience", type=int, default=5)
    parser.add_argument("--batch-size", type=int, default=32)
    parser.add_argument("--seed", type=int, default=1234)
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--temporal-holdout", action="store_true",
                        help="Opt into chronological splits within ONE session; no independent recording evaluation")
    parser.add_argument("--temporal-gap-frames", type=int, default=90,
                        help="Minimum excluded frame indices between temporal splits (positive; default 90)")
    parser.add_argument("--synthetic-demo", action="store_true",
                        help="Mark export as demo only; required for synthetic generated data")
    args = parser.parse_args()
    if min(args.epochs, args.patience, args.batch_size, args.threads) < 1:
        parser.error("epochs, patience, batch-size and threads must be positive")
    if args.temporal_gap_frames < 1:
        parser.error("temporal-gap-frames must be positive")
    generator_provenance = args.manifest.parent / "generator.json"
    if generator_provenance.exists() and not args.synthetic_demo:
        parser.error("Synthetic generator data requires --synthetic-demo")
    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    torch.set_num_threads(args.threads)
    torch.use_deterministic_algorithms(True)
    if args.temporal_holdout:
        print("Single-session temporal holdout: train, validation and test share one recording. "
              "No independent recording evaluation exists; results do not establish generalization to other sessions.", flush=True)
    pixels, labels, rows, indices = read_dataset(
        args.manifest, args.seed, temporal_holdout=args.temporal_holdout,
        temporal_gap_frames=args.temporal_gap_frames)
    x, y = torch.from_numpy(pixels), torch.from_numpy(labels)
    model = LoadingNet().cpu()
    train_ids = torch.from_numpy(indices["train"])
    train_labels = y[train_ids]
    positive_weight = float((train_labels == 0).sum() / (train_labels == 1).sum())
    criterion = nn.BCEWithLogitsLoss(pos_weight=torch.tensor(positive_weight))
    optimizer = torch.optim.Adam(model.parameters(), lr=.001)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    checkpoint = output / "best.pt"
    best_loss, best_epoch, history = float("inf"), 0, []
    for epoch in range(1, args.epochs + 1):
        model.train()
        losses = []
        order = train_ids[torch.randperm(len(train_ids))]
        for batch in order.split(args.batch_size):
            optimizer.zero_grad(set_to_none=True)
            loss = criterion(model(augment(x[batch])), y[batch])
            if not torch.isfinite(loss):
                raise ValueError("Nonfinite training loss")
            loss.backward()
            optimizer.step()
            losses.append((float(loss.detach()), len(batch)))
        model.eval()
        with torch.no_grad():
            val_ids = indices["val"]
            val_logits = torch.cat([model(x[batch]) for batch in np.array_split(val_ids, max(1, len(val_ids) // 64))])
            val_loss = float(nn.functional.binary_cross_entropy_with_logits(val_logits, y[val_ids]))
        record = {"epoch": epoch, "train_loss": sum(a * b for a, b in losses) / len(train_ids), "val_loss": val_loss}
        history.append(record)
        print(json.dumps(record), flush=True)
        if val_loss < best_loss - 1e-6:
            best_loss, best_epoch = val_loss, epoch
            torch.save(model.state_dict(), checkpoint)
        elif epoch - best_epoch >= args.patience:
            break
    model.load_state_dict(torch.load(checkpoint, map_location="cpu", weights_only=True))
    model.eval()
    probabilities = {}
    with torch.no_grad():
        for split in SPLITS:
            ids = indices[split]
            probabilities[split] = torch.cat([torch.sigmoid(model(x[batch])) for batch in
                np.array_split(ids, max(1, len(ids) // 64))]).numpy()
    # Threshold is chosen on validation only; test data never influences model/threshold.
    thresholds = np.linspace(.05, .95, 181)
    threshold = max(thresholds, key=lambda t: (metrics(labels[indices["val"]], probabilities["val"], t)["balanced_accuracy"],
                                             -abs(t - .5)))
    split_metrics = {split: metrics(labels[indices[split]], probabilities[split], threshold) for split in SPLITS}
    binary = output / "loading-text.twml"
    state = export_model(model, binary)
    # Export representative held-out tensors with predictions for independent Vulkan parity.
    test_ids = indices["test"]
    chosen = [next(int(i) for i in test_ids if labels[i] == label) for label in (0, 1)]
    chosen += [int(test_ids[np.argmin(abs(probabilities["test"] - .5))])]
    chosen = list(dict.fromkeys(chosen))
    reference = np.array([reference_predict(state, pixels[i]) for i in chosen], dtype=np.float32)
    with torch.no_grad():
        pytorch = torch.sigmoid(model(x[chosen])).numpy()
    if not np.allclose(reference, pytorch, rtol=1e-4, atol=2e-5):
        raise ValueError("Exported NumPy reference disagrees with PyTorch")
    np.savez_compressed(output / "parity.npz", pixels=pixels[chosen], confidence=reference, labels=labels[chosen])
    sample_dir = output / "parity-samples"
    sample_dir.mkdir(exist_ok=True)
    from PIL import Image
    for number, i in enumerate(chosen):
        with Image.open(args.manifest.parent / rows[i]["path"]) as image:
            image.convert("RGB").save(sample_dir / f"{number}.png")
        np.save(sample_dir / f"{number}.npy", pixels[i], allow_pickle=False)
    (sample_dir / "expected.json").write_text(json.dumps([
        {"sample": n, "label": int(labels[i]), "confidence": float(reference[n]), "source": rows[i]["path"]}
        for n, i in enumerate(chosen)], indent=2) + "\n", encoding="utf-8")
    provenance_rows = [{key: row[key] for key in ("path", "label", "group", "split", "tensor_sha256", "frame_index")
                        if key in row} for row in rows]
    metadata = {
        "format_version": 1, "synthetic_demo": args.synthetic_demo, "synthetic_demo_only": args.synthetic_demo,
        "intended_use": "Synthetic pipeline demonstration; unvalidated on gameplay" if args.synthetic_demo else "User-trained ROI classifier; validate on representative held-out recordings",
        "seed": args.seed, "best_epoch": best_epoch, "best_validation_loss": best_loss,
        "recommended_threshold": float(threshold), "threshold_selection": "maximum validation balanced accuracy; ties nearest 0.5",
        "metrics": split_metrics, "positive_class_weight": positive_weight,
        "evaluation_protocol": {
            "mode": "single_session_temporal_holdout" if args.temporal_holdout else "independent_recording_holdout",
            "independent_recordings": len({row["group"] for row in rows}),
            **({"gap_frames": args.temporal_gap_frames,
                "limitation": "No independent recording evaluation exists; all splits share one session and do not establish generalization to other recordings."}
               if args.temporal_holdout else {}),
        },
        "training": {"epochs_requested": args.epochs, "epochs_completed": len(history), "patience": args.patience,
                     "batch_size": args.batch_size, "threads": args.threads, "optimizer": "Adam", "learning_rate": .001,
                     "augmentation": "train only: bilinear translation +/-2.88 px, gain 0.8-1.2, Gaussian noise sigma 0.012"},
        "environment": {"python": platform.python_version(), "torch": torch.__version__, "numpy": np.__version__, "pillow": PIL.__version__, "device": "cpu"},
        "manifest_sha256": hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
        "model_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
        "split_summary": {split: {"samples": len(ids), "positive": int(labels[ids].sum()),
                                  "groups": sorted({rows[i]["group"] for i in ids})} for split, ids in indices.items()},
        "dataset": provenance_rows, "parity_max_absolute_error": float(abs(reference - pytorch).max()),
        "generator": json.loads(generator_provenance.read_text(encoding="utf-8")) if generator_provenance.exists() else None,
    }
    (output / "loading-text.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    (output / "history.json").write_text(json.dumps(history, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"model": str(binary), "best_epoch": best_epoch, "metrics": split_metrics,
                      "synthetic_demo_only": args.synthetic_demo}), flush=True)


if __name__ == "__main__":
    main()
