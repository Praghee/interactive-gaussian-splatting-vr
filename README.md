# Interactive Gaussian Splatting VR

A virtual-reality viewer for **3D Gaussian Splatting (3DGS)** scenes, built in Unity for **Meta Quest 3 / 3S** (standalone) and **PCVR via Meta Quest Link**. It renders real, photo-captured `.ply` splat scenes in stereo VR and lets you reach in and handle objects with your hands: swing a **hinged cupboard door**, pull a **sliding drawer**, and **pick up an apple** inside a photoreal kitchen, with smooth stick locomotion to move around.

Everything you see is made of millions of tiny Gaussian "splats" reconstructed from photographs, rendered live at VR frame rates and depth-sorted as one stream so moving objects blend correctly with the rest of the scene.

The signature interaction is **multimodal**: you hold **one controller** for locomotion while your **other, bare hand** is tracked for grabbing — both at the same time.

---

## Requirements

- **Unity 6000.3.19f1** — open with exactly this version.
- **Meta Quest 3 or 3S** headset, with **hand tracking enabled** (`Settings → Movement Tracking → Hand tracking`).
- A Windows PC to build the Android (Quest) player.
- (Optional) **Meta Quest Link** for testing in the Editor.

The project already includes the packages it needs:

| Package | Version |
|---|---|
| Meta XR Core SDK | 205.0.0 |
| Universal Render Pipeline (URP) | 17.3.0 |
| OpenXR Plugin | 1.17.1 |
| XR Plugin Management | 4.6.0 |
| Input System | 1.19.0 |

### Graphics API — important

Both GPU sort backends use **Shader Model 6.0 wave intrinsics**, so the graphics API must be **Direct3D12** (Windows) or **Vulkan** (Android). Direct3D11 tops out at SM 5.0: the sort kernels report unsupported, and splats render unsorted with visibly wrong transparency. The project ships configured correctly — Windows on D3D12, Android on Vulkan.

If sorting ever looks wrong, check the Console for:

```
[GaussianSplatVR] Splats are rendering UNSORTED — ...
```

which names the active graphics device and what to do about it.

> Meta's Project Setup Tool carries a *Required* rule titled "Manual selection of Graphic API, favoring Direct3D11". Don't apply its fix — it reverts Windows to D3D11 and silently disables sorting. Meta's own runtime check accepts both D3D11 and D3D12.

---

## Getting started

1. **Open the project** in Unity Hub with version **6000.3.19f1**. First import may take a while (shaders + sample splats).
2. **Open the main scene:** `Assets/Project/Scenes/InteractiveGaussianSplatting.unity`
3. Press **Play** with a headset connected via Link, or build to the Quest (below).

### Testing over Meta Quest Link

1. In the **Meta Quest Link** desktop app: *Settings → General → OpenXR Runtime* → **Set Meta Quest Link as active**.
2. *Settings → Developer* → enable **Developer Runtime Features**, then restart Unity.
3. In Unity: *Project Settings → XR Plug-in Management → OpenXR* → **Play Mode OpenXR Runtime = Oculus**.

This last setting is stored per machine, not in the project. A stale selection here is the most common reason hand tracking or the headset display fails to come up in Play mode.

Note that over Link on PC, pose data for controllers is unavailable when you’re not actively using them (such as when they’re lying on a table).

### Simultaneous hands + controllers

The one-controller-plus-one-tracked-hand scheme relies on Meta's `XR_META_simultaneous_hands_and_controllers` extension. Three settings enable it, all pre-configured in the project:

- *Project Settings → XR Plug-in Management → OpenXR* → **Meta XR Feature** ticked on **both** the Android and Windows tabs.
- On the scene's `OVRCameraRig`, under **OVRManager → Quest Features → General** → **Hand Tracking Support = Controllers And Hands**.
- On the same component, under **Hand Tracking** → **Simultaneous Hands And Controllers Enabled** and **Launch Simultaneous H&C On Startup**, both on.

The first flag is a build-time capability; the second is the runtime switch. Both are needed — the capability alone does nothing.

### Render mode

OpenXR keeps **separate** render-mode settings for Windows and Android, and switching build target can reset them. If one eye ever renders wrong, check this first:

- **Android tab** → Render Mode = **Single Pass Instanced** (required for Quest performance).
- **Windows tab** → whichever render mode displays correctly in your headset.

Re-check after every platform switch.

### Building for Quest

- **File → Build Settings** → Platform = **Android**, then Switch Platform.
- Player settings: **IL2CPP**, **ARM64**, **Vulkan**.
- Add the main scene, connect the Quest, and **Build And Run**.

---

## Controls

You hold **one controller** and keep your **other hand bare**. The controller drives locomotion; the bare hand grabs.

Which hand does which is detected automatically — the **XR Control Layout** component on `OVRCameraRig` asks OVRInput which hand is actually holding a controller, so swapping the controller to your other hand mid-session swaps the roles with it. **Preferred Controller Hand** on that component is only the tie-break, used when both hands hold a controller or neither hand is tracked.

### Locomotion — one controller, grip as a modifier

| Input | Action |
|---|---|
| **Stick** (no grip) | Move — forward / back / strafe (relative to where you look) |
| **Grip + stick sideways** | Turn left / right |
| **Grip + stick up / down** | Rise / descend |

