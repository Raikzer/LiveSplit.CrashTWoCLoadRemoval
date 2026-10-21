# 2.0 pre-release installation

Download `LiveSplit.CrashTWoCLoadRemoval-2.0.zip` from the GitHub release assets.
Close LiveSplit, then extract the ZIP into the folder containing `LiveSplit.exe`.
Merge its `Components` folder with the existing one, preserving the subfolders.
Restart LiveSplit and add **Control > Crash TWoC Load Removal** to the layout.

On **Setup**, configure the capture source and crop. For the neural detector,
select **ENG/PS2 experimental ML**. On **Experimental ML**, use
`ML\loading-text.twml` and threshold **0.500**, then test the capture. Existing
layouts retain their saved threshold; set it explicitly when upgrading. Use
Game Time and save the layout. The existing OCR platforms are also included.

The ZIP includes both x86 and x64 native runtimes, OCR language data, the trained
English loading-text model, compiled Vulkan shaders and app-local C++ runtime
dependencies. It installs into an existing LiveSplit Desktop installation; the
release was verified against LiveSplit 1.8.37 on Windows with .NET Framework 4.8.1.

**The Vulkan SDK and Python are not runtime requirements.** Experimental ML mode
needs a Vulkan-capable GPU and graphics driver. SDK/build tools are needed only
to compile the native runtime and shaders; Python is needed only for offline
training. The Vulkan loader is provided by the installed graphics driver.

The release model was trained from one reviewed English PS2 capture session.
Chronological held-out frames with gaps scored 99.44% accuracy (four false
positives and four misses on 1,422 frames). Performance on independent recordings
and other capture setups remains unverified. This is an experimental pre-release.
Source builds use the synthetic demonstration unless `-ModelPath` selects the
release model; the GitHub binary ZIP includes the trained real-session model.

For training capture, open **Training > Open capture and review**. The defaults
are 20,000 cropped frames at 30 FPS. **Y/N** label, **R** goes back, and **U** marks
the current plus next 99 frames absent. See [TRAINING-MODE.md](TRAINING-MODE.md).
