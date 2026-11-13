# Navmeshes for OpenDAoC, without Windows

**Symptoms:** mobs roam through walls, walk into hills, or climb into the air on slopes.

**Cause:** the server has no navmesh for the zone. Without one, OpenDAoC has no ground data at all.
A roaming NPC picks a random point at its spawn height and walks straight to it. With a navmesh
(Recast/Detour), roaming and pathing follow the real terrain.

Navmeshes are optional, so a server starts and runs without them. Nothing tells you clearly that
they're missing:
- A missing native Detour library shows up as one `DllNotFoundException` at startup.
- A missing `.nav` file is logged only at Debug level.
- A `.nav` file in an old format logs "Loading NavMesh successful" and loads no tiles at all.

The usual tool, [OpenDAoC-BuildNav](https://github.com/OpenDAoC/OpenDAoC-BuildNav), only runs on
Windows. It hands the final step to a bundled `RecastDemo.exe`. This folder builds the same
navmeshes on Linux and macOS, from the game client's files. Nothing here needs Windows.

## Contents

| Path | What it is |
|---|---|
| [`buildnav/`](buildnav/) | OpenDAoC-BuildNav (GPL-3.0, upstream commit `2911491`), ported to plain .NET 10. It reads the client's zone files and writes geometry, then calls `gset2nav`. |
| [`gset2nav/`](gset2nav/) | Replaces `RecastDemo.exe`: turns BuildNav's geometry into `zoneNNN.nav`. C++ on upstream [recastnavigation](https://github.com/recastnavigation/recastnavigation) (zlib), pinned to the commit the exe was built from, plus a three-line patch. |
| [`tools/navcheck.py`](tools/navcheck.py) | Checks `.nav` files and compares two tile by tile. Python 3, no dependencies. |
| [`tools/navload/`](tools/navload/) | Loads a `.nav` with the server's own Detour code and queries it: "will my server load this?" |
| [`docs/recastdemo-settings.md`](docs/recastdemo-settings.md) | The settings hidden in BuildNav's `RecastDemo.exe`, and how they were found and checked. |

## Status

| Piece | State |
|---|---|
| `gset2nav` | Built and tested on macOS (Apple Silicon) and Linux (x86-64, GCC 13). **Its output is byte-identical to BuildNav's `RecastDemo.exe` on Windows for the same input**, apart from unused padding. Tested on six real zones (outdoor zones in two realms, a capital city, a sky city, a small and a large dungeon; ladder links included) and on two synthetic scenes. Output loads fully with the server's `LoadNavMesh`. Single- and multi-threaded output is identical. |
| `navcheck.py`, `navload` | Tested on good, truncated, wrong-magic and old 32-bit files. |
| `buildnav` port | Run on every zone of a retail client (`--all`, 448 `.nav` files) on macOS (Apple Silicon, client on a network share, about 65 minutes on 8 cores) and on Linux (x86-64, GCC, client on local disk, 28 minutes on 32 cores). The two sets are byte-identical. Every file passes `navcheck` and loads every tile with the server's `LoadNavMesh`. Zones rebuilt one at a time match the full run. |
| Real zones match the Windows build | **Stage 2 matches on all six zones compared**: 0 (Camelot Hills), 100 (outdoor, Midgard), 120 (Jordheim), 066 (a sky city), 244 and 302 (dungeons). `RecastDemo.exe` and `gset2nav` (on macOS and on Linux) built the same `.gset` files into tile-for-tile identical meshes. Not compared yet: stage 1 (the geometry export, which would need stock BuildNav on Windows). |

The steps below have been run end to end, for every zone of a retail client, on macOS and on Linux.

## 1. Check what your server has now

From the server's folder (the one holding `CoreServer.dll` or `CoreServer.exe`):

```bash
ls lib/ | grep -i detour          # Detour.Managed.dll alone isn't enough: libDetour.so (Linux/macOS) or Detour.dll (Windows)
ls pathing/ navmesh/ 2>/dev/null  # one zoneNNN.nav per zone
grep -i navmesh <server log> | head  # "Loading NavMesh successful for zone N" per zone
```

If you already have `.nav` files, check them:

```bash
python3 navmesh/tools/navcheck.py pathing/*.nav
```

- `32-bit tile refs` means an old file, built before September 2025. Current servers load **none**
  of its tiles, even though the log says "successful". Rebuild it.

## 2. Install the native Detour library

The server's C# code calls a native library built from OpenDAoC-Core's `Pathing/Detour` folder.
Build it on the machine, or the same OS and CPU, that runs the server:

```bash
cd OpenDAoC-Core/Pathing/Detour
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

Copy the result into the server's `lib/` folder:

| Server OS | Built file | Copy to |
|---|---|---|
| Linux | `build/libDetour.so` | `lib/libDetour.so` |
| macOS | `build/libDetour.dylib` | `lib/libDetour.so` (the server only looks for `.so`; the extension doesn't matter to the OS loader) |
| Windows | `build/Release/Detour.dll` | `lib/Detour.dll` |

Use `cp -L`: on Linux and macOS the built file is a symlink (`libDetour.so -> libDetour.so.VERSION`).
On macOS, the renamed `.dylib` was checked to load and read a navmesh.

## 3. Build the tools

You need:
- the [.NET 10 SDK](https://dotnet.microsoft.com/download);
- CMake 3.16 or newer;
- a C++ compiler (`build-essential` on Debian/Ubuntu, Xcode command line tools on macOS);
- Python 3;
- git;
- an OpenDAoC-Core checkout.

```bash
# gset2nav: fetches recastnavigation, applies gset2nav/patches/, builds with 64-bit poly refs
cmake -S navmesh/gset2nav -B build/gset2nav -DCMAKE_BUILD_TYPE=Release
cmake --build build/gset2nav

# BuildNav (needs OpenDAoC-Core for its Detour.Managed project)
dotnet build navmesh/buildnav -c Release -p:CoreDir=/path/to/OpenDAoC-Core

# optional: navload, to test files with the server's own loader
cmake -S navmesh/tools/navload -B build/navload -DOPENDAOC_CORE=/path/to/OpenDAoC-Core
cmake --build build/navload
```

BuildNav uses native Detour too, for placing ladders. Build a copy of the library for it with
`-ffp-contract=off`, and put it in a `base/lib/` folder next to the `OpenDAoC-BuildNav` program:

```bash
cmake -S /path/to/OpenDAoC-Core/Pathing/Detour -B build/detour -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_CXX_FLAGS=-ffp-contract=off
cmake --build build/detour
mkdir -p navmesh/buildnav/bin/Release/net10.0/base/lib
cp -L build/detour/libDetour.so navmesh/buildnav/bin/Release/net10.0/base/lib/      # Linux
cp -L build/detour/libDetour.dylib navmesh/buildnav/bin/Release/net10.0/base/lib/libDetour.so  # macOS
```

Without the flag, Clang on Apple Silicon fuses multiply-adds, which now and then moves a ladder
endpoint by a tenth of a unit. With it, macOS and Linux builds are byte-identical.

The library and `CoreDir` must come from the same OpenDAoC-Core checkout: if you update one,
rebuild the other. Otherwise the ladder pass fails with `Unable to find an entry point named ...`
and the zone is built without ladder links.

On macOS, if CMake stops with `tapi error: malformed file ... unknown architecture` or asks you to
agree to the Xcode licence, build with the Command Line Tools instead of Xcode. Put
`DEVELOPER_DIR=/Library/Developer/CommandLineTools` in front of both `cmake` commands, using a new
build folder.

## 4. Build the navmeshes

You need the game client's files: the `zones/` folder (about 2.4 GB) and the files beside it. Use
the client version your players use. Meshes built from an older client miss newer geometry, for
example the "New Towns" areas. The client folder is only read, never written.

```bash
cd navmesh/buildnav/bin/Release/net10.0
./OpenDAoC-BuildNav --daoc="/path/to/Dark Age of Camelot" \
    --recast=/path/to/build/gset2nav/gset2nav --zones=0,100,200
```

- If .NET is installed in your home folder (`dotnet-install.sh`, e.g. `~/.dotnet`) rather than
  system-wide, the program stops with "You must install .NET to run this application". Set
  `DOTNET_ROOT=~/.dotnet` first.
- `--zones=` takes a list of zone IDs, `--regions=` a list of region IDs, and `--all` builds
  every zone. Start with a few zones. A full build takes hours and a lot of memory.
- `--obj` stops after the geometry (`.obj`, `.gset`, `.ladders.json`). To build the navmesh from
  those by hand, run `gset2nav zones/zone000.gset zone000.nav`. A mesh built this way
  has no ladder links, which BuildNav adds in a second pass.

The `.nav` files are written to `base/zones/` under the BuildNav build folder.

## 5. Check and install

```bash
python3 navmesh/tools/navcheck.py base/zones/*.nav                  # format, tile counts, agent settings
build/navload/navload base/zones/zone000.nav <x> <y> <z>   # load with the server's code; optional game coordinates to query
```

Copy the files into a `pathing/` folder (or `navmesh/`) in the server's folder:

```bash
mkdir -p /path/to/server/pathing
cp base/zones/zone*.nav /path/to/server/pathing/
```

BuildNav skips the Trials of Atlantis zones 30–47 and 130–147, which the client marks as proxies
of zones 73–90 (same geometry, same position in their region). The server still looks for their
own files, so give them copies:

```bash
cd /path/to/server/pathing
for p in $(seq 30 47) $(seq 130 147); do
  cp "$(printf zone%03d.nav $((p % 100 + 43)))" "$(printf zone%03d.nav $p)"
done
```

Each proxy zone has the same region offset and size as its source in the client's `zones.dat` and
in the server's default `Zones` data, which is what makes the copy valid. If your server's `Zones`
table has been changed, check those first.

Restart the server and look for `Loading NavMesh successful for zone N`. The upstream docs say a
server holding every zone's mesh needs at least 5 GB of RAM. Then watch a few roaming mobs in a
zone you built.

## How this was worked out

[`docs/recastdemo-settings.md`](docs/recastdemo-settings.md) covers it in full:
- where `RecastDemo.exe` comes from;
- the settings compiled into it, read from the binary with objdump (nothing was run);
- what its command-line mode does;
- why large zones lose tiles.

## Licences

| Part | Licence |
|---|---|
| `buildnav/` | GPL-3.0, from OpenDAoC-BuildNav and Uthgard's buildnav |
| `MinAreaRect.cs` | a port of OpenCV code (Apache-2.0) |
| `gset2nav/` | derived from recastnavigation's RecastDemo, zlib licence, with the original notices kept |
| everything else in this repository | GPL-3.0-or-later |

No client files or extracted geometry are included.
