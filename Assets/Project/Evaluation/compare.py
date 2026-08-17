"""
compare.py — visual comparison at the two views of interest.

C3 is where band removal does most damage; C5 is the least affected view. Run
side by side they bracket how much the scene content matters.

Full frames, no crops. Difference maps share one linear scale per view, so the
relative magnitudes stay truthful.

    Images/
      SH3/  SH3_01_VeryHigh_C3.png ... SH3_05_VeryLow_C5.png
      SH2/  ...  SH1/  ...  SH0/  ...

Outputs, per view
    <view>_failure_modes.png  the three extreme configurations
    <view>_diffmaps.png       absolute difference from the reference
    <view>_matched_size.png   same file size, two routes to it
"""

import csv
from pathlib import Path

import numpy as np
from PIL import Image
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------

ROOT = Path("Images")
VIEWS = ["C3", "C5"]

PRESETS = ["Very High", "High", "Balanced", "Low", "Very Low"]
ORDERS = [3, 2, 1, 0]
STEM = {"Very High": "01_VeryHigh", "High": "02_High", "Balanced": "03_Balanced",
        "Low": "04_Low", "Very Low": "05_VeryLow"}

REFERENCE = ("Very High", 3)
REFERENCE_MB = 62.6
REF_FILE = "SH3_01_VeryHigh_{view}.png"

# The three configurations examined in detail: each axis alone, then both.
EXTREMES = [("Very Low", 3), ("Very High", 0), ("Very Low", 0)]

# Same file size, reached two different ways.
PAIRS = [(("Very Low", 3), ("High", 1)),
         (("Low", 3), ("High", 2)),
         (("Balanced", 3), ("Very High", 1))]

FULL = 1200

# Difference maps: clip the shared linear scale to this fraction of the largest
# error present, so the smallest of the three panels is legible. Values above
# the clip point render white. 1.0 would be no gain at all.
DIFF_CLIP = 0.20


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def load(cell, view, width=FULL, as_float=False):
    preset, order = cell
    path = ROOT / f"SH{order}" / f"SH{order}_{STEM[preset]}_{view}.png"
    img = Image.open(path).convert("RGB")
    if width and img.width > width:
        img = img.resize((width, round(img.height * width / img.width)),
                         Image.LANCZOS)
    arr = np.asarray(img, np.uint8)
    return arr.astype(np.float32) / 255.0 if as_float else arr


def error_map(cell, view):
    """Mean absolute difference from the reference, per pixel."""
    ref = load(REFERENCE, view, as_float=True)
    return np.abs(ref - load(cell, view, as_float=True)).mean(axis=2)


def strip(ax):
    ax.set_xticks([])
    ax.set_yticks([])
    for spine in ax.spines.values():
        spine.set_visible(False)


def footnote(fig, view, y):
    fig.text(0.5, y, f"PSNR measured against {REF_FILE.format(view=view)}",
             ha="center", fontsize=9, color="#555")


# ---------------------------------------------------------------------------
# Scores
# ---------------------------------------------------------------------------

psnr, size = {}, {}
for row in csv.DictReader(open("scores.csv", newline="")):
    cell = (row["preset"], int(row["sh_order"]))
    psnr[(cell, row["pose"])] = float(row["psnr"])
    size[cell] = float(row["size_mb"])
size[REFERENCE] = REFERENCE_MB


def label(cell, view):
    preset, order = cell
    if cell == REFERENCE:
        return (f"{preset} @ SH{order}   REFERENCE\n"
                f"{size[cell]} MB · {REF_FILE.format(view=view)}")
    return f"{preset} @ SH{order}\n{size[cell]} MB · {psnr[(cell, view)]:.2f} dB"


# ---------------------------------------------------------------------------
# Figures, per view
# ---------------------------------------------------------------------------

for view in VIEWS:

    # --- 1. the three extreme configurations -------------------------------
    fig, axs = plt.subplots(1, 4, figsize=(18, 5.6))
    for ax, cell in zip(axs, [REFERENCE] + EXTREMES):
        strip(ax)
        ax.imshow(load(cell, view))
        ax.set_title(label(cell, view), fontsize=10.5)
    fig.tight_layout()
    footnote(fig, view, 0.005)
    fig.savefig(f"{view}_failure_modes.png", dpi=150)
    plt.close(fig)

    # --- 2. absolute difference from the reference -------------------------
    maps = [(cell, error_map(cell, view)) for cell in EXTREMES]
    peak = max(m.max() for _, m in maps)
    vmax = peak * DIFF_CLIP                  # shared linear scale, modest gain

    fig, axs = plt.subplots(1, 4, figsize=(18, 5.6))
    strip(axs[0])
    axs[0].imshow(load(REFERENCE, view))
    axs[0].set_title(f"reference\n{REF_FILE.format(view=view)}", fontsize=10.5)

    for ax, ((preset, order), m) in zip(axs[1:], maps):
        strip(ax)
        image = ax.imshow(m, cmap="gray", vmin=0, vmax=vmax)
        ax.set_title(f"{preset} @ SH{order}\nmean error {m.mean():.4f}",
                     fontsize=10.5)

    cbar = fig.colorbar(image, ax=axs[1:].tolist(), shrink=0.7, pad=0.015)
    cbar.set_label(f"absolute error, white at {vmax:.3f} "
                   f"(peak {peak:.3f})", fontsize=9)
    fig.savefig(f"{view}_diffmaps.png", dpi=150, bbox_inches="tight")
    plt.close(fig)

    # --- 3. same file size, two routes to it -------------------------------
    fig, axs = plt.subplots(len(PAIRS), 3, figsize=(14, 5.6 * len(PAIRS)))
    for row, (precision, bands) in enumerate(PAIRS):
        for col, cell in enumerate([REFERENCE, precision, bands]):
            ax = axs[row, col]
            strip(ax)
            ax.imshow(load(cell, view, width=900))
            ax.set_title(label(cell, view), fontsize=10)

        gap = psnr[(precision, view)] - psnr[(bands, view)]
        axs[row, 0].set_ylabel(f"~{size[precision]:.0f} MB\n{gap:+.1f} dB",
                               fontsize=10.5, labelpad=14)

    fig.tight_layout()
    footnote(fig, view, 0.002)
    fig.savefig(f"{view}_matched_size.png", dpi=140)
    plt.close(fig)

    print(f"wrote {view}_failure_modes.png  {view}_diffmaps.png  "
          f"{view}_matched_size.png")


# ---------------------------------------------------------------------------
# Numbers behind the figures
# ---------------------------------------------------------------------------

for view in VIEWS:
    print(f"\nPSNR at {view}\n")
    print(f"  {'':11s}" + "".join(f"{'SH' + str(o):>9s}" for o in ORDERS))
    for preset in PRESETS:
        line = f"  {preset:11s}"
        for order in ORDERS:
            cell = (preset, order)
            line += (f"{'ref':>9s}" if cell == REFERENCE
                     else f"{psnr[(cell, view)]:9.2f}")
        print(line)

print("\nMean absolute error, the three extremes\n")
print(f"  {'configuration':22s}" + "".join(f"{v:>10s}" for v in VIEWS))
for preset, order in EXTREMES:
    line = f"  {preset + ' @ SH' + str(order):22s}"
    for view in VIEWS:
        line += f"{error_map((preset, order), view).mean():10.4f}"
    print(line)
