"""Compare the native Vulkan C ABI against exported reference tensors (no fallback)."""
import argparse
import ctypes as ct
import json
from pathlib import Path

import numpy as np


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dll", type=Path)
    parser.add_argument("model", type=Path)
    parser.add_argument("shaders", type=Path)
    parser.add_argument("--parity", type=Path, default=Path(__file__).parent / "artifacts/demo/parity.npz")
    parser.add_argument("--atol", type=float, default=2e-4)
    parser.add_argument("--validation", action="store_true")
    args = parser.parse_args()
    lib = ct.CDLL(str(args.dll.resolve()))
    lib.TwmCreate.argtypes = [ct.c_wchar_p, ct.c_wchar_p, ct.c_int, ct.POINTER(ct.c_void_p), ct.c_char_p, ct.c_uint]
    lib.TwmCreate.restype = ct.c_int
    lib.TwmPredict.argtypes = [ct.c_void_p, ct.POINTER(ct.c_float), ct.c_uint, ct.POINTER(ct.c_float), ct.c_char_p, ct.c_uint]
    lib.TwmPredict.restype = ct.c_int
    lib.TwmGetDeviceName.argtypes = [ct.c_void_p, ct.c_char_p, ct.c_uint]
    lib.TwmGetDeviceName.restype = ct.c_int
    lib.TwmDestroy.argtypes = [ct.c_void_p]
    lib.TwmDestroy.restype = None
    handle, error = ct.c_void_p(), ct.create_string_buffer(2048)
    status = lib.TwmCreate(str(args.model.resolve()), str(args.shaders.resolve()), int(args.validation), ct.byref(handle), error, len(error))
    if status:
        raise RuntimeError(error.value.decode("utf-8", errors="replace"))
    try:
        name = ct.create_string_buffer(1024)
        if lib.TwmGetDeviceName(handle, name, len(name)):
            raise RuntimeError("Cannot obtain Vulkan device name")
        with np.load(args.parity, allow_pickle=False) as data:
            expected, predictions = data["confidence"], []
            for pixels in data["pixels"]:
                pixels = np.ascontiguousarray(pixels, dtype=np.float32)
                confidence = ct.c_float()
                status = lib.TwmPredict(handle, pixels.ctypes.data_as(ct.POINTER(ct.c_float)), pixels.size,
                                        ct.byref(confidence), error, len(error))
                if status:
                    raise RuntimeError(error.value.decode("utf-8", errors="replace"))
                predictions.append(confidence.value)
        actual = np.array(predictions)
        if not np.isfinite(actual).all() or (actual < 0).any() or (actual > 1).any():
            raise AssertionError("Native confidence outside finite [0,1]")
        maximum = float(abs(actual - expected).max())
        print(json.dumps({"device": name.value.decode("utf-8", errors="replace"),
                          "validation_enabled": args.validation, "samples": len(expected),
                          "reference": expected.tolist(), "native": predictions,
                          "max_absolute_error": maximum, "atol": args.atol}))
        if maximum > args.atol:
            raise AssertionError("Native predictions differ from CPU reference")
    finally:
        lib.TwmDestroy(handle)


if __name__ == "__main__":
    main()
