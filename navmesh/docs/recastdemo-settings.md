# What OpenDAoC-BuildNav's `RecastDemo.exe` builds with

OpenDAoC builds its navmeshes (`zoneNNN.nav`) with [OpenDAoC-BuildNav](https://github.com/OpenDAoC/OpenDAoC-BuildNav).
BuildNav has two stages:
1. C# code reads the client's zone files and writes geometry (`.obj` and `.gset`).
2. A bundled Windows binary, `base/RecastDemo.exe`, turns that geometry into the `.nav` files the
   server loads.

BuildNav's README says the exe is "slightly tweaked" and that its "parameters for navmesh generation
(agent size, etc.) are currently hardcoded and not configurable". The tweaked source isn't
published. That's the only reason the build is Windows-only, and the reason nobody else can
reproduce the meshes.

The settings can be read straight out of the binary. This document records them and how they
were found, so anyone can rebuild the same meshes from upstream source on Linux or macOS.

## Where the exe comes from

- It is the **demo application of [recastnavigation](https://github.com/recastnavigation/recastnavigation)**
  (zlib licence), compiled for x86-64 Windows. It contains no BuildNav code. Its debug-symbols path
  points to a local recastnavigation checkout (`...\recastnavigation\RecastDemo\Bin\RecastDemo.pdb`).
- It was built from **upstream `main` as of late April to early May 2025**, not from a release.
  Two things show this:
  - Its detail meshes carry the edge flags of upstream commit `c393777` ("Make detail mesh edge
    detection more robust", December 2023). That commit isn't in v1.6.0, the last release. A
    v1.6.0 build marks about 4 400 fewer detail edges than the exe on zone 0.
  - Its strings match the demo source of upstream commits from 2025-04-25 (`202c07e`) to
    2025-05-06 (`f25a3c3`) more closely than any other revision. The exe's string table was
    compared with the string literals of every upstream revision of `RecastDemo/Source`. The
    UI was rewritten in July 2025, and strings stop matching from then on.

  Recast and Detour themselves didn't change in that window, so building the libraries at
  `f25a3c3` reproduces the exe.
- It was compiled with **`/fp:fast`**, upstream's own setting for the Windows demo project
  (`RecastDemo/premake5.lua`). That lets the compiler rewrite float expressions, and it did so in
  three places in Recast's detail-mesh code. See [below](#checked-against-the-windows-exe).
- Besides the settings below, it has one **code change**: polygons in the "jump" area (area 5,
  which BuildNav's ladder pads use) get the JUMP flag (`0x08`). Upstream leaves their flags at 0.
  The exe sets it in all three samples' build code:
  `cmp dl,0x5 / jne / mov WORD PTR [rax+rcx*2],0x8`.
- History in the BuildNav repo:
  - Uthgard's original buildnav (2017, [thekroko/uthgard-opensource](https://github.com/thekroko/uthgard-opensource/tree/master/pathing/buildnav))
    shipped its own binary build of the demo, `buildnav.exe`, next to `SDL.dll`. There was no
    source for that either.
  - BuildNav started as a copy of that in February 2025.
  - Commit `b24eff6` (2025-09-19) replaced the binary with a new `RecastDemo.exe` and says:
    "Updated various default properties such as agent radius", "Enabled DT_POLYREF64".
  - Commit `f65010a` (2026-07-17) recompiled it with "a higher agent max slope (70 => 80) and
    MAX_OFFMESH_CONNECTIONS (256 => 4098)".
- The binary examined here comes from BuildNav commit `2911491` (2026-07-18): `base/RecastDemo.exe`,
  479 744 bytes, SHA-256
  `38575027f3e1383f19ad9bc1b8d74ffa30c7f3dc8ad9938e221799aca63a50cf`.

## The settings

Recast works in its own units. OpenDAoC converts game coordinates to Recast as (X, Z, Y) × 1/32
(`Pathing/Detour.Managed/RecastCoords.cs` in OpenDAoC-Core), so one Recast unit is 32 game units.

| Setting (`Sample` field) | Upstream default | BuildNav's exe | In game units |
|---|---|---|---|
| cell size (`m_cellSize`) | 0.3 | **0.1** | 3.2 |
| cell height (`m_cellHeight`) | 0.2 | **0.1** | 3.2 |
| agent height (`m_agentHeight`) | 2.0 | **1.8** | 57.6 |
| agent radius (`m_agentRadius`) | 0.6 | **0.3** | 9.6 |
| agent max climb (`m_agentMaxClimb`) | 0.9 | 0.9 | 28.8 |
| agent max slope (`m_agentMaxSlope`) | 45° | **80°** | |
| region min size (`m_regionMinSize`) | 8 | 8 | |
| region merge size (`m_regionMergeSize`) | 20 | **96** | |
| edge max length (`m_edgeMaxLen`) | 12 | 12 | |
| edge max error (`m_edgeMaxError`) | 1.3 | 1.3 | |
| verts per poly (`m_vertsPerPoly`) | 6 | 6 | |
| detail sample distance (`m_detailSampleDist`) | 6 | **4** | |
| detail sample max error (`m_detailSampleMaxError`) | 1 | 1 | |
| partition type (`m_partitionType`) | watershed | watershed | |
| filters: low-hanging obstacles, ledge spans, low-height spans | on | on | |
| tile size (`Sample_TileMesh::m_tileSize`) | 32 | **96** | 96 cells = 9.6 = 307.2 |
| tile bits / poly bits (`Sample_TileMesh::handleSettings`) | up to 14 / 22 − tile bits | **up to 28 / min(48 − tile bits, 20)** | |
| `InputGeom::MAX_OFFMESH_CONNECTIONS` | 256 | **4096** | |
| flags for "jump" area polygons | 0 | **JUMP (`0x08`)** | |

Notes:
- The off-mesh limit is `0x1000` = 4096 in the binary. That matches BuildNav's own C# constant
  (`NavmeshMgr.RecastOffMeshConnectionLimit = 4096`). The "4098" in the commit message is a typo.
- The tile and poly bits decide `maxTiles` and `maxPolys` in the saved header. Upstream caps a
  navmesh at 2^14 = 16 384 tiles, a limit that comes from 32-bit poly refs. BuildNav's exe allows
  up to 2^28 tiles, so large zones aren't cut off.
- The 64-bit poly refs (`DT_POLYREF64`) aren't visible as a constant. The commit message says
  they're on, and OpenDAoC-Core's Detour is built with them, so the `.nav` tile refs are 64-bit.

## How they were read

Nothing was executed. The binary was disassembled with MinGW's objdump:

```bash
x86_64-w64-mingw32-objdump -d -M intel RecastDemo.exe > recastdemo.asm
grep -n 'DWORD PTR \[rcx+0x38\],0x3e99999a' recastdemo.asm
```

- **The `Sample` settings.** `Sample::resetCommonSettings` is a run of immediate stores to
  consecutive fields. The exe has it as a standalone function at `0x140010c50`, and inlined into the
  `Sample` constructor at `0x14000fee0`:
  ```
  mov DWORD PTR [rcx+0x2c],0x3dcccccd   ; 0.1   m_cellSize
  mov DWORD PTR [rcx+0x30],0x3dcccccd   ; 0.1   m_cellHeight
  mov DWORD PTR [rcx+0x34],0x3fe66666   ; 1.8   m_agentHeight
  mov DWORD PTR [rcx+0x38],0x3e99999a   ; 0.3   m_agentRadius
  mov DWORD PTR [rcx+0x3c],0x3f666666   ; 0.9   m_agentMaxClimb
  mov DWORD PTR [rcx+0x40],0x42a00000   ; 80.0  m_agentMaxSlope
  mov DWORD PTR [rcx+0x44],0x41000000   ; 8.0   m_regionMinSize
  mov DWORD PTR [rcx+0x48],0x42c00000   ; 96.0  m_regionMergeSize
  mov DWORD PTR [rcx+0x4c],0x41400000   ; 12.0  m_edgeMaxLen
  mov DWORD PTR [rcx+0x50],0x3fa66666   ; 1.3   m_edgeMaxError
  mov DWORD PTR [rcx+0x54],0x40c00000   ; 6.0   m_vertsPerPoly
  mov DWORD PTR [rcx+0x58],0x40800000   ; 4.0   m_detailSampleDist
  mov QWORD PTR [rcx+0x5c],0x3f800000   ; 1.0   m_detailSampleMaxError, then 0 = watershed
  ```
  The stores follow the field order of upstream's `Sample.h` exactly: 13 floats, then the int.
  The three filter flags (`[rcx+0x64..0x66] = 1`) are set in the constructor, as upstream does.
- **The tile size.** The `Sample_TileMesh` constructor at `0x1400167c0` calls the `Sample`
  constructor, stores `0x42c00000` (96.0) to `[rbx+0x168]`, then calls `resetCommonSettings`.
  Its `handleSettings`, at `0x140019230`, reads the same field. A first reading took a constructor
  at `0x140013270` (tile size 48) for this one. That constructor belongs to another demo sample, and
  the Windows test below caught the mistake.
- **Tile and poly bits.** In `Sample_TileMesh::handleSettings`:
  - the tile-bit cap is `0x1c` (28), where upstream uses 14;
  - the poly bits are computed as `min(0x30 − tileBits, 0x14)`, that is `min(48 − tileBits, 20)`,
    where upstream uses `22 − tileBits`.
- **The off-mesh limit.** `loadGeomSet` passes `0x1000` as the `%d` of its
  "MAX_OFFMESH_CONNECTIONS (%d) reached" message.

## The command-line path

BuildNav runs the exe as `RecastDemo.exe <file>.gset <file>.nav`. In `main`, at `0x140021f30`, an
argument count of exactly 3 (the program name plus two arguments) branches to a headless function
at `0x140021d20`. That function:
1. creates the `Sample_TileMesh`, whose constructor applies the settings above;
2. creates an `InputGeom` and loads the `.gset`;
3. calls `handleMeshChanged`, then `handleSettings`, then `handleBuild` (vtable slots `+0x48`,
   `+0x08`, `+0x50`);
4. calls `Sample::saveAll` with the second argument.

Nothing in it changes a setting. A `.gset` file could override them with an `s` line, but
BuildNav doesn't write one. So the table above is the complete set.

`handleBuild` and Detour's `dtNavMesh::init` in the exe are unmodified upstream code.

## Checked against the Windows exe

`RecastDemo.exe` (Windows) and `gset2nav` (macOS, Apple Silicon) were each run on the same two
`.gset` files:
- a small scene with a floor, a wall, a ramp onto a platform, a door volume and an off-mesh link
  (25 tiles);
- a flat 700 × 700 floor (73 × 73 = 5 329 tiles).

Results:
- **The headers match.** The tile size is 9.6, so 96 cells. Agent height, radius and climb are
  1.8, 0.3 and 0.9. The cell size is 0.1. `maxTiles` and `maxPolys` are 32 and 2^20 for the scene,
  and 8 192 and 2^20 for the floor, as the formulas above give.
- **Every tile is byte-identical.** The whole files are identical apart from the 4 padding bytes
  in each 16-byte tile record. The exe leaves those uninitialised and the server never reads them.
- **This needed one fix.** Recast and Detour sort with `qsort`, and some comparisons have ties.
  Each C library leaves ties in its own order. With Apple's `qsort`, 10 of the scene's 25 tiles
  had a differently ordered BV tree. That is the per-tile search index; the polygons and links
  were the same. `gset2nav` therefore uses a `qsort` that orders ties the way Microsoft's runtime
  does (`gset2nav/src/msvc_qsort.cpp`). With it, all tiles match.

### A real zone: Camelot Hills (zone 0)

BuildNav, ported to macOS, exported zone 0 from the retail client. That gave 960 536 vertices and
827 702 triangles, plus the ladder pass's 174 off-mesh links and 155 volumes. `RecastDemo.exe`
(Windows) and `gset2nav` (macOS) then each built that same `.gset`. Both produced 45 796 tiles with
the same header.

The first comparison used the libraries at v1.6.0 and found **935 tiles (2 %) different**. Each
difference was traced in the exe's disassembly, and `gset2nav` was changed to match:

| Cause | Tiles | What the exe does | `gset2nav` |
|---|---|---|---|
| Library version, mostly the detail-edge boundary flags | 871 | Upstream `main`'s hull-based test (`c393777`); v1.6.0's 0.001-unit distance test misses edges at zone-scale coordinates | builds Recast/Detour at `f25a3c3` |
| Polygon flags in the "jump" area | 46 | sets JUMP (`0x08`) | does the same |
| Samples along tile-border edges | 7 | `/fp:fast` turned `d / sampleDist` into `d * (1 / sampleDist)`. Border edges are exactly 9.6 = 24 × 0.4 long, so the sample count rounds differently. | patched to the same expression |
| One more or one fewer interior detail sample | 11 | `/fp:fast` turned the barycentric `x * (1 / denom)` into `x / denom`, and `u + v` into `(unum + vnum) / denom`. Detail heights are multiples of 0.1, and so is the error threshold, so this decides ties. | patched to the same expressions |

`/fp:fast` also folded the sample jitter, `(j / 65535 × 2 − 1) × cs × 0.1`, into
`(j × 3.0518045e−6 − 0.1) × cs`. That makes no difference on zone 0, but `gset2nav` uses the
exe's form anyway. The three expressions are in `gset2nav/patches/recast-detail-as-recastdemo-exe.patch`.
Only the parts of `buildPolyDetail` involved in these differences were read in the disassembly.
Other `/fp:fast` rewrites may exist in code that zone 0 doesn't exercise. Comparing more zones
would find them, and the fix would be the same kind of change.

With those changes, **all 45 796 tiles are byte-identical**. The whole 33 MB file is identical
apart from the per-tile padding bytes, and the server's `LoadNavMesh` loads every tile.

Five more zones were then compared the same way, chosen to cover each kind of zone BuildNav
exports: 100 (outdoor, Midgard), 120 (Jordheim, a capital city), 066 (Albion's Underground Forest,
a sky city), 244 (Passage of Conflict, a large dungeon) and 302 (Damp Cavern, a small dungeon).
All five are tile-for-tile identical, with no further changes needed.

Not covered yet: stage 1, the C# geometry export. The exe was given the macOS export each time,
so these comparisons test stage 2 only.

## What this means

The Windows binary isn't needed. Upstream recastnavigation gives the same build when:
- it's taken at `f25a3c3` (or anywhere from 2025-04-25 to 2025-05-06), with
  `DT_POLYREF64`;
- it uses the settings and tile and poly bits above, and flags "jump" polygons as JUMP;
- its off-mesh limit is raised to 4096;
- three detail-mesh expressions are written the way MSVC's `/fp:fast` compiled them;
- it's compiled without fused multiply-add (`-ffp-contract=off`);
- ties in its sorts come out in the Microsoft runtime's order;
- it's driven by a small command-line program that loads the `.gset`, builds every tile, and
  saves the standard "MSET" tile set.

That builds on Linux and macOS as well as Windows. In this repository that program is
[`gset2nav`](../gset2nav/). The [navmesh README](../README.md) covers the whole process.
