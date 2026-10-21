# Capture and review training frames

1. Open LiveSplit's layout settings for Crash TWoC Load Removal.
2. On **Setup**, select your game/capture window and crop the game area. Select
   **ENG/PS2** or **ENG/PS2 experimental ML**. A trained model is not required to
   collect images.
3. On **Training**, click **Open capture and review**.
4. Leave **Frames** at **20,000** and **FPS** at **30**, then click **Start capture**.
   The requested duration is about **11 minutes 7 seconds**. Capture or disk delays
   may extend it; the window reports saved frames and actual progress.
5. Play with the capture source open. You can minimize the training window.
   Source/crop settings are held fixed for this recording. Click **Stop and
   review** to end early, or wait for the target to be saved.

The capture uses the same raw English **100x100 loading-text ROI** as inference.
It saves PNGs on disk using a bounded writer queue, rather than keeping all images
in memory. A slow writer delays sampling instead of allocating an unlimited queue.
Frames are not generated to fill gaps. Closing the training/settings window stops
capture and flushes queued frames. Interrupted/partial recordings remain reviewable.

## Review shortcuts

The keys act only while the review window has focus and capture has stopped.
They are inactive in input fields and while dialogs are open. Held-key repeats
are ignored to avoid labeling many frames accidentally.

| Key | Action |
| --- | --- |
| **Y** | Current frame shows LOADING; save label 1 and advance one frame. |
| **N** | Current frame does not show LOADING; save label 0 and advance one frame. |
| **R** | Go back one frame, keeping the saved label so it can be corrected. |
| **U** | Current frame and following 99 show no LOADING; save 100 absent labels and advance 100. |

U stops at the final frame when fewer than 100 remain. At the end, R returns to
the last frame. Label changes and review position autosave immediately. Review
does not delete images, and revisiting a labeled frame lets you overwrite its
label deliberately. The image and current label appear above the shortcut buttons.

Sessions live under **Components/ML/training/session-...**. Click **Open session**
and select a session folder to resume after closing LiveSplit. A completed PNG
survives interruption; incomplete temporary files are not counted as frames.
Unreadable/corrupt sessions are reported rather than silently skipping images.

## Export and train

Click **Export training CSV** to create the session's `samples.csv`. It includes
only labeled frames, with relative paths and one recording group per session.
Capture at least three independent recordings for separate training, validation
and test groups. Adjacent frames within one recording stay in the same group.

Repeated/static frames are expected. Prepare the reviewed CSVs using the bundled
Python toolchain before training:

```powershell
.\ml\setup.ps1
.\ml\.venv\Scripts\python.exe .\ml\prepare_captures.py C:\captures\session-a\samples.csv C:\captures\session-b\samples.csv C:\captures\session-c\samples.csv --output combined.csv
.\ml\.venv\Scripts\python.exe .\ml\train.py combined.csv --output .\ml\artifacts\real --epochs 30 --patience 5
```

Preparation removes identical model inputs with matching labels, retaining one
recording owner to avoid leakage across splits. If identical images have opposite
labels, it writes a conflict report and leaves the prepared manifest unwritten;
correct the labels in review and export again. Near-duplicate images are not
automatically detected. Original images and review labels are unchanged.

See [ml/README.md](ml/README.md) for dependencies, model export, metrics and Vulkan
verification. The bundled synthetic model's weights do not change during capture
or review; training is a separate explicit step.
