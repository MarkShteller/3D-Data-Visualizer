# 3D Data Visualizer

A point cloud viewer built for **debugging** point clouds, not just looking at them.

![Unity 6.5](https://img.shields.io/badge/Unity-6000.5.8f1-black)
![URP 17](https://img.shields.io/badge/URP-17.5-black)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue)
![Tests](https://img.shields.io/badge/tests-59%20edit%20%2B%2028%20play-brightgreen)

<!-- Drop a screenshot or GIF here — docs/media/demo.gif -->

---

## Why

Most point cloud viewers are built for inspecting scans. When you are evaluating a
perception model, the questions are different:

- *Did my exporter actually write the confidence channel?*
- *Why is this prediction 400 m from the ground truth?*
- *Is that noise, or is my colour mapping wrong?*

This viewer is built around those questions. The design rule throughout is that **"your
data doesn't have this" and "this tool can't show it" must never look the same**, because
they send you down completely different debugging paths.

## What it does today

**Reads real files.** Stanford PLY in `ascii`, `binary_little_endian` and
`binary_big_endian`, with whatever per-vertex properties the file happens to carry.
Big-endian is verified bit-for-bit against little-endian, because it is the branch nobody
exercises and everybody gets wrong.

**Tells you what is in your file.** The attribute panel lists every channel as present or
absent. A render mode that needs a channel you don't have stays visible and says why it is
unavailable, rather than silently disappearing.

**Falls back sensibly.** No colour data? Points are coloured by camera-space distance so
the geometry is still readable. Colour present? Per-point RGB, with `uchar` treated as
sRGB and `float` as linear — conflating those washes out every float-coloured file.

**Keeps your custom scalars.** An unrecognised per-point property becomes a named scalar
field under its original name, so `scalar_C2C_absolute_distances` is still recognisable in
the UI. CloudCompare's `scalar_` prefix is stripped for semantic matching but preserved
for display.

**Preserves geo-referenced precision.** Double-precision positions at UTM magnitudes are
stored relative to a per-cloud origin. Kept raw, float32 loses roughly half a metre at
eastings near 5×10⁵ and the cloud renders as a visibly wobbling mess.

**Aligns clouds for comparison.** Two captures in different coordinate frames can land
kilometres apart. One click centres each on the origin so they overlay. It is a display
transform only — the source data is untouched and the move is exactly reversible.

**Navigates at any scale.** Zoom steps are a fixed fraction of the distance to the cloud
*surface*, not to the orbit pivot, so it is fast at range, asymptotically gentle up close,
and cannot scroll through the geometry.

## Performance

Measured on an RTX 3070 Laptop, D3D12, in the Unity editor.

| | |
|---|---|
| 2M-point CloudCompare PLY (35 MB, binary LE) | **113 ms** end to end — 300 MB/s, 17.6 M points/s |
| 20M synthetic points | 1148 ms generate + chunk, 88 ms upload, **305 MB** VRAM, 153 chunks |
| 20M points on screen | **one indirect draw call**, 1.09 ms/frame at 512×512 |
| Progressive upload | 320 MB becomes resident over ~39 frames at an 8 MB/frame budget |

The frame time is at 512×512; at full resolution with larger points the renderer becomes
fill-rate bound and this number will be higher. The single-draw-call figure holds at any
resolution.

## Quick start

Requires **Unity 6000.5.8f1** and Git LFS (the sample cloud is an LFS object).

```bash
git lfs install
git clone <this repo>
```

Open the project, load `Assets/App/Scenes/Main.unity`, and press Play. A synthetic cloud is
generated at startup; use **Open File…** in the left dock to load your own, or pass a path
on the command line (`viz.exe cloud.ply`).

If the project was cloned fresh, run **Tools ▸ Point Cloud ▸ Apply Project Setup** once —
it applies the player settings and render-pipeline configuration the tool depends on.

### Controls

| | |
|---|---|
| Orbit | Left drag |
| Pan | Middle drag, or Shift + left drag |
| Dolly | Right drag |
| Zoom | Wheel (anchored to whatever is under the cursor) |
| Focus selected / frame all | `F` / `A` |
| Fly mode | `` ` `` then WASD + QE, Shift to boost |

## Architecture

Five runtime assemblies. The load-bearing rule is that **`Rendering` never references
`Formats`** — the renderer only ever sees `Core` types, which is what will let a streaming
source drop in later without touching a shader or a draw loop.

```
Core ◄──── Formats ◄──┐
 ▲  ▲                 │
 │  └── Rendering ────┤   (Rendering → Core only)
 └───────── App ──────┘
```

- **Core** — the SoA data model, attribute encoding, spatial chunking, the source
  abstraction, and the synthetic generator.
- **Formats** — PLY reader, shared parsing primitives, and a registered VRS stub.
- **Rendering** — GPU residency, the procedural point shader, colormaps, draw submission.
- **App** — camera, input, runtime UI Toolkit interface, file dialog, orchestration.

Three decisions shape everything else:

**Structure of arrays, one `ByteAddressBuffer` per attribute.** Attributes are optional, so
an interleaved layout would force a worst-case stride even on an XYZ-only cloud. Each
render mode reads only the streams it needs.

**Points are Morton-sorted into 128 K chunks, then shuffled *within* each chunk.** Chunks
stay spatially tight so frustum culling works, while any prefix of a chunk is an unbiased
uniform sample of it. Progressive display, movement decimation and level of detail all
reduce to picking a prefix length — no octree, no second data layout.

**Six vertices per point via `RenderPrimitivesIndirect`.** No vertex buffer, no index
buffer, no per-point GameObject. `Mesh` with `MeshTopology.Points` is not an option:
D3D10+ removed point size, so every point would be exactly one pixel.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the details, including several
non-obvious performance findings that cost real measurement to track down.

## Tests

```bash
Unity.exe -batchmode -runTests -testPlatform EditMode -projectPath . -testResults results.xml
Unity.exe -batchmode -runTests -testPlatform PlayMode -projectPath . -testResults results.xml
```

59 EditMode and 28 PlayMode tests. The PlayMode suite renders to a `RenderTexture` and
asserts actual pixels — that mid-grey reads back as mid-grey (the sRGB regression test),
that the depth ramp varies with depth, and that a 20M-point cloud reaches the GPU as a
single draw.

Note that `WaitForEndOfFrame` never resumes under `-batchmode`, so tests drive rendering
with an explicit `Camera.Render()` instead.

## Not there yet

Stated plainly, because a roadmap that reads like a feature list is not useful:

- **Formats** — PCD, OBJ and FBX are designed but unimplemented. VRS is a registered stub
  that fails with "not supported yet" rather than "unknown format", so the discovery path
  is already exercised.
- **Eye-Dome Lighting** — the single biggest legibility win for point clouds. The clip-plane
  fitter it depends on is already in place.
- **Point picking and the attribute inspector** — clicking a point does nothing today.
- **Out-of-core LOD** — the chunk model accommodates it; roughly 60M points fit in VRAM now.
- **Sequenced playback** — the source interface is frame-sequenced throughout, but nothing
  produces more than one frame yet.

### Known limitations

- The Win32 file dialog is Windows-only; elsewhere the UI falls back to a path field.
- A PLY vertex block is read whole rather than in slabs, capped at 6 GB with a clear error.
- Point spacing is estimated from bounds and count rather than a median nearest-neighbour
  distance, which is good enough to open looking solid but not exact.

## Licence

**Not yet chosen.** Add a `LICENSE` file before making the repository public — without one,
default copyright applies and nobody may reuse the code.

## Sample data

`Assets/Resources/Nube_2mill.ply` is a 37 MB Git LFS object used by the validation tests.
It is in `Resources/`, which means it is force-included in player builds; move it outside
`Assets/` if you build for distribution.
