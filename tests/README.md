# Managed ML integration verification

From the repository root, after building the component and native runtime:

```powershell
.\tests\build.ps1 -LiveSplitDirectory ..\LiveSplit\bin\Release
```

Requires the Visual Studio C# compiler, C++ x86/x64 tools, .NET Framework,
packaged OCR dependencies, and a Vulkan-capable GPU/driver. The compiler is
discovered with `vswhere`, or can be supplied with `-Compiler`. `-OutputDirectory`
defaults to the sibling `ml-verification` directory. No upstream host tests run.

The script creates isolated x64/x86 host copies, installs the final demo model
and available native build outputs, builds the harness for each architecture,
and runs real Vulkan parity checks. It then builds an explicitly fake native
runtime in separate `fault-x64`/`fault-x86` copies to test inference failure and
two-second destruction. Never distribute those fault-runtime directories.

The harness uses the actual component/settings constructors with preview capture
disabled. It injects synthetic bitmaps into the PS2 processing seam and uses an
invalid process selection for the production `Update` failure path. Settings
rendering uses `DrawToBitmap` in an offscreen test form, not desktop capture.

Checks cover exact platform labels and retained OCR choices, lazy Vulkan loading,
XML path/threshold round trips under German culture, English capture resize,
OCR/ML switching and disposal, preprocessing/GPU parity, malformed/missing assets,
cached errors and reload recovery, positive/negative PS2 timer transitions,
calibration without OCR, invalid capture, and prompt game-time recovery while
native cleanup blocks. Confidence parity tolerance is `1e-4`; tensor tolerance
is `1e-7`.

Logs are `managed-x64.log`, `managed-x86.log`, `blocking-x64.log`, and
`blocking-x86.log` under the output directory. Static setup/ML settings images
are written inside each architecture directory. The executable also accepts
`--render-settings` to regenerate only those images.

Limitations: no real gameplay, capture/crop accuracy, graphics-driver hang, or
non-English OCR recognition accuracy is established by these tests. The fake
native inference exception is passed to the actual component failure handler at
the recognition seam; the production outer catch is independently tested through
an invalid capture. Runtime asset mutation happens only in scratch copies.

## Training capture and review verification

After building the managed component, run:

```powershell
.\tests\training.ps1 -LiveSplitDirectory ..\LiveSplit\bin\Release
```

This separate harness requires .NET Framework and the Visual Studio C# compiler;
it does not require a GPU, OCR initialization, or a running game. It builds x64
and x86 executables in the sibling `training-verification` directory and uses
only synthetic 100 x 100 frames. Its WinForms message loop uses offscreen forms
and direct `ProcessCmdKey` calls, with no input injection or desktop capture.
Run it without concurrent UI test harnesses: its offscreen review form briefly
needs keyboard focus to exercise the production foreground-window guard.
If Windows denies foreground activation, focused keyboard sections explicitly
log `SKIP`, while direct review-command checks still run. Use
`-RequireKeyboardFocus` to make unavailable foreground focus a test failure.

Checks cover the 20,000-frame/30-FPS defaults, durable labels and cursor, Y/N/R/U
keyboard routing and held-key suppression, editable-control and inactive-window
focus guards, bounded writer ownership, stop/close/dispose flushing, automatic
completion, capture/write failures, journal recovery and stale-writer rejection,
PNG validation, labeled-only CSV export, long paths, and the component's training
recognition guard and transition reset. Review, completed-capture, and Training
settings renders are saved as PNGs beside each executable. Logs are
`training-x64.log` and `training-x86.log`.

Folder picker and message-box interaction, real gameplay, actual desktop crop
accuracy, and sustained real-time 30-FPS delivery are outside this harness. The
form's capture timer is driven deterministically by synthetic ticks; it verifies
the fractional 30-FPS deadline without relying on machine timing. Session files
are retained under each runtime's `data` directory for inspection.
