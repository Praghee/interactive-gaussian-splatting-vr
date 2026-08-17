"""
charts.py — figures for the precision x SH-order image quality study.

Reads scores.csv (columns: preset, sh_order, pose, size_mb, psnr, ssim).
No images needed.

Outputs
    fig1_pareto.png       size vs quality, frontier, dominated cells
    fig2_heatmaps.png     PSNR and SSIM grids side by side
    fig3_interaction.png  convergence lines + per-column spread
    fig4_perview.png      per-view breakdown
"""

import csv

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------

PRESETS = ["Very High", "High", "Balanced", "Low", "Very Low"]
ORDERS = [3, 2, 1, 0]
POSES = ["C1", "C2", "C3", "C4", "C5"]

REFERENCE = ("Very High", 3)
REFERENCE_MB = 62.6            # measured size of the reference asset
VISIBLE = 40                   # dB above which differences are not visible

COLOUR = {"Very High": "#40396b", "High": "#2b6cb0", "Balanced": "#2c9c8f",
          "Low": "#63a95f", "Very Low": "#bfae3c"}
MARKER = {3: "o", 2: "s", 1: "^", 0: "v"}

plt.rcParams.update({
    "font.size": 10,
    "axes.titlesize": 12,
    "axes.titlepad": 10,
    "axes.spines.top": False,
    "axes.spines.right": False,
    "grid.alpha": 0.25,
})


# ---------------------------------------------------------------------------
# Load: group the rows by cell, then average across the five views
# ---------------------------------------------------------------------------

by_cell = {}
for row in csv.DictReader(open("scores.csv", newline="")):
    cell = (row["preset"], int(row["sh_order"]))
    by_cell.setdefault(cell, []).append(row)

psnr, ssim, size, per_view = {}, {}, {}, {}
for cell, rows in by_cell.items():
    psnr[cell] = np.mean([float(r["psnr"]) for r in rows])
    ssim[cell] = np.mean([float(r["ssim"]) for r in rows])
    size[cell] = float(rows[0]["size_mb"])
    per_view[cell] = {r["pose"]: float(r["psnr"]) for r in rows}

CELLS = [(p, o) for p in PRESETS for o in ORDERS if (p, o) in psnr]


# ---------------------------------------------------------------------------
# Figure 1 — the design space and its frontier
#
# A cell is on the frontier if nothing smaller scores higher. Walking the
# cells from smallest to largest, that is simply "better than everything so
# far".
# ---------------------------------------------------------------------------

frontier = []
best_so_far = -np.inf
for cell in sorted(CELLS, key=lambda c: size[c]):
    if psnr[cell] > best_so_far:
        frontier.append(cell)
        best_so_far = psnr[cell]

fig, ax = plt.subplots(figsize=(9, 6))

# Solid: on the frontier. Faded: beaten by something smaller.
for cell in CELLS:
    preset, order = cell
    on = cell in frontier
    ax.scatter(size[cell], psnr[cell],
               color=COLOUR[preset], marker=MARKER[order],
               s=150 if on else 55,
               edgecolor="black" if on else "none",
               linewidth=1.3, alpha=1.0 if on else 0.30,
               zorder=4 if on else 2)

ax.plot([size[c] for c in frontier], [psnr[c] for c in frontier],
        color="black", linestyle="--", linewidth=1.4, alpha=0.75, zorder=3)

# Log x so the cheap end, where five cells sit within 8 MB, is legible.
ax.set_xscale("log")
ax.set_xticks([4, 6, 10, 20, 40, 60])
ax.get_xaxis().set_major_formatter(plt.ScalarFormatter())
ax.set_xlim(3.0, 75)
ax.set_ylim(22, 61)

ax.axhline(VISIBLE, color="grey", linestyle=":", linewidth=1)
ax.text(72, VISIBLE + 0.6, "40 dB — difference not visible",
        fontsize=8.5, color="#777", ha="right")

ax.axvline(REFERENCE_MB, color="#999", linestyle="-.", linewidth=1)
ax.text(REFERENCE_MB * 0.94, 59.5, "reference\n62.6 MB", fontsize=8.5,
        color="#777", ha="right", va="top")


