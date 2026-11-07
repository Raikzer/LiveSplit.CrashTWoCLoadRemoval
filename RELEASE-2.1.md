# Updated 2.1 pre-release

The 2.1 pre-release was updated on 2026-11-07 with Windows Graphics Capture,
editable capture controls and the Ghost Town model. Its tag remains `v2.1` and
assembly version remains 2.1.0.0. Download the replacement
`LiveSplit.CrashTWoCLoadRemoval-2.1.zip` from the
[2.1 release](https://github.com/Raikzer/LiveSplit.CrashTWoCLoadRemoval/releases/tag/v2.1).

## Install or upgrade

Close LiveSplit, then extract the ZIP into the folder containing `LiveSplit.exe`.
Merge **all files in its `Components` folder**, preserving every subfolder, with
your existing `Components` folder. Restart LiveSplit and add
**Control > Crash TWoC Load Removal** to the layout if needed. The host executable,
layouts and training sessions are not included or replaced by this package.

On **Setup**, choose the source and capture boundaries, then select
**ENG/PS2 experimental ML** for the neural detector. On **Experimental ML**, use
`ML\loading-text.twml`, set the threshold to **0.800** and reload/test the model.
Existing layouts retain their saved model paths and thresholds; change those
explicitly to use the replacement model. Use Game Time and save the layout.
The existing OCR platforms remain available.

## Updated controls and capture

- **Use Windows Graphics Capture** selects the Windows capture API for the chosen
  window or monitor, including detection, previews, saved samples and training.
  It defaults off and is saved with the layout. Scaling is automatic when enabled.
- **Disable load remover** stops automatic detection, splitting and calibration,
  releases the Game Time pause and preserves timer phase and existing load counts.
- **Blacklevel** is editable from 0 to 255 and stays synchronized with calibration.
- Four boundary controls set top-left X/Y and bottom-right X/Y in the existing
  **400 × 160 preview**. Right/bottom are exclusive. Numeric edits, mouse selection
  and layout XML stay synchronized; empty or crossed regions are rejected.

Pending or failed capture releases the load-removal pause and reports capture
status without discarding a valid ML recognizer or inventing a black frame.
Training skips pending frames and preserves a partial session on capture failure.
See [WINDOWS-GRAPHICS-CAPTURE.md](WINDOWS-GRAPHICS-CAPTURE.md) for details.

## Runtime requirements

The ZIP contains the .NET Framework 4.8.1 component, x86/x64 Windows Graphics
Capture and Vulkan libraries, compiled shaders, the model, OCR language data and
app-local C++ runtime dependencies. The target host is LiveSplit Desktop 1.8.37 on
a Windows version supported by **.NET Framework 4.8.1**.

**Python, the Windows SDK, Vulkan SDK and build tools are not runtime requirements.**
Experimental ML needs a Vulkan-capable GPU and its graphics driver. Windows
Graphics Capture uses Direct3D 11 and requires Windows 10 version 1903 or later,
in addition to the component's framework requirements. Legacy OCR platforms do
not initialize Vulkan. The normal Windows capture border is retained.

## Model and validation

The bundled [Ghost Town model](ml/models/eng-ps2-ghost-town/README.md) adds one
user-confirmed screenshot crop as a training negative to the HUD-fix model.
The inferred crop already scored below threshold before retraining, so this does
not establish a fix for the reported pause. Validation has 21 detections and zero
false positives; the remaining 14,316-frame test has zero false positives and
**no positive frames**. Both holdouts were reused. These are not fresh benchmark
results, and there is no independent positive test recall. See the model card
for full counts, ancestry and limitations.

The current build passed x86/x64 native and managed checks on an AMD Radeon RX
6800. The full build includes 71 ML checks, nine cleanup checks, 82 training checks,
81 settings checks per architecture, plus 99 x64/97 x86 capture checks. Three
keyboard-focus sections were skipped per architecture because Windows denied
foreground activation; direct review-command checks passed. Live capture passed
47 checks per architecture using only synthetic test windows. Monitor output,
mixed-DPI transitions, device loss and unsupported Windows versions were not
validated live. This remains an experimental pre-release.

For training capture, open **Training > Open capture and review**. The defaults
are 20,000 cropped frames at 30 FPS. **Y/N** label, **R** goes back, and **U** marks
the current plus next 99 frames absent. See [TRAINING-MODE.md](TRAINING-MODE.md).
