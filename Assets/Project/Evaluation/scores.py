"""
scores.py — scores the precision x SH-order grid and writes scores.csv.

Layout expected:

    Images/
      SH3/  SH3_01_VeryHigh_C1.png ... SH3_05_VeryLow_C5.png
      SH2/  SH2_01_VeryHigh_C1.png ...
      SH1/  ...
      SH0/  ...

Reference = SH3 / 01_VeryHigh.

Output:
    scores.csv    columns: preset, sh_order, pose, size_mb, psnr, ssim
"""

import csv
from pathlib import Path

import numpy as np
from PIL import Image
from skimage.metrics import peak_signal_noise_ratio as psnr
from skimage.metrics import structural_similarity as ssim

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

ROOT = Path("Images")
POSES = ["C1", "C2", "C3", "C4", "C5"]
ORDERS = [3, 2, 1, 0]
PRESETS = ["Very High", "High", "Balanced", "Low", "Very Low"]

REFERENCE = ("Very High", 3)

# Filename stem for each preset.
STEM = {
    "Very High": "01_VeryHigh",
    "High":      "02_High",
    "Balanced":  "03_Balanced",
    "Low":       "04_Low",
    "Very Low":  "05_VeryLow",
}

# Measured scene size at SH3, from Table 1 (Kitchen + Drawer + Door):
#
#     preset      Kitchen  Drawer   Door   scene   bytes/splat
#     Very High     52.5     8.6     1.5    62.6       248
#     High          25.3     4.2     0.7    30.2       120
#     Balanced      17.8     2.9     0.5    21.2        84
#     Low           16.1     2.6     0.5    19.2        76
#     Very Low       9.8     1.6     0.3    11.7        46
#
SCENE_MB = {"Very High": 62.6, "High": 30.2, "Balanced": 21.2,
            "Low": 19.2, "Very Low": 11.7}
TOTAL_BYTES = {"Very High": 248, "High": 120, "Balanced": 84,
               "Low": 76, "Very Low": 46}

# Bytes not spent on spherical harmonics, and bytes per SH coefficient.
OTHER_BYTES = {"Very High": 56, "High": 24, "Balanced": 24,
               "Low": 16, "Very Low": 14}
SH_BYTES = {"Very High": 12, "High": 6, "Balanced": 4, "Low": 4, "Very Low": 2}

# How many SH coefficients each order keeps.
COEFFS = {3: 15, 2: 8, 1: 3, 0: 0}


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def path_for(preset, order, pose):
    return ROOT / f"SH{order}" / f"SH{order}_{STEM[preset]}_{pose}.png"


def size_mb(preset, order):
    """Measured at SH3; calculated from the byte model at lower orders."""
    if order == 3:
        return SCENE_MB[preset]
    bytes_used = OTHER_BYTES[preset] + SH_BYTES[preset] * COEFFS[order]
    return round(bytes_used * SCENE_MB[preset] / TOTAL_BYTES[preset], 1)


def load(preset, order, pose):
    img = Image.open(path_for(preset, order, pose)).convert("RGB")
    return np.asarray(img, np.float64) / 255.0


def has_all_poses(preset, order):
    return all(path_for(preset, order, pose).exists() for pose in POSES)


# ---------------------------------------------------------------------------
# Score
# ---------------------------------------------------------------------------

cells = [(p, o) for p in PRESETS for o in ORDERS]
missing = [c for c in cells if not has_all_poses(*c)]

if missing:
    print(f"Skipping {len(missing)} cell(s) with no captures:")
    for preset, order in missing:
        print(f"   {preset} @ SH{order}")

cells = [c for c in cells if c not in missing]

rows = []
for pose in POSES:
    ref = load(*REFERENCE, pose)
    for preset, order in cells:
        if (preset, order) == REFERENCE:
            continue
        img = load(preset, order, pose)
        rows.append({
            "preset": preset,
            "sh_order": order,
            "pose": pose,
            "size_mb": size_mb(preset, order),
            "psnr": round(psnr(ref, img, data_range=1.0), 2),
            "ssim": round(ssim(ref, img, channel_axis=2, data_range=1.0), 4),
        })

with open("scores.csv", "w", newline="") as f:
    writer = csv.DictWriter(
        f, fieldnames=["preset", "sh_order", "pose", "size_mb", "psnr", "ssim"])
    writer.writeheader()
    writer.writerows(rows)

print(f"Wrote scores.csv — {len(rows)} rows "
      f"({len(cells) - 1} configurations x {len(POSES)} poses)")