def label_offset(quality):
    """Keep frontier labels clear of each other and of the markers."""
    if quality > 55:                    # top pair: sit above
        return (0, 14), "center"
    if quality < 32:                    # cheap cluster: sit below
        return (0, -18), "center"
    return (12, -3), "left"             # mid-range: sit to the right


# Label only frontier points that are visually distinct; near-duplicates would
# collide and add nothing the CSV does not already say.
last_labelled = -np.inf
for preset, order in frontier:
    quality = psnr[(preset, order)]
    if quality - last_labelled < 1.5:
        continue
    offset, align = label_offset(quality)
    ax.annotate(f"{preset} @ SH{order}", (size[(preset, order)], quality),
                fontsize=8.5, textcoords="offset points",
                xytext=offset, ha=align)
    last_labelled = quality

ax.annotate("", xy=(11.7, 42.6), xytext=(7.6, 29.9),
            arrowprops=dict(arrowstyle="<->", color="#c05621", linewidth=1.6))
ax.text(9.3, 36.5, "12.8 dB\nfor 4 MB", fontsize=9, color="#c05621",
        ha="center", fontweight="bold")

preset_keys = [Line2D([], [], linestyle="", marker="o", markersize=8,
                      color=COLOUR[p], label=p) for p in PRESETS]
order_keys = [Line2D([], [], linestyle="", marker=MARKER[o], markersize=8,
                     color="#666", label=f"SH order {o}") for o in ORDERS]
spacer = Line2D([], [], linestyle="", label="")

ax.legend(handles=preset_keys + [spacer] + order_keys, loc="lower right",
          fontsize=8.5, frameon=True, framealpha=0.95, borderpad=0.8)

ax.set_xlabel("Scene size (MB, log scale) — SH3 measured, lower orders calculated")
ax.set_ylabel("PSNR vs reference (dB)")
ax.grid(which="major")
fig.tight_layout()
fig.savefig("fig1_pareto.png", dpi=150)
plt.close(fig)


# ---------------------------------------------------------------------------
# The remaining figures use the original styling: matplotlib defaults and a
# viridis-derived palette. Figure 1 above has its own look.
# ---------------------------------------------------------------------------

plt.rcParams.update(plt.rcParamsDefault)
COLOURS = dict(zip(PRESETS, plt.cm.viridis(np.linspace(0.12, 0.88, len(PRESETS)))))


# ---------------------------------------------------------------------------
# Figure 2 — the grid, both metrics
# ---------------------------------------------------------------------------

def draw_heatmap(ax, values, title, fmt, cmap):
    grid = np.array([[values.get((p, o), np.nan) for o in ORDERS]
                     for p in PRESETS])
    image = ax.imshow(grid, cmap=cmap, aspect="auto")
    ax.set_xticks(range(len(ORDERS)), [f"SH {o}" for o in ORDERS])
    ax.set_yticks(range(len(PRESETS)), PRESETS)

    lo, hi = np.nanmin(grid), np.nanmax(grid)
    midpoint = lo + 0.55 * (hi - lo)

    for i, preset in enumerate(PRESETS):
        for j, order in enumerate(ORDERS):
            if (preset, order) == REFERENCE:
                ax.text(j, i, "reference", ha="center", va="center",
                        fontsize=9, style="italic", color="#666")
            else:
                value = grid[i, j]
                ax.text(j, i, fmt.format(value), ha="center", va="center",
                        fontsize=9.5,
                        color="white" if value < midpoint else "black")

    ax.set_title(title, fontsize=11)
    return image


fig, axs = plt.subplots(1, 2, figsize=(13, 4.8))
left = draw_heatmap(axs[0], psnr, "PSNR vs reference (dB)", "{:.2f}", "viridis")
right = draw_heatmap(axs[1], ssim, "SSIM vs reference", "{:.4f}", "magma")
fig.colorbar(left, ax=axs[0], shrink=0.85)
fig.colorbar(right, ax=axs[1], shrink=0.85)
fig.tight_layout()
fig.savefig("fig2_heatmaps.png", dpi=150)
plt.close(fig)


