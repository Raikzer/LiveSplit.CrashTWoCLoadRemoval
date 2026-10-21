# Native Vulkan loading-text inference

`TwocLoadingVulkan.dll` implements the C ABI and fixed CNN in
[`../ml/MODEL-FORMAT.md`](../ml/MODEL-FORMAT.md). The DLL performs every neural
network layer on a hardware Vulkan compute device. CPU inference exists only in
the test executable as an independent numerical reference.

## Build and test

Requirements: Windows, Visual Studio 2022 C++ build tools with the v143 toolset
and a Windows SDK, and a Vulkan SDK containing both `Lib` and `Lib32`, `glslc`
and `spirv-val`. The script discovers MSBuild using `vswhere` and the SDK using
`VULKAN_SDK` or `C:\VulkanSDK`. It does not install or modify system software.

From the repository root:

```powershell
.\native\build.ps1 -Test -Validation
```

Explicit tool paths and a single architecture are supported:

```powershell
.\native\build.ps1 -VulkanSdk C:\VulkanSDK\1.3.216.0 `
    -MSBuild 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
    -Architecture x64 -Test -Validation
```

Outputs:

```text
native/build/x64/TwocLoadingVulkan.dll
native/build/x86/TwocLoadingVulkan.dll
native/build/shaders/conv1.spv
native/build/shaders/conv2.spv
native/build/shaders/classifier.spv
native/build/{x64,x86}/NativeTests.exe
```

The runtime uses a statically linked C++ runtime. Deployment needs the matching
DLL architecture, those three shaders, a model, and the graphics driver's Vulkan
loader/ICD. It needs neither the Vulkan SDK nor the validation layer unless
validation is explicitly enabled. Vulkan 1.0 is used without device or instance
extensions or optional device features. CPU Vulkan devices are excluded.

`-Validation` requests `VK_LAYER_KHRONOS_validation` and fails if it is absent.
Enable synchronization validation for an additional test run:

```powershell
$env:VK_LAYER_ENABLES = 'VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT'
.\native\build\x64\NativeTests.exe .\native\build\shaders --validation
.\native\build\x86\NativeTests.exe .\native\build\shaders --validation
```

The layer's default logger emits validation diagnostics to stdout. A local
`vk_layer_settings.txt`/Vulkan Configurator setup can override that destination.
The test process reports its numerical/API failures through a nonzero exit code;
inspect validation output as well. No debugging extension is required.

To compare an exported model against the native tests' CPU reference:

```powershell
.\native\build\x64\NativeTests.exe .\native\build\shaders --validation `
    --model .\ml\artifacts\demo\loading-text.twml
```

Tests cover all header fields, extra/truncated data, NaN/infinite model weights,
invalid/null inputs, UTF-16 model paths, undecorated exports, bounded C error
buffers, missing shaders/partial initialization cleanup, four input patterns,
concurrent calls on one handle, repeated submissions, and CPU/GPU parity with an
absolute tolerance of `2e-5`. Temporary test models are created beside the test
executable and removed when the test exits.

## Runtime behavior

Each stage has one descriptor set with exactly three storage buffer bindings:
input, shared weights, output. The two convolution shaders fuse convolution,
ReLU and max pooling while preserving CHW ordering and zero padding. The final
shader computes a dense logit and stable sigmoid. Five persistent buffers are
mapped once. Every allocation checks `memoryTypeBits` for a host-visible,
host-coherent type, excluding AMD memory flags requiring disabled extensions.
Explicit barriers cover host upload, each compute write/read dependency, and
the final compute write/host read. A fence completes before confidence is read.

`TwmCreate` validates the complete model before creating Vulkan resources and
cleans up partial initialization on failure. Device selection prefers a discrete
GPU, then an integrated GPU, and requires the limits needed by the three passes.
Model and shader paths use UTF-16 on Windows; status text is bounded UTF-8.
All predictions and device-name calls on a handle are serialized. Stop callers
before `TwmDestroy`; a destroyed or foreign handle is invalid.

GPU submissions have a one-second fence timeout, allowing more than a thousand
times the measured normal inference duration for GPU scheduling. A submission/wait/reset failure
makes that handle unavailable to subsequent predictions. Destruction still waits
for the device to become idle before freeing in-flight resources; a hung driver
can therefore delay destruction. Callers needing prompt failure handling should
perform destruction asynchronously and retain the loaded DLL and its destroy
function until destruction completes. A timed-out context retains all resources
until that cleanup; subsequent predictions fail without resubmitting work.
Errors return nonzero with a zero-terminated
message when an error buffer is supplied. There is no CPU ML fallback.
