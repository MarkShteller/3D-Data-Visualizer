# Architecture

Notes on the decisions that shape this codebase, and on several performance findings that
were genuinely non-obvious — each of them cost real measurement to track down, and all of
them would have shipped silently.

## Contents

- [Assemblies](#assemblies)
- [Data model](#data-model)
- [Chunking, and the shuffle](#chunking-and-the-shuffle)
- [Rendering](#rendering)
- [Colour correctness](#colour-correctness)
- [Precision](#precision)
- [The source abstraction](#the-source-abstraction)
- [Performance findings](#performance-findings)
- [Testing approach](#testing-approach)

---

## Assemblies

```
Core ◄──── Formats ◄──┐
 ▲  ▲                 │
 │  └── Rendering ────┤   (Rendering → Core only)
 └───────── App ──────┘
             ▲
          Editor (Editor platform only)
          Tests.EditMode → Core, Formats, Rendering, App
          Tests.PlayMode → all
```

The rule that matters: **`Rendering` never references `Formats`.** The renderer only sees
`Core` types. That is what will allow a VRS or live sensor source to be added later without
touching a shader, a draw loop, or the UI — the seam already exists and is exercised by the
registered VRS stub.

A corollary that bit once: `Tests.EditMode` originally referenced only `Core` and
`Formats`, so a compile error in `App` did not fail the EditMode suite. PlayMode is what
covers `App`.

## Data model

**Structure of arrays, one stream per attribute.** Considered and rejected: an interleaved
per-point struct.

- Attributes are optional. Interleaving forces either a worst-case stride — 30 B/point even
  for an XYZ-only cloud, a 2.5× waste on the most common case — or N compile-time layouts
  with N shader variants.
- Each render mode reads only what it needs. Intensity mode touches positions + intensity
  (16 B/pt), not the full stride.
- The usual argument for interleaving does not apply here. Consecutive `SV_VertexID` maps to
  consecutive point indices, so loads stay perfectly coalesced in *every* stream.

**Every attribute binds as `GraphicsBuffer.Target.Raw`** (`ByteAddressBuffer`): one binding
path, one wrapper type, no per-element-type shader variants, no stride-alignment questions.

**Packing.** Colour → RGBA8 `uint32`, normals → octahedral `uint32`, timestamps → `uint32`
microseconds from the frame epoch. Nothing is packed below 4 bytes: sub-dword loads out of
a `ByteAddressBuffer` need shift-and-mask on every access, which infects every shader path
to save about 13%.

| Cloud | Bytes/pt | 20M |
|---|---|---|
| position + colour (typical) | 16 | 320 MB |
| + normal, intensity, label | 24 | 480 MB |
| everything | 30 | 600 MB |

## Chunking, and the shuffle

Points are Morton-sorted, sliced into 128 K-point chunks, and then **shuffled within each
chunk**.

This is the highest-leverage decision in the codebase. After the shuffle:

- Spatial locality lives at **chunk** granularity — each chunk's AABB is tight, so frustum
  culling rejects real work.
- Sample uniformity lives at **prefix** granularity — any prefix `[Start, Start+k)` is an
  unbiased uniform sample of the chunk.

Which means progressive display during load, decimation while the camera moves, and a
first-cut level of detail all reduce to setting `LodPrefix = Count / k`. No octree, no
second data layout, and an octree can later drop in behind the same field without the
renderer noticing.

There is a test for this: a 5% prefix of a chunk must still span more than half the chunk's
extent. If the shuffle regresses, clouds visibly fill in from one corner.

Morton codes use 10 bits per axis rather than the usual 21. The code only has to order
points well enough that a run of 128 K is spatially tight, and 10 bits leaves 34 bits in a
`ulong` for the point index — so one key sort produces the permutation directly.

## Rendering

**`Graphics.RenderPrimitivesIndirect`, `MeshTopology.Triangles`, six vertices per point.**
`pointId = SV_VertexID / 6`, `corner = SV_VertexID % 6`. No vertex buffer, no index buffer,
no input assembler.

`Mesh` with `MeshTopology.Points` was rejected outright: D3D10+ removed point size, so
`PSIZE` is ignored on D3D11/12 and every point would be exactly one pixel.

**Why indirect rather than one draw per visible chunk:** for a non-indexed D3D draw,
`SV_VertexID` *includes* `StartVertexLocation`, so a command's `startVertex` offsets the
point id for free. All runs share one material with no property block, and 20M points go
out as a single C# call.

> ⚠️ Portability: Vulkan's `gl_VertexIndex` also includes `firstVertex`, but Metal's
> `[[vertex_id]]` does **not** for non-indexed draws. The Windows/D3D target is fine; a
> Metal port needs per-run draws with a `_PointBase` uniform.

**Mode selection is a uniform branch, not shader keywords.** Twelve colour modes as
keywords would multiply against shape × size mode × sRGB into ~144 variants that must all
exist in a build. `_ColorMode` is uniform across every invocation in the draw, so the
branch is fully coherent, and this shader is dominated by buffer reads and ROP writes
anyway. Keywords are reserved for what a branch cannot fix: which buffers exist, and the
geometry of the quad.

**Colour is evaluated in the vertex shader** with `nointerpolation` — six times per point
rather than once, which is cheaper than either a compute prepass (an extra full-size buffer
and a whole pass) or per-pixel evaluation (a point can cover many pixels).

## Colour correctness

Three conversion points, each easy to get backwards.

1. **Per-point colour.** There is no hardware sRGB path for buffers the way there is for
   textures — `ByteAddressBuffer` reads are raw. So `uchar` colour is converted in the
   shader, gated on `Descriptor.ColorIsLinear`. Doing it in-shader rather than at load also
   means the inspector can report the byte-exact source value.
2. **The convention itself.** `uchar red` is 0–255 sRGB; `float red` is 0–1 and linear by
   Open3D/CloudCompare convention. Conflating them washes out every float-coloured file.
3. **The colormap LUT** is created with `linear: false`. Published viridis/turbo tables are
   8-bit sRGB values chosen to be perceptually uniform *in sRGB*, so in a linear project the
   hardware conversion on sample is exactly right.

A PlayMode test renders a 50% grey flat colour and asserts it reads back as 50% grey. If
any conversion is applied twice it lands near 188/255; if one is skipped, near 55.

## Precision

Geo-referenced clouds ship UTM eastings around 5×10⁵, where float32 has 0.25–0.5 m of
quantisation. Stored raw, such a cloud renders as a visibly wobbling mess with no obvious
cause.

Positions are therefore stored **relative to a per-cloud origin**, with the offset kept in
the descriptor so the exact absolute coordinate is still recoverable. The offset is rounded
to whole metres, because two exports of the same scene rarely sample identically and an
offset that wobbles between loads would stop two copies of a cloud from overlaying.

Applied only when the source really is `double`. A float source has already lost whatever
precision it was going to lose, and re-centring it would break bit-exact comparison against
the same cloud exported in another format.

## The source abstraction

`IPointCloudSource` is **frame-sequenced from day one**, even though every implemented
format is a single static frame (`FrameCount == 1`). That is deliberate: it is the seam a
VRS stream drops into without the renderer, the transport or the UI changing shape.

The VRS stub is registered rather than absent, so a `.vrs` file resolves to a real factory,
opens through the normal code path, and fails with *"not supported yet — export to PLY or
PCD"* rather than *"unknown format"*. The discovery, error and UI paths are already
exercised.

Frames are reference counted, because a sequence will have three independent holders — the
prefetch ring, the renderer, and the inspector — and any of them releasing must not free
`NativeArray`s out from under the others.

## Performance findings

Four things that were measured, not guessed. Each was invisible until instrumented, and
each would have shipped.

### Burst does not compile a job first invoked on a background thread

A job whose **first** invocation happens on a plain background thread does not get
Burst-compiled at all. It silently runs the managed fallback — and stays that way for the
process.

```
cold first touch from Task.Run:  2191 ms
same build, main thread first:     84 ms
```

File loading runs on `Task.Run`, because blocking IO has no business occupying a Job System
worker. So without intervention the first load of every session would compile nothing and
**every load afterwards would be ~25× slower**. No synthetic benchmark reveals this,
because generation runs on the main thread and compiles the jobs as a side effect.

Fixed by `JobWarmup` / `FormatJobWarmup`, run from `AppBootstrap` on the main thread — a few
milliseconds on 256 points. A regression test fails if the warm-up stops covering a job on
the load path.

### Generic jobs are not Burst-compiled in the editor

`NativeArray.SortJob()` is generic. Per-stage instrumentation of a 2M-point chunk build:

```
bounds 7 ms · morton 4 ms · sort 9117 ms · chunks 8 ms · gather 13 ms
```

The sort was **99.6% of the cost** while every hand-written job compiled and ran in
single-digit milliseconds. `RegisterGenericJobType` recovered only about 20%.

Replaced with an LSD radix sort over the 30-bit Morton code — four passes of eight bits,
ping-ponged so the even pass count lands the result back in place with no final copy.
**9117 ms → 53 ms**, and O(n) rather than O(n log n).

### `Allocator.Temp` and `TempJob` both have thread and lifetime contracts

- `Temp` is thread-local to the main thread and job workers. Using it on a `Task.Run`
  thread throws.
- `TempJob` asserts a 4-frame lifetime, which a background-thread load routinely exceeds —
  producing a wall of leak traces once loading happened inside a real play session.

Every scratch allocation on the load path is now `Persistent`, still disposed
deterministically within the call that made it.

### Sequential stream upload defeats progressive display

The drawable prefix is the **minimum** across attribute streams — drawing a point whose
position has landed but whose colour has not would flash garbage. Uploading one stream at a
time therefore pins that prefix at zero until the first stream finishes.

Uploads now advance every stream over the same point range, which also made the code
simpler.

## Testing approach

Everything is procedural, which makes end-to-end render assertions genuinely achievable: a
known synthetic cloud, a fixed camera, one frame, then assert actual pixels.

The synthetic generator is a **first-class feature, not a test helper**. It is reachable
from the UI, it unblocked the renderer before any parser existed, and it produces
analytically checkable data — on a sphere shell every point is at radius exactly `Scale`,
so the AABB must be exactly ±`Scale`. Every attribute is a pure function of position,
deliberately, because chunk building reorders points and any index-derived value would be
scrambled afterwards.

Notable coverage:

- **Cross-encoding equality** — big-endian PLY must match little-endian bit for bit.
- **sRGB round trip** — mid-grey in, mid-grey out.
- **Leak detection** with stack traces on load and on cancel. Domain reload is disabled in
  this project, so a leaked native allocation survives into the next play session.
- **Resolved UI geometry**, not just element existence. A `min-width: auto` flex default
  clipped cloud names, and a too-small `fixedItemHeight` cut the second line off every row —
  neither is visible to a test that only checks the elements are there.
- **Scene smoke tests** that load `Main.unity` exactly as pressing Play does.

Two environment constraints worth knowing:

- `WaitForEndOfFrame` never resumes under `-batchmode`; it hangs the run rather than failing
  it. Tests drive rendering with an explicit `Camera.Render()`.
- `EditorUtility.DisplayDialog` always answers *cancel* under `-batchmode`, so editor tools
  guard confirmations with `Application.isBatchMode`.
