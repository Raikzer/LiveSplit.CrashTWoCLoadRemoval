# English PS2 Ghost Town model

This is the **updated 2.1 pre-release and source-build default**, with fixed
threshold **0.800**, replacing the earlier [HUD-fix model](../eng-ps2-hud-fix/README.md)
in the 2.1 ZIP. The assembly version remains 2.1.0.0; the existing pre-release is
updated in place. The model scores complete English **LOADING** text in the
capture ROI; confidence is not a calibrated probability.

`loading-text.twml` is a 41,892-byte version-1 model using the architecture and
preprocessing in [MODEL-FORMAT.md](../../MODEL-FORMAT.md). SHA-256:

```text
dad00c11aa5e642e6b3af8ac369f0bb66f6e27806fce7a4f0d933f78b1266574
```

## Added negative and diagnostic limit

One user-confirmed Ghost Town screenshot was added to TRAIN as label 0. The
985 × 743 source image was assumed to show the full gameplay viewport. Its
inferred ROI was `(x=285, y=16, width=550, height=550)`, resized to 100 × 100 with
the runtime's Windows GDI+ HighQualityBicubic and TileFlipXY edge handling, then
the existing 96 × 96 floating-input preprocessing. The original image and derived
ROI are private; their SHA-256 hashes identify the inputs:

- Source: `b0e5068a68ae1f838eeea8a188354acf2000505559f7e0c6a20c7e47181854d1`
- Derived ROI: `ad37515d918b45192957d5dd68195e171b68501c243d23eee168150cf50046bc`

**The reported false positive was not reproduced under this inferred crop.**
Before retraining, the crop already scored approximately **0.00002329317**;
the new native Vulkan score is **0.000029806415**. Both are far below 0.8.
The screenshot is an added confirmed negative, not evidence that the observed
pause was fixed. A different configured crop or an earlier frame whose pause
remained latched could explain the difference; these possibilities remain
unverified.

## Training and ancestry

This custom offline fine-tune initialized from the HUD-fix model with SHA-256
`2c167317cf777da5f23dd9a5a659279466a3394650c450d58a96f902e546994d`.
Earlier ancestors are recorded in the adjacent compact metadata. The generic
`train.py` does not reproduce this classifier/replay/anchor procedure or these
exact weights. The reviewed dataset and custom run artifacts remain private, so
the training run is not publicly reproducible.

Convolution weights remained bitwise equal to initialization. Only the classifier
was updated with Adam at learning rate 0.0001, normal batch size 64 plus four
replay samples per batch drawn from three historical HUD negatives and the new
ROI. Normal TRAIN samples were shuffled without replacement each epoch; replay
used seeded shuffled cycles. TRAIN-only augmentation used translation within
+/-2.88 pixels, gain 0.8–1.2 and Gaussian noise sigma 0.012. Class-weighted BCE
used the normal TRAIN negative/positive ratio, excluding replay duplication.

The initial HUD-fix classifier was the frozen teacher for a logit MSE anchor with
coefficient 0.1 on the same augmented frozen features. The anchor covered only
67,760 normal samples from the original 11 TRAIN recording groups, excluding
the migrated 6,267-input HUD recording, the new screenshot group and all replay.
No VAL or TEST teacher targets were used. Seed 1234 and six CPU threads were
used. Three epochs completed from a maximum of six with patience two; epoch one
was selected by lowest unweighted VAL BCE among eligible trained checkpoints.
Eligibility required all four designated TRAIN negatives below 0.2 and VAL
TP 21, FN 0, FP 0 at the fixed 0.8 threshold; there was no threshold search.

## Counts and results

The dataset contains **17 recordings plus one user-image source group**: 18
source groups and 101,143 globally deduplicated model inputs. The screenshot is
not an eighteenth independent recording or a holdout. All results use 0.8.

| Split | Source groups | Positive | Negative | TP | TN | FP | FN |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| TRAIN | 13 | 1,196 | 72,832 | 1,194 | 72,732 | 100 | 2 |
| VAL | 3 | 21 | 12,778 | 21 | 12,778 | 0 | 0 |
| Remaining TEST | 2 | 0 | 14,316 | 0 | 14,316 | 0 | 0 |

The three historical HUD negatives now score approximately 0.039588720,
0.081370533 and 0.053127110 in Vulkan. The entire migrated HUD recording retains
zero false positives in 6,267 inputs. These and the new negative are **training
regression checks**. Full TRAIN has 100 nominal false positives and two false
negatives, compared with 98 and two in the initial HUD-fix model; these results
do not show a broad accuracy improvement. Counts retain the original labels.

VAL positives cover only one contiguous 21-frame loading event. TEST has no
positives, so positive test recall, F1 and balanced accuracy are `null`. Both
holdouts were reused after prior model selection, including two rejected
validation-guided trials in the earlier HUD-fix lineage; those trials did not
evaluate TEST. The selected checkpoint was frozen before this run's remaining
TEST evaluation, and TEST was not used for checkpoint or threshold selection in
this run. This is **not a fresh benchmark**. Generalization to new positive
events, recordings and capture settings remains unestablished.

## Runtime verification

On an AMD Radeon RX 6800 with Vulkan validation requested, 33,386 predictions
covered 33,383 unique inputs. Maximum absolute difference from the CPU reference
was `1.0132789611816406e-6`. The production wrapper passed PNG preprocessing and
tensor parity checks for six fixtures in both x86 and x64. These checks establish
numerical agreement, not accuracy on new recordings or resolution of the
reported pause.

Only the model, compact public metadata and this card are included. Private raw
images, image paths, session identifiers, per-frame manifests and run artifacts
are excluded.
