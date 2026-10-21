"""Contract v1 preprocessing, training network, strict export and CPU reference.

The CPU reference is a developer parity tool, not a LiveSplit inference backend.
"""
import struct
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from torch import nn

HEADER = struct.Struct("<8s6I")
HEADER_VALUES = (b"TWOCML1\0", 1, 96, 96, 8, 16, 10465)
LAYERS = (("conv1.weight", (8, 1, 3, 3)), ("conv1.bias", (8,)),
          ("conv2.weight", (16, 8, 3, 3)), ("conv2.bias", (16,)),
          ("classifier.weight", (1, 9216)), ("classifier.bias", (1,)))


def preprocess_rgb(rgb):
    """Pixel-center bilinear RGB, edge clamp, then unrounded floating luminance."""
    rgb = np.asarray(rgb)
    if rgb.ndim != 3 or rgb.shape[2] != 3 or rgb.shape[0] < 1 or rgb.shape[1] < 1:
        raise ValueError("Expected a nonempty HWC RGB image")
    if rgb.dtype != np.uint8:
        raise ValueError("Expected uint8 RGB pixels")
    height, width = rgb.shape[:2]
    xs = np.clip((np.arange(96, dtype=np.float64) + .5) * (width / 96) - .5, 0, width - 1)
    ys = np.clip((np.arange(96, dtype=np.float64) + .5) * (height / 96) - .5, 0, height - 1)
    x0, y0 = xs.astype(np.int32), ys.astype(np.int32)
    x1, y1 = np.minimum(x0 + 1, width - 1), np.minimum(y0 + 1, height - 1)
    wx, wy = (xs - x0)[None, :, None], (ys - y0)[:, None, None]
    pixels = rgb.astype(np.float64)
    top = pixels[y0[:, None], x0] * (1 - wx) + pixels[y0[:, None], x1] * wx
    bottom = pixels[y1[:, None], x0] * (1 - wx) + pixels[y1[:, None], x1] * wx
    resized = top * (1 - wy) + bottom * wy
    gray = (resized[:, :, 0] * .299 + resized[:, :, 1] * .587 + resized[:, :, 2] * .114) / 255
    return np.ascontiguousarray(gray[None], dtype=np.float32)


def load_pixels(path):
    with Image.open(path) as image:
        return preprocess_rgb(np.asarray(image.convert("RGB")))


class LoadingNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.conv1 = nn.Conv2d(1, 8, 3, padding=1)
        self.conv2 = nn.Conv2d(8, 16, 3, padding=1)
        self.classifier = nn.Linear(9216, 1)

    def forward(self, pixels):
        x = torch.nn.functional.max_pool2d(torch.relu(self.conv1(pixels)), 2)
        x = torch.nn.functional.max_pool2d(torch.relu(self.conv2(x)), 2)
        return self.classifier(x.flatten(1)).flatten()


def export_model(model, path):
    state = model.state_dict()
    weights = np.concatenate([state[key].detach().cpu().numpy().reshape(-1) for key, _ in LAYERS])
    if not np.isfinite(weights).all():
        raise ValueError("Cannot export nonfinite weights")
    Path(path).write_bytes(HEADER.pack(*HEADER_VALUES) + weights.astype("<f4").tobytes())
    return read_model(path)


def read_model(path):
    raw = Path(path).read_bytes()
    if len(raw) != 41892 or HEADER.unpack(raw[:HEADER.size]) != HEADER_VALUES:
        raise ValueError("Invalid model header or exact file length")
    weights = np.frombuffer(raw, dtype="<f4", offset=HEADER.size).copy()
    if not np.isfinite(weights).all():
        raise ValueError("Nonfinite model weights")
    offset, state = 0, {}
    for key, shape in LAYERS:
        count = int(np.prod(shape))
        state[key] = weights[offset:offset + count].reshape(shape)
        offset += count
    return state


def reference_predict(state, pixels):
    """Independent NumPy float32 convolution/pooling, returns exactly one float."""
    x = np.asarray(pixels, dtype=np.float32)
    if x.shape != (1, 96, 96) or not np.isfinite(x).all() or x.min() < 0 or x.max() > 1:
        raise ValueError("Expected finite [1,96,96] pixels in [0,1]")
    for layer in ("conv1", "conv2"):
        padded = np.pad(x, ((0, 0), (1, 1), (1, 1)))
        patches = np.lib.stride_tricks.sliding_window_view(padded, (3, 3), axis=(1, 2))
        x = np.einsum("cijab,ocab->oij", patches, state[layer + ".weight"], optimize=True)
        x += state[layer + ".bias"][:, None, None]
        x = np.maximum(x, np.float32(0))
        channels, height, width = x.shape
        x = x.reshape(channels, height // 2, 2, width // 2, 2).max(axis=(2, 4))
    logit = float(state["classifier.weight"].reshape(-1) @ x.reshape(-1) + state["classifier.bias"][0])
    return float(1 / (1 + np.exp(-np.clip(logit, -80, 80))))
