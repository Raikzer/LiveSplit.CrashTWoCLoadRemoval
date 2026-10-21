# Loading-text classifier contract, version 1

The only prediction output is one float32 sigmoid confidence in [0, 1] that the
captured image contains the English loading text. There is no OCR text output.
Training and runtime must share this contract exactly.

## Input and network

- Runtime takes the existing raw (not thresholded) English capture ROI, normally
  100x100. Training files are captures of that same ROI, not arbitrary full frames.
- Resize to **96x96** with bilinear sampling at pixel centers, clamping source
  coordinates at the edges. Then grayscale using (0.299 R + 0.587 G + 0.114 B)/255.
  No rounding to integer grayscale. Input is contiguous CHW float32 in [0, 1].
- Training must implement the same interpolation and floating grayscale conversion
  (NumPy reference is appropriate). Avoid PIL's rounded grayscale conversion or
  antialiased downsampling when producing the final tensor.
- Conv2d(1, 8, kernel=3, stride=1, padding=1, bias=True), ReLU, MaxPool2d(2).
- Conv2d(8, 16, kernel=3, stride=1, padding=1, bias=True), ReLU, MaxPool2d(2).
- Flatten CHW (16*24*24 = 9216), Linear(9216, 1, bias=True), sigmoid.
- Use binary cross entropy with logits for training; sigmoid only for inference.
- A label 1 means the LOADING text is present; 0 means absent. Black screens,
  HUD/menu text, partial/different words and ordinary gameplay are negative.

## Exported binary

Little-endian struct `<8s6I`, followed by exactly 10465 float32 weights:

| Field | Value |
| --- | --- |
| Magic | ASCII `TWOCML1` followed by one zero byte (8 bytes) |
| Version | 1 |
| Width | 96 |
| Height | 96 |
| Conv1 channels | 8 |
| Conv2 channels | 16 |
| Weight count | 10465 |

Weights are contiguous PyTorch row-major arrays in the following order:

| Array | Float offset | Count |
| --- | --- | --- |
| conv1.weight [8,1,3,3] | 0 | 72 |
| conv1.bias [8] | 72 | 8 |
| conv2.weight [16,8,3,3] | 80 | 1152 |
| conv2.bias [16] | 1232 | 16 |
| classifier.weight [1,9216] | 1248 | 9216 |
| classifier.bias [1] | 10464 | 1 |

File length is exactly 41892 bytes. Reject extra/truncated bytes, invalid
dimensions/version, nonfinite weights, nonfinite inputs and inputs outside [0, 1].
Adjacent JSON metadata records training dataset provenance, split metrics, seed,
threshold recommendation and whether the model is synthetic/demo only. Metadata
is informational; the binary is the complete inference model.

## Native C ABI

Export undecorated cdecl names from `TwocLoadingVulkan.dll` on x86 and x64.
Return 0 on success, nonzero on error. Fill UTF-8 error text (zero terminated,
capacity includes terminator) on failure; never throw across the ABI.

```c
int TwmCreate(const wchar_t* model_path, const wchar_t* shader_directory,
             int enable_validation, void** handle, char* error, unsigned capacity);
int TwmPredict(void* handle, const float* pixels, unsigned pixel_count,
              float* confidence, char* error, unsigned capacity);
int TwmGetDeviceName(void* handle, char* name, unsigned capacity);
void TwmDestroy(void* handle);
```

Shaders: `conv1.spv`, `conv2.spv`, `classifier.spv` in shader_directory.
Three compute passes; each descriptor layout has three storage buffers: binding 0
input activation, binding 1 shared weights, binding 2 output activation/confidence.
No CPU neural-network inference fallback. Thread-safe calls per handle; destroy
after callers stop. Native runtime owns Vulkan resources and persistent buffers.

## Installed layout

```text
Components/
  LiveSplit.CrashTWoCLoadRemoval.dll
  x64/TwocLoadingVulkan.dll
  x86/TwocLoadingVulkan.dll
  ML/
    loading-text.twml
    loading-text.json
    shaders/{conv1,conv2,classifier}.spv
```

Vulkan loader/driver comes from the graphics driver. Legacy platforms must not
load this native dependency. Experimental mode reports backend/model failures
visibly and releases Game Time if the detector becomes unavailable.
