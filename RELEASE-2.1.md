# 2.1 pre-release installation

Download `LiveSplit.CrashTWoCLoadRemoval-2.1.zip` from the GitHub release assets.
Close LiveSplit, then extract the ZIP into the folder containing `LiveSplit.exe`.
Merge **all files in its `Components` folder**, preserving every subfolder, with
your existing `Components` folder. Restart LiveSplit and add
**Control > Crash TWoC Load Removal** to the layout.

On **Setup**, configure the capture source and crop, then select
**ENG/PS2 experimental ML** for the neural detector. On **Experimental ML**, use
`ML\loading-text.twml` and threshold **0.800**, then test the capture. Existing
layouts retain their saved threshold; set 0.800 explicitly when upgrading. Use
Game Time and save the layout. The existing OCR platforms are also included.

The ZIP includes the managed .NET Framework 4.8.1 component, x86 and x64 native
runtimes, compiled Vulkan shaders, the reviewed-capture English PS2 model, OCR
language data and app-local C++ runtime dependencies. It installs into an existing
LiveSplit Desktop installation; the target host is LiveSplit 1.8.37 on Windows
with **.NET Framework 4.8.1**.

**Python, the Vulkan SDK and build tools are not runtime requirements.**
Experimental ML needs a Vulkan-capable GPU and an installed graphics driver with
Vulkan support; the driver supplies the Vulkan loader. No separate SDK is needed
to use the ZIP. Legacy OCR platforms do not initialize Vulkan.

2.1 bundles the HUD-fix model in source builds as well as the ZIP, and changes the
default ML threshold to 0.800. Three designated HUD false positives now score
0.106, 0.189 and 0.122, below the fixed threshold. The complete 6,267-frame HUD
recording has zero false positives, but it was moved into training: these are
training regression results. Validation has 21 detections and no false positives;
the remaining 14,316-frame test has no false positives and **no positive frames**.
Both holdouts were reused after earlier model selection. This is not a fresh
benchmark, and there is no independent positive test recall. See the
[model card](ml/models/eng-ps2-hud-fix/README.md) for full counts, lineage and limits.

The production Vulkan wrapper passed x86 and x64 checks on an AMD Radeon RX 6800.
Across 33,382 verification inputs the maximum absolute confidence difference from
the CPU reference was `8.698552846908569e-7`. Numerical agreement does not establish
accuracy on new recordings or capture setups. This remains an experimental
pre-release.

For training capture, open **Training > Open capture and review**. The defaults
are 20,000 cropped frames at 30 FPS. **Y/N** label, **R** goes back, and **U** marks
the current plus next 99 frames absent. See [TRAINING-MODE.md](TRAINING-MODE.md).
