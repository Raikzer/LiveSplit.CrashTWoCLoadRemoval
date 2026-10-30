# Build the experimental Vulkan detector

The platform selector includes **ENG/PS2 experimental ML**. It reuses the PS2
black-screen/transition timing logic and replaces its loading-text OCR with a
small convolutional neural network. The prediction is a single float in `[0,1]`.
All neural-network layers run in three Vulkan compute shader dispatches. Python
and the CPU reference are offline development tools, not a runtime fallback.

## Build and run

Prerequisites: Windows, Visual Studio 2022 C++ build tools (v143 and Windows SDK),
Vulkan SDK (`VULKAN_SDK`), .NET 8 SDK, and a LiveSplit 1.8.37 source checkout with
recursive submodules. The build script restores framework targeting assemblies
and the component's Tesseract dependency locally. GPU drivers must provide Vulkan
1.0 compute; inference needs neither the Vulkan SDK nor Python after packaging.

For a fresh source checkout next to this component:

```powershell
git clone --branch 1.8.37 --recursive https://github.com/LiveSplit/LiveSplit.git ..\LiveSplit
.\Build.ps1 -BuildHost
```

The default model is the included reviewed-capture HUD-fix model at
`ml/models/eng-ps2-hud-fix/loading-text.twml`, with threshold **0.800**. Its
[model card](ml/models/eng-ps2-hud-fix/README.md) describes the limited evaluation.
`Build.ps1` compiles and
validates the SPIR-V shaders, builds native DLLs for x64 and x86, and builds the
managed component with model/runtime files in `../LiveSplit/bin/Release/Components`.
Pass `-LiveSplitRoot`, `-PackagesRoot`, `-MSBuild`, or `-Dotnet` for other locations.
Pass `-ModelPath C:\models\loading-text.twml` to install your own exported model.
An adjacent `.json` metadata file is copied when present.

```powershell
.\Build.ps1 -Test
.\native\build.ps1 -Test -Validation
```

The managed tests use an isolated copy of the runtime, synthetic image fixtures,
and preview-disabled constructors. They do not capture your desktop.

## Train or replace the model

The full pipeline, locked Python environment, group-safe splits, export format,
and measured demo results are documented in [ml/README.md](ml/README.md).

```powershell
.\Build.ps1 -TrainDemo # explicitly train and install the synthetic demo instead
# Or train on a CSV of actual captures:
.\ml\setup.ps1
.\ml\.venv\Scripts\python.exe .\ml\train.py C:\captures\samples.csv --output .\ml\artifacts\real --epochs 30 --patience 5
.\Build.ps1 -ModelPath .\ml\artifacts\real\loading-text.twml
```

In LiveSplit, select the experimental platform on Setup and use the Experimental
ML tab to pick a model, set its recommended threshold, reload it, or test the
current capture. A prediction is made during the PS2 search window after a black
transition, during blacklevel calibration, or when Test current capture is clicked.
The confidence label shows the latest prediction; it does not poll continuously.

Save LOADING/non-loading buttons collect the same raw capture ROI used at runtime
and append `Components/ML/samples/samples.csv`. Use distinct session names for
separate recordings. Correct labels and capture grouping must be checked manually.

For larger datasets, the **Training** tab provides a 20,000-frame, 30-FPS recording
mode and resumable Y/N/R/U review. See [TRAINING-MODE.md](TRAINING-MODE.md).

The optional synthetic demo remains in `ml/artifacts/demo`; its threshold is
0.52 and its synthetic accuracy does **not** establish gameplay reliability.
`-TrainDemo` selects the demo unless `-ModelPath` explicitly selects another
model. The bundled real model uses a fixed 0.800 threshold, reused validation
recordings and negative-only test recordings; it has no independent positive
test recall. Check representative recordings before relying on ML for timing.

Missing models, shaders, native DLLs, Vulkan devices and prediction failures show
an error on the ML tab. The detector releases its Game Time pause and stops using
the failed backend until Reload model or a platform/path change. GPU cleanup runs
off the timer thread and retains the DLL until pending work can be released.
Legacy platforms do not load the Vulkan runtime.
