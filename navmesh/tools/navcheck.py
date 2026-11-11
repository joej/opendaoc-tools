#!/usr/bin/env python3
"""Check and compare OpenDAoC navmesh files (.nav).

  navcheck.py zone000.nav [more.nav ...]     validate and summarise
  navcheck.py --compare a.nav b.nav          compare two files tile by tile

A .nav file is Recast/Detour's "MSET" tile set as RecastDemo saves it, with 64-bit tile refs,
which is what OpenDAoC-Core's LoadNavMesh (Pathing/Detour/Source/DetourInterface.cpp) reads:

  header  int32 magic 'MSET', int32 version 1, int32 numTiles,
          dtNavMeshParams { float orig[3], tileWidth, tileHeight; int32 maxTiles, maxPolys }
  tiles   uint64 tileRef, int32 dataSize, 4 bytes padding, then dataSize bytes of Detour tile data
          (dtMeshHeader: magic 'DNAV', version 7, ...)

The loader stops at the first tile with ref 0 or size 0. Exit status: 0 ok, 1 problems found.
Standard library only.
"""

import struct
import sys

MSET = 0x4D534554  # 'MSET'
DNAV = 0x444E4156  # 'DNAV'
SET_HEADER = struct.Struct("<iii3fffii")      # 40 bytes
TILE_HEADER = struct.Struct("<Qi4x")           # 16 bytes (x64 padding)
TILE_HEADER_32 = struct.Struct("<Ii")          # 8 bytes: older files with 32-bit tile refs
# dtMeshHeader (v7): magic, version, x, y, layer, userId, polyCount, vertCount, maxLinkCount,
# detailMeshCount, detailVertCount, detailTriCount, bvNodeCount, offMeshConCount, offMeshBase,
# walkableHeight, walkableRadius, walkableClimb, bmin[3], bmax[3], bvQuantFactor
MESH_HEADER = struct.Struct("<iiiiiIiiiiiiiiifff3f3ff")  # 100 bytes
MESH_FIELDS = ("magic", "version", "x", "y", "layer", "userId", "polyCount", "vertCount",
               "maxLinkCount", "detailMeshCount", "detailVertCount", "detailTriCount",
               "bvNodeCount", "offMeshConCount", "offMeshBase", "walkableHeight",
               "walkableRadius", "walkableClimb", "bmin", "bmax", "bvQuantFactor")

GAME_UNITS = 32.0  # one Recast unit = 32 game units (OpenDAoC RecastCoords)


def parse_mesh_header(data):
    v = MESH_HEADER.unpack_from(data, 0)
    h = dict(zip(MESH_FIELDS[:18], v[:18]))
    h["bmin"] = v[18:21]
    h["bmax"] = v[21:24]
    h["bvQuantFactor"] = v[24]
    return h


def read_nav(path):
    """Return (params, tiles, problems). tiles: list of (ref, data, header-or-None)."""
    problems = []
    with open(path, "rb") as f:
        buf = f.read()
    if len(buf) < SET_HEADER.size:
        return None, [], ["file is %d bytes, shorter than the 40-byte header" % len(buf)]
    magic, version, num_tiles, ox, oy, oz, tw, th, max_tiles, max_polys = SET_HEADER.unpack_from(buf, 0)
    params = dict(orig=(ox, oy, oz), tileWidth=tw, tileHeight=th, maxTiles=max_tiles,
                  maxPolys=max_polys, numTiles=num_tiles)
    if magic != MSET:
        problems.append("bad magic 0x%08x (expected 'MSET')" % magic)
        return params, [], problems
    if version != 1:
        problems.append("set version %d (expected 1)" % version)
    if num_tiles > max_tiles:
        problems.append("numTiles %d > maxTiles %d" % (num_tiles, max_tiles))

    # Files from before September 2025 (Uthgard's buildnav, early OpenDAoC-BuildNav) use 32-bit
    # tile refs: an 8-byte tile record instead of 16. Recognise them by where the tile data starts.
    tile_header = TILE_HEADER
    off = SET_HEADER.size
    if num_tiles > 0 and len(buf) >= off + 12:
        if struct.unpack_from("<i", buf, off + 8)[0] == DNAV and struct.unpack_from("<i", buf, off + 16)[0] != DNAV:
            tile_header = TILE_HEADER_32
            params["tileRefBits"] = 32
            problems.append("32-bit tile refs: built for a Detour without DT_POLYREF64. Current OpenDAoC "
                            "servers use 64-bit refs and cannot load this file; rebuild it")
        else:
            params["tileRefBits"] = 64

    tiles = []
    for i in range(num_tiles):
        if off + tile_header.size > len(buf):
            problems.append("file ends inside tile %d's header" % i)
            break
        ref, size = tile_header.unpack_from(buf, off)
        off += tile_header.size
        if ref == 0 or size == 0:
            problems.append("tile %d has ref %d, size %d: the server stops loading here" % (i, ref, size))
            break
        if off + size > len(buf):
            problems.append("tile %d: %d bytes declared, file ends after %d" % (i, size, len(buf) - off))
            break
        data = buf[off:off + size]
        off += size
        hdr = None
        if size < MESH_HEADER.size:
            problems.append("tile %d: %d bytes, too small for a Detour header" % (i, size))
        else:
            hdr = parse_mesh_header(data)
            if hdr["magic"] != DNAV:
                problems.append("tile %d: bad tile magic 0x%08x (expected 'DNAV')" % (i, hdr["magic"]))
            elif hdr["version"] != 7:
                problems.append("tile %d: tile version %d (the server's Detour expects 7)" % (i, hdr["version"]))
        tiles.append((ref, data, hdr))
    if off != len(buf) and not problems:
        problems.append("%d unexpected bytes after the last tile" % (len(buf) - off))

    seen = {}
    for i, (ref, _, hdr) in enumerate(tiles):
        if hdr is None:
            continue
        key = (hdr["x"], hdr["y"], hdr["layer"])
        if key in seen:
            problems.append("tiles %d and %d are both at x=%d y=%d layer=%d" % (seen[key], i, *key))
        seen[key] = i
    return params, tiles, problems