# ---------------------------------------------------------------------------
# Figure 3 — precision stops mattering once a band is dropped
# ---------------------------------------------------------------------------

# How far apart the best and worst precision are, at each SH order.
spreads = []
for order in ORDERS:
    values = [psnr[(p, order)] for p in PRESETS if (p, order) in psnr]
    spreads.append(max(values) - min(values))

fig, (axa, axb) = plt.subplots(1, 2, figsize=(13, 5),
                               gridspec_kw={"width_ratios": [1.5, 1]})

for preset in PRESETS:
    orders = [o for o in ORDERS if (preset, o) in psnr]
    axa.plot(orders, [psnr[(preset, o)] for o in orders], "o-",
             color=COLOURS[preset], linewidth=2.2, markersize=8, label=preset)

axa.set_xticks(ORDERS)
axa.invert_xaxis()
axa.set_xlabel("SH order (bands used)")
axa.set_title("Five precisions, converging", fontsize=11)
axa.set_ylabel("PSNR vs reference (dB)")
axa.legend(fontsize=9, title="precision", title_fontsize=9)
axa.grid(alpha=0.25)

bars = axb.bar([f"SH {o}" for o in ORDERS], spreads,
               color=["#c05621", "#7b8794", "#7b8794", "#7b8794"])
for bar, spread in zip(bars, spreads):
    axb.text(bar.get_x() + bar.get_width() / 2, spread + max(spreads) * 0.02,
             f"{spread:.2f} dB", ha="center", fontsize=10)

axb.set_ylabel("PSNR spread across all five precisions (dB)")
axb.set_title("How much precision buys you,\nat each SH order", fontsize=11)
axb.grid(alpha=0.25, axis="y")

fig.tight_layout()
fig.savefig("fig3_interaction.png", dpi=150)
plt.close(fig)


# ---------------------------------------------------------------------------
# Figure 5 — which view each configuration struggles with
# ---------------------------------------------------------------------------

fig, axs = plt.subplots(1, 2, figsize=(13, 5))
x = np.arange(len(POSES))

# Left: SH order held at 3, precision varying.
width = 0.15
for k, preset in enumerate([p for p in PRESETS if (p, 3) in psnr]):
    heights = [per_view[(preset, 3)][pose] for pose in POSES]
    axs[0].bar(x + k * width, heights, width,
               color=COLOURS[preset], label=preset)

axs[0].set_xticks(x + width * 1.5, POSES)
axs[0].set_ylabel("PSNR (dB)")
axs[0].set_title("SH order 3 — precision varying", fontsize=11)
axs[0].legend(fontsize=8)
axs[0].grid(alpha=0.25, axis="y")

# Right: precision held at Very High, SH order varying.
width = 0.22
for k, order in enumerate([o for o in ORDERS if ("Very High", o) in psnr]):
    heights = [per_view[("Very High", order)][pose] for pose in POSES]
    axs[1].bar(x + k * width, heights, width, label=f"SH {order}")

axs[1].set_xticks(x + width, POSES)
axs[1].set_ylabel("PSNR (dB)")
axs[1].set_title("Very High precision — SH order varying", fontsize=11)
axs[1].legend(fontsize=8)
axs[1].grid(alpha=0.25, axis="y")

fig.tight_layout()
fig.savefig("fig4_perview.png", dpi=150)
plt.close(fig)


# ---------------------------------------------------------------------------
# Console summary
# ---------------------------------------------------------------------------

print("Frontier — best quality available at each size\n")
for preset, order in frontier:
    print(f"  {size[(preset, order)]:6.1f} MB  {psnr[(preset, order)]:6.2f} dB"
          f"   {preset} @ SH{order}")

dominated = [c for c in CELLS if c not in frontier]
print(f"\nDominated — {len(dominated)} of {len(CELLS)} cells")
for preset, order in dominated:
    print(f"    {preset} @ SH{order}")

print("\nPrecision gap at each SH order\n")
for order, spread in zip(ORDERS, spreads):
    print(f"  SH{order}: {spread:6.2f} dB")

print("\nWrote fig1_pareto.png, fig2_heatmaps.png, "
      "fig3_interaction.png, fig4_perview.png")
