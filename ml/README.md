# Training the loading-text classifier

The model returns one floating confidence in `[0,1]` for the English **LOADING**
text. It does not recognize or return text. The native experimental runtime uses
Vulkan; Python/PyTorch here are offline training and verification tools.

Source builds default to `models/eng-ps2-hud-fix/loading-text.twml`, a custom
offline fine-tune on reviewed English PS2 captures at fixed threshold **0.800**.
Its [model card](models/eng-ps2-hud-fix/README.md) records training provenance,
HUD regression results and limited, reused holdouts. The test recordings contain
no positives, so positive test recall is unavailable. The generic `train.py`
instructions below do not reproduce these custom fine-tuned weights; the dataset
and custom run artifacts remain private.

The separate `artifacts/demo/loading-text.twml` remains **synthetic demo only**;
use `Build.ps1 -TrainDemo` to select it explicitly. Its metrics describe artificial
text on generated backgrounds and do not establish gameplay accuracy. A sigmoid
confidence is not a calibrated probability.

## Local setup (Windows, Python 3.10, CPU)

From this directory:

```powershell
.\setup.ps1 -Python C:\Python310\python.exe
.\.venv\Scripts\python.exe -m unittest -v test_pipeline test_prepare_captures
```

`setup.ps1` creates only `ml/.venv` and installs into it; activation and global
Python package changes are unnecessary. `requirements-lock.txt` pins the actual
CPU environment used for the demo. `requirements.txt` documents compatible
dependency ranges; setup prefers the lock. Reproducible rendering also needs the
same fonts; generator metadata records their names and SHA-256 hashes. Repeating
on different PyTorch versions, CPUs, or font files may change exact bytes.

## Collect and label the existing ROI

Save the existing **raw RGB loading capture ROI**, normally 100×100 pixels,
before thresholding. Do not supply a full screenshot or an image already
thresholded for OCR. The component's training capture window records the ROI to
a session folder, with a default target of 20,000 frames at 30 FPS (about 11 minutes
7 seconds). Stop to review, or wait
for capture to finish. In the review window, **Y** marks LOADING present, **N**
marks it absent, **R** moves back one frame, and **U** marks the current frame and
the next 99 frames absent. Review and correct labels before exporting. Progress
is saved in the session folder so it can be reopened and continued later.

**Export training CSV** writes `samples.csv` in that session folder with labeled
frames only. Unlabeled frames are excluded. Re-export after correcting labels.
The raw export can contain identical adjacent frames; prepare it before training
using the command below.

Label `1` only when the complete English LOADING word is present. Label `0` for
ordinary gameplay, HUD/menu text, black screens, partial words, similar words,
transitions, and other loading screens without that word. Collect representative
hard negatives and positive examples across backgrounds, resolutions, capture
settings, and the intended emulator/game setup. Include enough independent
recordings in each split, rather than many nearly identical adjacent frames.

Use a CSV with `path,label,group` columns. Paths are relative to the CSV, or
absolute. Each `group` names one recording/session. Keep adjacent frames,
recrops, and repeated variants of the same source recording in the same group:

```csv
path,label,group
session-a/loading/frame0001.png,1,session-a
session-a/not_loading/frame0002.png,0,session-a
session-b/loading/frame0001.png,1,session-b
```

## Prepare reviewed sessions

From this directory, merge the exported manifests into a new file:

```powershell
.\.venv\Scripts\python.exe prepare_captures.py C:\captures\session-a\samples.csv C:\captures\session-b\samples.csv C:\captures\session-c\samples.csv --output C:\captures\combined.csv
```

The output directory must already exist. The tool accepts exactly the three
`path,label,group` columns above, resolves each image relative to its source CSV
(or accepts an absolute path), and writes absolute image paths in the output.
Labels must be `0` or `1`, and groups must be nonempty. Empty exports, unreadable
images, and malformed rows fail preparation. Original images and label files
are never edited.

Images are processed one at a time with the exact training preprocessing in
`model.load_pixels`; the command does not accumulate a dataset of image tensors.
For identical model inputs with the same label, it keeps only the first row in
command/CSV order, including that row's recording group. This also catches
repeated paths and images re-encoded or resized to an identical model input.
Keeping a single group owner prevents those duplicate inputs from crossing
training splits. Near-duplicates still require correct recording groups.

`combined.report.json` records counts, every omitted duplicate and its retained
source, and label conflicts with all source frame paths. Set `--report PATH` to
choose another report path. If identical model inputs have opposite labels, the
command exits nonzero, writes the conflict report, and does not create or replace
the cleaned CSV. Review the source frames, correct labels in the review window,
re-export the affected sessions, then rerun preparation. If conflicting frames
truly have different labels, collect examples distinguishable within the capture
ROI; this input cannot represent both labels. An existing CSV from an earlier
run remains unchanged on conflict; use a newly successful preparation result.

