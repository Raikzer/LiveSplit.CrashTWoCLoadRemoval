# English PS2 HUD-fix model for 2.1

This experimental model scores the complete English **LOADING** word in the raw
RGB capture ROI. It is the default for source builds and the 2.1 pre-release.
Use threshold **0.800**. A sigmoid confidence is not a calibrated probability;
check your capture setup before using it for timing.

`loading-text.twml` is a 41,892-byte version-1 model using the architecture and
preprocessing in [MODEL-FORMAT.md](../../MODEL-FORMAT.md). Its SHA-256 is:

```text
2c167317cf777da5f23dd9a5a659279466a3394650c450d58a96f902e546994d
```

The adjacent `loading-text.json` is compact public metadata. Raw captures,
per-frame manifests, absolute source paths and private run artifacts are excluded.
The separate synthetic demonstration remains in `ml/artifacts/demo` and can be
selected explicitly with `Build.ps1 -TrainDemo`.

## Provenance and training

The model was custom fine-tuned offline from the all-session model with SHA-256
`28698bc1a0d597426ed143783220aa46fd5ceb37f0d80d7ed51bec411fe7b50c`.
That model's earlier ancestor was
`e43bcd738938511ffd27a45333e3b1f024aca62659ad8541172cde3aeac99e43`.
The initial model used current TRAIN recordings for gradients and the same
reused VAL recordings for checkpoint selection.

Both convolution layers remained bitwise equal to initialization. Only the final
classifier was updated with Adam, learning rate `0.0001`, normal batch size 64,
and four additional hard-negative replay samples per batch, drawn in seeded
shuffled cycles from three designated HUD negatives. Normal TRAIN inputs were
shuffled without replacement each epoch. TRAIN augmentation used translation
within +/-2.88 pixels, gain 0.8–1.2 and Gaussian noise sigma 0.012. The positive
class weight came from the normal TRAIN class ratio, excluding replay duplication.

A logit anchor with coefficient `0.1` penalized squared differences from the
initial classifier on the same augmented frozen features. It applied only to
normal samples from the original 11 TRAIN recordings, excluding all samples from
the migrated HUD recording and all replay entries. No VAL/TEST teacher targets
were used. The seed was 1234; six epochs completed from a maximum of eight, with
patience three. Epoch three was selected by lowest unweighted VAL BCE among
eligible trained checkpoints. Eligibility required all three designated TRAIN
negatives below 0.2 and VAL TP 21, FN 0, FP 0 at the user-fixed 0.8 threshold.
There was no threshold search.

This was a **custom offline fine-tune**. The repository's generic `train.py` does
not reproduce these exact weights or this classifier/replay/anchor procedure.
The reviewed dataset and custom run artifacts remain private, so this is not a
publicly reproducible training run. Metadata preserves model ancestry and the
custom retrainer's hash for provenance without publishing capture images.

## Recording split and measured results

Seventeen recordings yielded 101,142 unique model inputs after global exact-input
deduplication. The entire 6,267-input HUD recording was moved from the previous
TEST split into TRAIN after false-positive inspection; its results below are
training regression checks.

| Split | Recordings | Positive | Negative | TP | TN | FP | FN |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| TRAIN | 12 | 1,196 | 72,831 | 1,194 | 72,733 | 98 | 2 |
| VAL | 3 | 21 | 12,778 | 21 | 12,778 | 0 | 0 |
| Remaining TEST | 2 | 0 | 14,316 | 0 | 14,316 | 0 | 0 |

All counts use threshold 0.8. The 21 VAL positives are adjacent frames from **one
contiguous loading event**. Remaining TEST has no positives: positive test recall,
F1 and balanced accuracy are unavailable (`null`), even though negative accuracy
and specificity are 1.0.

The three designated HUD scores changed from approximately 0.941226, 0.952410 and
0.898741 to Vulkan confidences 0.105544373, 0.188555747 and 0.121967480. All are
below the 0.2 regression margin. The full migrated HUD recording has zero false
positives out of 6,267 inputs, versus three before fine-tuning. These inputs were
used in training and do not demonstrate held-out generalization.

Full TRAIN still has 98 nominal false positives and two false negatives. Five
sampled label-0 false positives visibly contain LOADING despite their labels;
that inspection does not establish that all other nominal false positives are
mislabeled. One remaining gameplay false positive was observed. The two false
negatives contain faint loading text. The recorded confusion counts retain the
original labels rather than silently correcting them.

## Evaluation limits and runtime verification

VAL and remaining TEST were already evaluated during prior model selection and
are reused. Two earlier validation-guided trials were rejected: full-network
updates regressed VAL detections, and classifier updates with two replay samples
missed the strict HUD confidence margin. Neither rejected trial evaluated TEST.
The selected checkpoint was frozen before this run's remaining TEST evaluation;
TEST was not used for checkpoint or threshold selection in this run. This history
still means the reported results are **not a fresh benchmark**.

The production Vulkan wrapper passed x86 and x64 checks on an AMD Radeon RX 6800.
Verification covered 33,382 inputs (the HUD training regression, VAL and remaining
TEST) with Vulkan validation requested. Maximum absolute confidence difference
from the CPU reference was `8.698552846908569e-7`. This verifies numerical
agreement, not accuracy on new recordings. New positive events, independent
recordings, capture settings and platforms remain unverified.