def summarise(path):
    params, tiles, problems = read_nav(path)
    print(path)
    if params is not None and "orig" in params:
        o = params["orig"]
        print("  origin (Recast) %.2f %.2f %.2f   tile %.2f x %.2f   maxTiles %d   maxPolys %d"
              % (o[0], o[1], o[2], params["tileWidth"], params["tileHeight"], params["maxTiles"], params["maxPolys"]))
    hdrs = [h for _, _, h in tiles if h]
    if hdrs:
        polys = sum(h["polyCount"] for h in hdrs)
        offmesh = sum(h["offMeshConCount"] for h in hdrs)
        xs = [h["x"] for h in hdrs]
        ys = [h["y"] for h in hdrs]
        h0 = hdrs[0]
        cs = 1.0 / h0["bvQuantFactor"] if h0["bvQuantFactor"] else 0.0
        print("  %d tiles (x %d..%d, y %d..%d), %d polygons, %d off-mesh links, %d bytes of tile data"
              % (len(hdrs), min(xs), max(xs), min(ys), max(ys), polys, offmesh, sum(len(d) for _, d, _ in tiles)))
        print("  agent height %.2f radius %.2f climb %.2f, cell size %.3f  (game units: %.1f / %.1f / %.1f / %.1f)"
              % (h0["walkableHeight"], h0["walkableRadius"], h0["walkableClimb"], cs,
                 h0["walkableHeight"] * GAME_UNITS, h0["walkableRadius"] * GAME_UNITS,
                 h0["walkableClimb"] * GAME_UNITS, cs * GAME_UNITS))
    elif not problems:
        print("  no tiles")
    for p in problems:
        print("  PROBLEM: " + p)
    return not problems


def compare(a, b):
    pa, ta, qa = read_nav(a)
    pb, tb, qb = read_nav(b)
    ok = True
    for path, q in ((a, qa), (b, qb)):
        for p in q:
            print("%s: PROBLEM: %s" % (path, p))
            ok = False
    if pa != pb:
        print("header params differ:\n  %s\n  %s" % (pa, pb))
        ok = False

    def by_pos(tiles):
        return {(h["x"], h["y"], h["layer"]): (ref, d) for ref, d, h in tiles if h}

    ma, mb = by_pos(ta), by_pos(tb)
    only_a = sorted(set(ma) - set(mb))
    only_b = sorted(set(mb) - set(ma))
    differ = sorted(k for k in set(ma) & set(mb) if ma[k][1] != mb[k][1])
    ref_differ = sorted(k for k in set(ma) & set(mb) if ma[k][0] != mb[k][0])
    same = len(set(ma) & set(mb)) - len(differ)
    print("%d tiles identical, %d differ, %d only in %s, %d only in %s, %d with a different tile ref"
          % (same, len(differ), len(only_a), a, len(only_b), b, len(ref_differ)))
    for label, keys in (("differ", differ), ("only in " + a, only_a), ("only in " + b, only_b)):
        if keys:
            shown = ", ".join("%d,%d" % (x, y) for x, y, _ in keys[:20])
            print("  %s: %s%s" % (label, shown, " ..." if len(keys) > 20 else ""))
    if differ or only_a or only_b or ref_differ:
        ok = False
    if ok:
        print("files are equivalent tile for tile")
    return ok


def main(argv):
    if len(argv) >= 2 and argv[1] in ("-h", "--help"):
        print(__doc__)
        return 0
    if len(argv) == 4 and argv[1] == "--compare":
        return 0 if compare(argv[2], argv[3]) else 1
    if len(argv) < 2 or argv[1].startswith("-"):
        print(__doc__, file=sys.stderr)
        return 2
    ok = True
    for path in argv[1:]:
        ok = summarise(path) and ok
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