Existing output or report files are refused unless `--force` is supplied
explicitly. Each file is published atomically. Source manifests and referenced
images are protected even with `--force`.

One recording can be prepared now and merged with more session exports later.
Default training requires **at least three independent recordings**, with
enough variety to put both classes in every split after deduplication. Keep all
adjacent frames from a recording together; never randomly split its frames or
rename one recording into multiple groups to meet this requirement. Do not
combine unrelated recordings under the same group name.

By default, deterministic group splits use
about 70%/15%/15% for train/validation/test. Both classes must occur in every
split; add sufficiently varied groups if that is impossible. To control splits,
add a `split` column containing `train`, `val`, or `test` on **every row**. A
recording cannot span splits. Assign explicit splits to the prepared CSV after
merging; the preparation command rejects a `split` column. The training loader
also rejects exact duplicate paths and equal preprocessed image content even
within one split, so train from the cleaned manifest.

### Explicit independent negative-only test

If only two independent recording groups contain positives, use the explicit
`--negative-only-test` protocol. Assign the first both-class recording to `train`,
the second both-class recording to `val`, and independent negative-only recordings
to `test`. Supply a `split` on every row of the cleaned manifest. At least three
original recording groups are required; a group cannot span splits, training and
validation must each contain both classes, and test must contain only negatives.
The flag cannot be combined with `--temporal-holdout`. Never rename portions of
one recording as independent groups. Default training still requires both classes
in every split.

For example, after preparing and assigning explicit splits in
`C:\captures\all-sessions\training-data.csv`, run:

```powershell
.\.venv\Scripts\python.exe train.py C:\captures\all-sessions\training-data.csv --output artifacts\all-sessions --epochs 30 --patience 5 --seed 1234 --negative-only-test --initial-model artifacts\previous\loading-text.twml
```

`--initial-model` is optional. It loads the exact version-1 `.twml` weights before
creating a new Adam optimizer; it does not resume optimizer state. Header, shape,
file length and finite weights are validated by `model.read_model`. The caller
must verify that **every recording used by the initial model belongs exclusively
to the current training split**, including recordings used for that model's
validation, test, or experiment selection. The binary cannot establish its own
data provenance. Use the previous run's metadata and source records to check
this; also remove duplicate model inputs before assigning the new splits.
Metadata records `training.initialization` and `training.initial_model_sha256`.

Checkpoint and threshold selection still use validation only. Independent test
recordings measure negative accuracy, specificity, false-positive rate, and
confusion counts. There is **no held-out test recall**: `recall`, `f1` and
`balanced_accuracy` are JSON `null`. Precision is `0` when false positives exist
and `null` when no samples are predicted positive. Validation recall is measured
on the same data used to select the model and threshold; it is not independent
test recall. Many adjacent positive frames from a single loading event provide
weak positive coverage, even when the frame count looks substantial. Collect
additional independent positive events and recordings before drawing conclusions
about missed loading screens.

`evaluation_protocol.mode` is
`independent_recording_holdout_negative_only_test`. Metadata records the actual
unique recording count, `test_class_coverage`, `validation_class_counts`, and the
explicit recall limitation. Negative parity fixtures come from test and positive
fixtures come from validation. Each fixture's `source_split` is recorded in
`parity-samples/expected.json` and `loading-text.json`'s `parity_samples`; parity
agreement establishes runtime numerical agreement and supplies no positive test
coverage.

### Explicit single-session temporal holdout

For exploratory training from exactly one real recording, explicitly opt into
`--temporal-holdout`. This uses three chronological portions of the same session.
There is **no independent recording evaluation**: even with gaps, nearby images
can be correlated, and these scores do not establish performance on other
recordings, capture settings, or future sessions. Collect independent recordings
for a meaningful assessment of generalization.

Create a separate manifest with `path,label,group,split,frame_index` columns.
Use the unchanged original session group on every row, absolute or relative image
paths, and the actual nonnegative integer frame indices from that recording.
Rows must be strictly increasing by frame index with no repeats. Explicit splits
must form contiguous train, then val, then test portions, each containing both
classes. Exclude at least 90 original frame indices between the final training
frame and first validation frame, and between the final validation frame and
first test frame. For example, after training frame 100, validation can start at
191: indices 101 through 190 are excluded. `--temporal-gap-frames` sets this
positive minimum, with 90 as the default. Keep gaps excluded even after removing
duplicate inputs. Exact duplicate paths and model inputs remain forbidden.

The normal preparation command accepts only `path,label,group`; construct the
temporal manifest separately using the original frame chronology after reviewing
labels and duplicates. Do not fabricate groups or renumber sampled frames to
hide their original temporal distances.

```powershell
.\.venv\Scripts\python.exe train.py C:\captures\session-a\temporal-samples.csv --output artifacts\real-temporal --epochs 30 --patience 5 --seed 1234 --temporal-holdout --temporal-gap-frames 90
```