In turn/fly mode the stick does **one action at a time** (turn *or* up/down, whichever you push toward most). Turning is **locked while you're holding an object**, so a head-centred turn can't fling the door or drawer open. Speeds, smoothing, and the grip threshold are on the **XR Smooth Locomotion** component.

### Grabbing — the bare hand

| Input | Action |
|---|---|
| **Curl all five fingers** (make a fist) | Grab — open your hand to release |
| **Controller grip** (if that hand holds one) | Grab — release to let go |

A small marker on the grabbing hand shows its state:

| Marker | Meaning |
|---|---|
| **Yellow** | Hand open — not gripping |
| **Red** | Gripping, but nothing caught |
| **Green** | Gripping and holding something |

Fist detection sums the bend across each finger's two knuckles, so it doesn't care how big your hands are. Grab and release use deliberately different thresholds — the gap is what stops the grip flickering. Tune **Closed Degrees** and **Grab Enter** on the **XR Grab Gesture** component if it doesn't match your hand.

An object is released **only** when you open your hand; reaching a joint's limit never lets go.

### What you can do

- **Cupboard door** — grab the handle and swing it around the hinge; it follows your hand and stops at its open and closed angles.
- **Drawer** — grab the front and slide it out or in. Any grabbable object left **fully inside** the drawer rides along with it — set the apple in, push the drawer shut, and it goes in too.
- **Apple** — grab it freely (move and rotate), and it stays wherever you let go. Drop it in the open drawer or on the counter.

---

## Adding your own splat scenes

The project imports standard 3DGS `.ply` files (the format produced by common Gaussian-splatting trainers) and turns them into optimized Unity assets.

1. **Tools → Gaussian Splat → Convert PLY to Asset**
2. Select your `.ply` file.
3. Pick a **quality preset** — trading file size / VRAM for fidelity: `Very Low → Low → Balanced → High → Very High` (or **Custom** for per-attribute control).
4. Convert. This writes a Gaussian splat asset into the project.
5. In a scene, add a **Gaussian Splat Renderer** component to a GameObject and assign your new asset as its **Source**.

The included sample scenes were made exactly this way. Sample assets live under `Assets/Project/Samples/` (Kitchen, Door, Drawer, Apple), alongside the source `.ply` files they were converted from.

To make a new object grabbable, add a **Splat Grab Volume** — an invisible box standing in for geometry the splats don't have — plus the matching interaction component: **Splat Free Grab** (pick-up), **Splat Slide Joint** (drawer), or **Splat Hinge Joint** (door). For a container that carries its contents, add **Splat Container**.

Note that every preset except **Very High** produces a *chunked* asset. Chunking is what makes the optional frustum culling on the render feature possible, so prefer **High** or below for room-scale scenes.

---

## How rendering works

All active splat clouds are drawn as **one globally depth-sorted stream**, which is what lets a moving object interpenetrate a static one without popping:

1. A compute pass decodes each cloud into a shared buffer — position, 2D ellipse axes, spherical-harmonics colour — writing a view-space depth key and its global index as the sort payload.
2. One GPU sort orders every splat from every cloud back-to-front. Two interchangeable backends are available: **DeviceRadixSort** (the default, much faster on mobile GPUs) and **FidelityFX ParallelSort**.
3. A single procedural draw renders the whole sorted stream. The vertex shader reprojects each splat's world centre per eye, so one draw covers both eyes under multiview or instanced stereo.

Optional **chunk frustum culling** tests each 256-splat chunk's dilated bounds against the frustum and skips the decode and SH work for anything outside the view — a large win in room-scale scenes, no gain when a single cloud fills the view.

Everything is configured on the **Gaussian Splat URP Feature**, added to both `PC_Renderer` and `Mobile_Renderer` under `Assets/Settings/`.

---

## Project layout

```
Assets/Project/
├── Runtime/
│   ├── Core/          Gaussian splat asset + shared math
│   ├── Rendering/     The VR splat renderer (decode, sort, culling, URP integration)
│   └── Interaction/   Grab system, joints, container, locomotion, control layout
├── Shaders/           Compute + rendering shaders (decode, sort, cull, splat draw)
├── Editor/            PLY reader and the Convert-PLY-to-Asset tool
├── Materials/         Scene materials
├── Samples/           Example captured Gaussian scenes + source PLY files
└── Scenes/            InteractiveGaussianSplatting.unity (main scene)
```

---

## Third-party attribution

This project's own source is licensed **MIT** (see the SPDX headers in each file). It also adapts or includes work from others, which remains under its respective license:

- **UnityGaussianSplatting** — Aras Pranckevičius (aras-p). The 3D→2D projection and spherical-harmonics evaluation in the decode compute shader are adapted from this project. MIT.
- **GPUSorting** — Thomas Smith (b0nes164). The DeviceRadixSort GPU sort (default backend) is adapted from this project. MIT.
- **gsplat-unity** — reference used when adapting the radix sort pass into a URP render feature.
- **AMD FidelityFX ParallelSort** — the alternative FFX sort backend. MIT.
- **Meta XR Core SDK** — the camera rig, hand and controller models, and OpenXR integration. © Meta Platforms, under the Oculus SDK License Agreement.

## License

Original code in this repository is released under the **MIT License**. Bundled third-party assets and adapted code are governed by the licenses noted above; retain their attribution and license terms when redistributing.