The startup message and `loading-text.json` explicitly identify the limitation.
Metadata records `evaluation_protocol.mode` as
`single_session_temporal_holdout`, `independent_recordings` as 1, the configured
`gap_frames`, and the absence of independent recording evaluation. Dataset
provenance retains `frame_index`; all split summaries retain the same original
group. Default training records the `independent_recording_holdout` protocol.
Validation still selects the checkpoint and threshold; test data remains held
out from both decisions.

## Train and evaluate

```powershell
.\.venv\Scripts\python.exe train.py C:\captures\combined.csv --output artifacts\real --epochs 30 --patience 5 --seed 1234
```

Training stays on the CPU, fixes Python/NumPy/PyTorch seeds, and enables
deterministic PyTorch algorithms. Augmentation runs only on training batches:
small translations, brightness variation, and noise. A training-only positive
class weight handles class imbalance. Validation loss selects `best.pt`; early
stopping limits epochs without improvement. The threshold maximizes validation
balanced accuracy, with ties nearest 0.5. The test split is evaluated after
selection, without influencing weights or threshold.

The output contains:

- `loading-text.twml`: the strict 41,892-byte version-1 binary model.
- `loading-text.json`: sample/tensor hashes, group membership, seeds, environment,
  generator provenance if present, threshold and confusion metrics on each split.
- `best.pt` and `history.json`: local checkpoint and training/validation losses.
- `parity.npz`: held-out CHW float32 tensors and CPU reference confidences;
  negative-only test runs include validation positives as described above.
- `parity-samples/`: corresponding raw PNGs, `.npy` input tensors and expected JSON.

Use a fresh output directory for each experiment. Keep the held-out recordings
fixed; repeatedly selecting experiments based on test results makes those results
optimistic. Check false positives and negatives on real recording sessions before
considering a real model suitable for timing.

## Reproduce the synthetic demonstration

```powershell
.\.venv\Scripts\python.exe generate_demo.py datasets\demo --groups 64 --per-group 12 --seed 86420
.\.venv\Scripts\python.exe train.py datasets\demo\samples.csv --output artifacts\demo --epochs 40 --patience 5 --seed 1234 --synthetic-demo
.\.venv\Scripts\python.exe predict.py artifacts\demo\loading-text.twml artifacts\demo\parity-samples\0.png
```

The generator requires an absent or empty output directory. Each of the 64
groups has a separate seed. Samples vary backgrounds, noise, font, placement and
brightness; negatives include partial/different words, blank backgrounds and a
black screen. These independent generator groups exercise the split machinery;
they are not a substitute for separate gameplay recordings. Synthetic generator
data requires the explicit `--synthetic-demo` training flag.

The included run trained on 528 images from 44 groups and held out 120 images
from 10 groups each for validation and test. At the validation-selected threshold
**0.52**, test accuracy/balanced accuracy was **90.83%**, precision **96.23%**,
recall **85.00%** (TP 51, TN 58, FP 2, FN 9). The best checkpoint was epoch 39
of the bounded 40-epoch run. These synthetic results do not establish gameplay
accuracy. Two identical initial 15-epoch runs also produced identical model bytes.
The final model's three reference tensors also passed native Vulkan comparison
on an AMD Radeon RX 6800 with validation enabled; maximum absolute confidence
error was `8.642673492431641e-7`.

## Verify runtime parity

`predict.py model.twml roi.png` prints exactly one float on stdout; errors go to
stderr with a nonzero status. Its independent NumPy implementation reads the
exported binary and checks the contract. This is a developer reference, not a
CPU fallback in the component.

`model.preprocess_rgb` matches the runtime: pixel-center bilinear RGB sampling
with edge clamp, double-precision intermediates, floating luminance, and one final
float32 conversion. It does not use Pillow grayscale conversion or antialiasing.
See [MODEL-FORMAT.md](MODEL-FORMAT.md) for the architecture and binary layout.

After building the native DLL and shaders with the same Python/DLL bitness:

```powershell
.\.venv\Scripts\python.exe validate_native.py ..\native\build\x64\TwocLoadingVulkan.dll artifacts\real-temporal\loading-text.twml ..\native\build\shaders --parity artifacts\real-temporal\parity.npz --validation
```

Supply the actual DLL, compiled shader directory, and training output directory
if your layout differs; always pass the matching output's `parity.npz` with
`--parity` (for example, `artifacts\real\parity.npz` for the default real run).
The script calls the native ABI, compares held-out tensor confidences within an
absolute tolerance of `0.0002`, and fails if Vulkan initialization or prediction
fails. The `.png`/`.npy` pairs can separately verify managed ROI preprocessing.
Generated datasets, virtual environments and training checkpoints are ignored by
Git. The reviewed-capture model includes only its binary, compact public metadata
and model card; raw captures and private run artifacts are excluded. The separate
demo also includes its metadata, history and parity fixtures.
