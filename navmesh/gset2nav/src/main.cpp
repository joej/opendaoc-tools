// gset2nav: build an OpenDAoC navmesh (.nav) from a Recast geometry set (.gset).
//
// A headless replacement for the RecastDemo.exe bundled with OpenDAoC-BuildNav. It does what that
// exe does when given two arguments (load the .gset, apply the settings, build every tile, save
// the tile set), with the same settings, on any platform. See ../docs/recastdemo-settings.md.
//
// The tile build and save code follow recastnavigation's RecastDemo (Sample_TileMesh.cpp,
// Sample.cpp; zlib licence, Copyright (c) 2009-2010 Mikko Mononen memon@inside.org). Altered from
// the original: no GUI; tiles may be built on several threads (they are still added to the
// navmesh in the original order, so the output is the same); tiles that fail to add are counted;
// the tile/poly bit split and the JUMP flag for "jump" area polygons are as in BuildNav's
// RecastDemo.exe, not upstream.

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <atomic>
#include <chrono>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "ChunkyTriMesh.h"
#include "DetourNavMesh.h"
#include "DetourNavMeshBuilder.h"
#include "GeomSet.h"
#include "MeshLoaderObj.h"
#include "Recast.h"

// ---------------------------------------------------------------------------------------------
// Settings. Defaults are the values compiled into OpenDAoC-BuildNav's RecastDemo.exe
// (Sample::resetCommonSettings and the Sample_TileMesh constructor at 0x1400167c0), in Recast units
// (1 Recast unit = 32 game units).

struct Settings
{
	float cellSize = 0.1f;
	float cellHeight = 0.1f;
	float agentHeight = 1.8f;
	float agentRadius = 0.3f;
	float agentMaxClimb = 0.9f;
	float agentMaxSlope = 80.0f;
	float regionMinSize = 8.0f;
	float regionMergeSize = 96.0f;
	float edgeMaxLen = 12.0f;
	float edgeMaxError = 1.3f;
	float vertsPerPoly = 6.0f;
	float detailSampleDist = 4.0f;
	float detailSampleMaxError = 1.0f;
	int partitionType = 0; // 0 watershed, 1 monotone, 2 layers
	float tileSize = 96.0f;
	bool filterLowHangingObstacles = true;
	bool filterLedgeSpans = true;
	bool filterWalkableLowHeightSpans = true;
};

enum PolyAreas { AREA_GROUND, AREA_WATER, AREA_ROAD, AREA_DOOR, AREA_GRASS, AREA_JUMP };
enum PolyFlags { FLAG_WALK = 0x01, FLAG_SWIM = 0x02, FLAG_DOOR = 0x04, FLAG_JUMP = 0x08, FLAG_DISABLED = 0x10 };

// ---------------------------------------------------------------------------------------------

static std::atomic<int> g_recastWarnings(0);

class Context : public rcContext
{
public:
	explicit Context(bool verbose) : rcContext(true), m_verbose(verbose) {}

protected:
	void doLog(const rcLogCategory category, const char* msg, const int /*len*/) override
	{
		if (category == RC_LOG_PROGRESS && !m_verbose)
			return;
		if (category == RC_LOG_WARNING)
		{
			// Routine on real terrain (e.g. "delaunayHull: Removing dangling face"); RecastDemo
			// doesn't show them either. Counted, and listed with -v.
			g_recastWarnings++;
			if (!m_verbose)
				return;
		}
		static std::mutex mtx;
		std::lock_guard<std::mutex> lock(mtx);
		fprintf(category == RC_LOG_PROGRESS ? stdout : stderr, "%s%s\n",
				category == RC_LOG_ERROR ? "error: " : category == RC_LOG_WARNING ? "warning: " : "", msg);
	}

private:
	bool m_verbose;
};

inline unsigned int nextPow2(unsigned int v)
{
	v--;
	v |= v >> 1;
	v |= v >> 2;
	v |= v >> 4;
	v |= v >> 8;
	v |= v >> 16;
	v++;
	return v;
}

inline unsigned int ilog2(unsigned int v)
{
	unsigned int r;
	unsigned int shift;
	r = (v > 0xffff) << 4; v >>= r;
	shift = (v > 0xff) << 3; v >>= shift; r |= shift;
	shift = (v > 0xf) << 2; v >>= shift; r |= shift;
	shift = (v > 0x3) << 1; v >>= shift; r |= shift;
	r |= (v >> 1);
	return r;
}

// Sample_TileMesh::buildTileMesh, with the intermediate results kept in locals.
static unsigned char* buildTileMesh(rcContext* ctx, const Settings& s, const GeomSet& geom,
									const int tx, const int ty, const float* bmin, const float* bmax, int& dataSize)
{
	dataSize = 0;
	const float* verts = geom.getMesh()->getVerts();
	const int nverts = geom.getMesh()->getVertCount();
	const rcChunkyTriMesh* chunkyMesh = geom.getChunkyMesh();

	rcConfig cfg;
	memset(&cfg, 0, sizeof(cfg));
	cfg.cs = s.cellSize;
	cfg.ch = s.cellHeight;
	cfg.walkableSlopeAngle = s.agentMaxSlope;
	cfg.walkableHeight = (int)ceilf(s.agentHeight / cfg.ch);
	cfg.walkableClimb = (int)floorf(s.agentMaxClimb / cfg.ch);
	cfg.walkableRadius = (int)ceilf(s.agentRadius / cfg.cs);
	cfg.maxEdgeLen = (int)(s.edgeMaxLen / s.cellSize);
	cfg.maxSimplificationError = s.edgeMaxError;
	cfg.minRegionArea = (int)rcSqr(s.regionMinSize);
	cfg.mergeRegionArea = (int)rcSqr(s.regionMergeSize);
	cfg.maxVertsPerPoly = (int)s.vertsPerPoly;
	cfg.tileSize = (int)s.tileSize;
	cfg.borderSize = cfg.walkableRadius + 3;
	cfg.width = cfg.tileSize + cfg.borderSize * 2;
	cfg.height = cfg.tileSize + cfg.borderSize * 2;
	cfg.detailSampleDist = s.detailSampleDist < 0.9f ? 0 : s.cellSize * s.detailSampleDist;
	cfg.detailSampleMaxError = s.cellHeight * s.detailSampleMaxError;

	rcVcopy(cfg.bmin, bmin);
	rcVcopy(cfg.bmax, bmax);
	cfg.bmin[0] -= cfg.borderSize * cfg.cs;
	cfg.bmin[2] -= cfg.borderSize * cfg.cs;
	cfg.bmax[0] += cfg.borderSize * cfg.cs;
	cfg.bmax[2] += cfg.borderSize * cfg.cs;

	struct Scratch
	{
		rcHeightfield* solid = 0;
		unsigned char* triareas = 0;
		rcCompactHeightfield* chf = 0;
		rcContourSet* cset = 0;
		rcPolyMesh* pmesh = 0;
		rcPolyMeshDetail* dmesh = 0;
		~Scratch()
		{
			rcFreeHeightField(solid);
			delete[] triareas;
			rcFreeCompactHeightfield(chf);
			rcFreeContourSet(cset);
			rcFreePolyMesh(pmesh);
			rcFreePolyMeshDetail(dmesh);
		}
	} w;

	w.solid = rcAllocHeightfield();
	if (!w.solid || !rcCreateHeightfield(ctx, *w.solid, cfg.width, cfg.height, cfg.bmin, cfg.bmax, cfg.cs, cfg.ch))
	{
		ctx->log(RC_LOG_ERROR, "tile %d,%d: could not create solid heightfield", tx, ty);
		return 0;
	}

	w.triareas = new unsigned char[chunkyMesh->maxTrisPerChunk];

	float tbmin[2], tbmax[2];
	tbmin[0] = cfg.bmin[0];
	tbmin[1] = cfg.bmin[2];
	tbmax[0] = cfg.bmax[0];
	tbmax[1] = cfg.bmax[2];
	int cid[512]; // As RecastDemo: at most 512 chunks per tile.
	const int ncid = chunkyMesh->GetChunksOverlappingRect(tbmin, tbmax, cid, 512);
	if (!ncid)
		return 0;

	for (int i = 0; i < ncid; ++i)
	{
		const rcChunkyTriMeshNode& node = chunkyMesh->nodes[cid[i]];
		const int* ctris = &chunkyMesh->tris[node.i * 3];
		const int nctris = node.n;

		memset(w.triareas, 0, nctris * sizeof(unsigned char));
		rcMarkWalkableTriangles(ctx, cfg.walkableSlopeAngle, verts, nverts, ctris, nctris, w.triareas);
		if (!rcRasterizeTriangles(ctx, verts, nverts, ctris, w.triareas, nctris, *w.solid, cfg.walkableClimb))
			return 0;
	}

	delete[] w.triareas;
	w.triareas = 0;

	if (s.filterLowHangingObstacles)
		rcFilterLowHangingWalkableObstacles(ctx, cfg.walkableClimb, *w.solid);
	if (s.filterLedgeSpans)
		rcFilterLedgeSpans(ctx, cfg.walkableHeight, cfg.walkableClimb, *w.solid);
	if (s.filterWalkableLowHeightSpans)
		rcFilterWalkableLowHeightSpans(ctx, cfg.walkableHeight, *w.solid);

	w.chf = rcAllocCompactHeightfield();
	if (!w.chf || !rcBuildCompactHeightfield(ctx, cfg.walkableHeight, cfg.walkableClimb, *w.solid, *w.chf))
	{
		ctx->log(RC_LOG_ERROR, "tile %d,%d: could not build compact data", tx, ty);
		return 0;
	}
	rcFreeHeightField(w.solid);
	w.solid = 0;

	if (!rcErodeWalkableArea(ctx, cfg.walkableRadius, *w.chf))
	{
		ctx->log(RC_LOG_ERROR, "tile %d,%d: could not erode", tx, ty);
		return 0;
	}

	const ConvexVolume* vols = geom.getConvexVolumes();
	for (int i = 0; i < geom.getConvexVolumeCount(); ++i)
		rcMarkConvexPolyArea(ctx, vols[i].verts, vols[i].nverts, vols[i].hmin, vols[i].hmax, (unsigned char)vols[i].area, *w.chf);

	if (s.partitionType == 0)
	{
		if (!rcBuildDistanceField(ctx, *w.chf) ||
			!rcBuildRegions(ctx, *w.chf, cfg.borderSize, cfg.minRegionArea, cfg.mergeRegionArea))
		{
			ctx->log(RC_LOG_ERROR, "tile %d,%d: could not build watershed regions", tx, ty);
			return 0;
		}
	}
	else if (s.partitionType == 1)
	{
		if (!rcBuildRegionsMonotone(ctx, *w.chf, cfg.borderSize, cfg.minRegionArea, cfg.mergeRegionArea))
		{
			ctx->log(RC_LOG_ERROR, "tile %d,%d: could not build monotone regions", tx, ty);
			return 0;
		}
	}
	else
	{
		if (!rcBuildLayerRegions(ctx, *w.chf, cfg.borderSize, cfg.minRegionArea))
		{
			ctx->log(RC_LOG_ERROR, "tile %d,%d: could not build layer regions", tx, ty);
			return 0;
		}
	}

	w.cset = rcAllocContourSet();
	if (!w.cset || !rcBuildContours(ctx, *w.chf, cfg.maxSimplificationError, cfg.maxEdgeLen, *w.cset))
	{
		ctx->log(RC_LOG_ERROR, "tile %d,%d: could not create contours", tx, ty);
		return 0;
	}
	if (w.cset->nconts == 0)
		return 0;

	w.pmesh = rcAllocPolyMesh();
	if (!w.pmesh || !rcBuildPolyMesh(ctx, *w.cset, cfg.maxVertsPerPoly, *w.pmesh))
	{
		ctx->log(RC_LOG_ERROR, "tile %d,%d: could not triangulate contours", tx, ty);
		return 0;
	}

	w.dmesh = rcAllocPolyMeshDetail();
	if (!w.dmesh || !rcBuildPolyMeshDetail(ctx, *w.pmesh, *w.chf, cfg.detailSampleDist, cfg.detailSampleMaxError, *w.dmesh))
	{
		ctx->log(RC_LOG_ERROR, "tile %d,%d: could not build detail mesh", tx, ty);
		return 0;
	}

	rcFreeCompactHeightfield(w.chf);
	w.chf = 0;
	rcFreeContourSet(w.cset);
	w.cset = 0;

	unsigned char* navData = 0;
	int navDataSize = 0;
	if (cfg.maxVertsPerPoly <= DT_VERTS_PER_POLYGON)
	{
		if (w.pmesh->nverts >= 0xffff)
		{
			ctx->log(RC_LOG_ERROR, "tile %d,%d: too many vertices per tile %d (max: %d)", tx, ty, w.pmesh->nverts, 0xffff);
			return 0;
		}

		for (int i = 0; i < w.pmesh->npolys; ++i)
		{
			if (w.pmesh->areas[i] == RC_WALKABLE_AREA)
				w.pmesh->areas[i] = AREA_GROUND;

			if (w.pmesh->areas[i] == AREA_GROUND || w.pmesh->areas[i] == AREA_GRASS || w.pmesh->areas[i] == AREA_ROAD)
				w.pmesh->flags[i] = FLAG_WALK;
			else if (w.pmesh->areas[i] == AREA_WATER)
				w.pmesh->flags[i] = FLAG_SWIM;
			else if (w.pmesh->areas[i] == AREA_DOOR)
				w.pmesh->flags[i] = FLAG_WALK | FLAG_DOOR;
			else if (w.pmesh->areas[i] == AREA_JUMP)
				w.pmesh->flags[i] = FLAG_JUMP; // BuildNav's RecastDemo.exe adds this; upstream leaves 0
		}

		dtNavMeshCreateParams params;
		memset(&params, 0, sizeof(params));
		params.verts = w.pmesh->verts;
		params.vertCount = w.pmesh->nverts;
		params.polys = w.pmesh->polys;
		params.polyAreas = w.pmesh->areas;
		params.polyFlags = w.pmesh->flags;
		params.polyCount = w.pmesh->npolys;
		params.nvp = w.pmesh->nvp;
		params.detailMeshes = w.dmesh->meshes;
		params.detailVerts = w.dmesh->verts;
		params.detailVertsCount = w.dmesh->nverts;
		params.detailTris = w.dmesh->tris;
		params.detailTriCount = w.dmesh->ntris;
		params.offMeshConVerts = geom.getOffMeshConnectionVerts();
		params.offMeshConRad = geom.getOffMeshConnectionRads();
		params.offMeshConDir = geom.getOffMeshConnectionDirs();
		params.offMeshConAreas = geom.getOffMeshConnectionAreas();
		params.offMeshConFlags = geom.getOffMeshConnectionFlags();
		params.offMeshConUserID = geom.getOffMeshConnectionId();
		params.offMeshConCount = geom.getOffMeshConnectionCount();
		params.walkableHeight = s.agentHeight;
		params.walkableRadius = s.agentRadius;
		params.walkableClimb = s.agentMaxClimb;
		params.tileX = tx;
		params.tileY = ty;
		params.tileLayer = 0;
		rcVcopy(params.bmin, w.pmesh->bmin);
		rcVcopy(params.bmax, w.pmesh->bmax);
		params.cs = cfg.cs;
		params.ch = cfg.ch;
		params.buildBvTree = true;

		if (!dtCreateNavMeshData(&params, &navData, &navDataSize))
		{
			ctx->log(RC_LOG_ERROR, "tile %d,%d: could not build Detour navmesh", tx, ty);
			return 0;
		}
	}

	dataSize = navDataSize;
	return navData;
}

// Sample::saveAll: the "MSET" tile set that OpenDAoC's LoadNavMesh reads.
static const int NAVMESHSET_MAGIC = 'M' << 24 | 'S' << 16 | 'E' << 8 | 'T';
static const int NAVMESHSET_VERSION = 1;

struct NavMeshSetHeader
{
	int magic;
	int version;
	int numTiles;
	dtNavMeshParams params;
};

struct NavMeshTileHeader
{
	dtTileRef tileRef;
	int dataSize;
};

static bool saveAll(const char* path, const dtNavMesh* mesh, int& tilesWritten)
{
	tilesWritten = 0;
	FILE* fp = fopen(path, "wb");
	if (!fp)
		return false;

	NavMeshSetHeader header;
	memset(&header, 0, sizeof(header));
	header.magic = NAVMESHSET_MAGIC;
	header.version = NAVMESHSET_VERSION;
	header.numTiles = 0;
	for (int i = 0; i < mesh->getMaxTiles(); ++i)
	{
		const dtMeshTile* tile = mesh->getTile(i);
		if (!tile || !tile->header || !tile->dataSize) continue;
		header.numTiles++;
	}
	memcpy(&header.params, mesh->getParams(), sizeof(dtNavMeshParams));
	bool ok = fwrite(&header, sizeof(NavMeshSetHeader), 1, fp) == 1;

	for (int i = 0; ok && i < mesh->getMaxTiles(); ++i)
	{
		const dtMeshTile* tile = mesh->getTile(i);
		if (!tile || !tile->header || !tile->dataSize) continue;

		NavMeshTileHeader tileHeader;
		memset(&tileHeader, 0, sizeof(tileHeader));
		tileHeader.tileRef = mesh->getTileRef(tile);
		tileHeader.dataSize = tile->dataSize;
		ok = fwrite(&tileHeader, sizeof(tileHeader), 1, fp) == 1 &&
			 fwrite(tile->data, tile->dataSize, 1, fp) == 1;
		if (ok) tilesWritten++;
	}

	ok = (fclose(fp) == 0) && ok;
	return ok;
}

// ---------------------------------------------------------------------------------------------

static void usage(FILE* out)
{
	fprintf(out,
		"usage: gset2nav [options] <input.gset> <output.nav>\n"
		"\n"
		"Builds an OpenDAoC navmesh from a Recast geometry set written by OpenDAoC-BuildNav,\n"
		"with the settings of BuildNav's RecastDemo.exe.\n"
		"\n"
		"options:\n"
		"  -j, --jobs N      build tiles on N threads (default: all cores; output is the same)\n"
		"  --print-settings  print the build settings and exit\n"
		"  -v, --verbose     log every build step\n"
		"  -h, --help        show this help\n");
}

static void printSettings(const Settings& s)
{
	printf("cellSize %g\ncellHeight %g\nagentHeight %g\nagentRadius %g\nagentMaxClimb %g\n"
		   "agentMaxSlope %g\nregionMinSize %g\nregionMergeSize %g\nedgeMaxLen %g\nedgeMaxError %g\n"
		   "vertsPerPoly %g\ndetailSampleDist %g\ndetailSampleMaxError %g\npartitionType %d\ntileSize %g\n"
		   "maxOffMeshConnections %d\npolyRef %d-bit\n",
		   s.cellSize, s.cellHeight, s.agentHeight, s.agentRadius, s.agentMaxClimb, s.agentMaxSlope,
		   s.regionMinSize, s.regionMergeSize, s.edgeMaxLen, s.edgeMaxError, s.vertsPerPoly,
		   s.detailSampleDist, s.detailSampleMaxError, s.partitionType, s.tileSize,
		   GeomSet::MAX_OFFMESH_CONNECTIONS, (int)(sizeof(dtPolyRef) * 8));
}

int main(int argc, char** argv)
{
	Settings s;
	bool verbose = false;
	int jobs = (int)std::thread::hardware_concurrency();
	if (jobs < 1) jobs = 1;
	std::vector<std::string> pos;

	for (int i = 1; i < argc; ++i)
	{
		std::string a = argv[i];
		if (a == "-h" || a == "--help") { usage(stdout); return 0; }
		else if (a == "-v" || a == "--verbose") verbose = true;
		else if (a == "--print-settings") { printSettings(s); return 0; }
		else if (a == "-j" || a == "--jobs")
		{
			if (i + 1 >= argc) { usage(stderr); return 2; }
			jobs = atoi(argv[++i]);
			if (jobs < 1) { fprintf(stderr, "error: --jobs needs a positive number\n"); return 2; }
		}
		else if (a.size() > 1 && a[0] == '-') { fprintf(stderr, "error: unknown option %s\n", a.c_str()); usage(stderr); return 2; }
		else pos.push_back(a);
	}
	if (pos.size() != 2) { usage(stderr); return 2; }
	const std::string inPath = pos[0], outPath = pos[1];

	static_assert(sizeof(dtPolyRef) == 8, "build with DT_POLYREF64: OpenDAoC's Detour uses 64-bit poly refs");

	Context ctx(verbose);
	auto t0 = std::chrono::steady_clock::now();

	GeomSet* geom = new GeomSet; // large; keep it off the stack
	if (!geom->load(&ctx, inPath) || !geom->getMesh() || !geom->getChunkyMesh())
	{
		fprintf(stderr, "error: could not load %s\n", inPath.c_str());
		return 1;
	}

	// handleMeshChanged: a settings line in the .gset overrides the defaults.
	if (const BuildSettings* bs = geom->getBuildSettings())
	{
		s.cellSize = bs->cellSize; s.cellHeight = bs->cellHeight;
		s.agentHeight = bs->agentHeight; s.agentRadius = bs->agentRadius;
		s.agentMaxClimb = bs->agentMaxClimb; s.agentMaxSlope = bs->agentMaxSlope;
		s.regionMinSize = bs->regionMinSize; s.regionMergeSize = bs->regionMergeSize;
		s.edgeMaxLen = bs->edgeMaxLen; s.edgeMaxError = bs->edgeMaxError;
		s.vertsPerPoly = bs->vertsPerPoly; s.detailSampleDist = bs->detailSampleDist;
		s.detailSampleMaxError = bs->detailSampleMaxError; s.partitionType = bs->partitionType;
		if (bs->tileSize > 0) s.tileSize = bs->tileSize;
		fprintf(stderr, "note: %s has its own build settings; using them instead of the defaults\n", inPath.c_str());
	}

	// handleSettings, as modified in BuildNav's RecastDemo.exe for 64-bit poly refs: up to 28 tile
	// bits (upstream caps at 14, which would limit a navmesh to 16384 tiles) and 48 - tileBits
	// poly bits, at most 20.
	const float* bmin = geom->getNavMeshBoundsMin();
	const float* bmax = geom->getNavMeshBoundsMax();
	int gw = 0, gh = 0;
	rcCalcGridSize(bmin, bmax, s.cellSize, &gw, &gh);
	const int ts = (int)s.tileSize;
	const int tw = (gw + ts - 1) / ts;
	const int th = (gh + ts - 1) / ts;
	int tileBits = rcMin((int)ilog2(nextPow2(tw * th)), 28);
	int polyBits = rcMin(48 - tileBits, 20);
	int maxTiles = 1 << tileBits;
	int maxPolys = 1 << polyBits;

	if (geom->droppedOffMeshConnections())
		fprintf(stderr, "warning: %d off-mesh connection(s) beyond the limit of %d were ignored\n",
				geom->droppedOffMeshConnections(), GeomSet::MAX_OFFMESH_CONNECTIONS);
	if (geom->droppedVolumes())
		fprintf(stderr, "warning: %d convex volume(s) beyond the limit of %d were ignored\n",
				geom->droppedVolumes(), GeomSet::MAX_VOLUMES);
	if (geom->truncatedVolumes())
		fprintf(stderr, "warning: %d convex volume(s) had more than %d points; only the first %d were used\n",
				geom->truncatedVolumes(), MAX_CONVEXVOL_PTS, MAX_CONVEXVOL_PTS);

	printf("%s: %d verts, %d tris, %d off-mesh links, %d volumes; %d x %d tiles of %d cells; maxTiles %d\n",
		   inPath.c_str(), geom->getMesh()->getVertCount(), geom->getMesh()->getTriCount(),
		   geom->getOffMeshConnectionCount(), geom->getConvexVolumeCount(), tw, th, ts, maxTiles);

	// handleBuild.
	dtNavMesh* navMesh = dtAllocNavMesh();
	dtNavMeshParams params;
	memset(&params, 0, sizeof(params));
	rcVcopy(params.orig, bmin);
	params.tileWidth = s.tileSize * s.cellSize;
	params.tileHeight = s.tileSize * s.cellSize;
	params.maxTiles = maxTiles;
	params.maxPolys = maxPolys;
	if (!navMesh || dtStatusFailed(navMesh->init(&params)))
	{
		fprintf(stderr, "error: could not init navmesh\n");
		return 1;
	}

	// buildAllTiles: tiles are built in parallel but added in RecastDemo's order (rows of x).
	const float tcs = s.tileSize * s.cellSize;
	const int total = tw * th;
	struct Result { unsigned char* data = 0; int size = 0; std::atomic<bool> done{false}; };
	std::vector<Result> results(total);
	std::atomic<int> next(0);

	auto worker = [&]()
	{
		Context wctx(verbose);
		for (;;)
		{
			int idx = next.fetch_add(1);
			if (idx >= total) break;
			int x = idx % tw, y = idx / tw;
			float tbmin[3], tbmax[3];
			tbmin[0] = bmin[0] + x * tcs;
			tbmin[1] = bmin[1];
			tbmin[2] = bmin[2] + y * tcs;
			tbmax[0] = bmin[0] + (x + 1) * tcs;
			tbmax[1] = bmax[1];
			tbmax[2] = bmin[2] + (y + 1) * tcs;
			int size = 0;
			results[idx].data = buildTileMesh(&wctx, s, *geom, x, y, tbmin, tbmax, size);
			results[idx].size = size;
			results[idx].done.store(true, std::memory_order_release);
		}
	};

	std::vector<std::thread> threads;
	const int nthreads = rcMin(jobs, rcMax(1, total));
	for (int i = 0; i < nthreads; ++i)
		threads.emplace_back(worker);

	int added = 0, empty = 0, dropped = 0;
	for (int idx = 0; idx < total; ++idx)
	{
		while (!results[idx].done.load(std::memory_order_acquire))
			std::this_thread::sleep_for(std::chrono::milliseconds(1));
		int x = idx % tw, y = idx / tw;
		unsigned char* data = results[idx].data;
		results[idx].data = 0;
		if (!data)
		{
			empty++;
		}
		else
		{
			navMesh->removeTile(navMesh->getTileRefAt(x, y, 0), 0, 0);
			dtStatus st = navMesh->addTile(data, results[idx].size, DT_TILE_FREE_DATA, 0, 0);
			if (dtStatusFailed(st))
			{
				dtFree(data);
				dropped++;
			}
			else
			{
				added++;
			}
		}
	}
	for (auto& t : threads) t.join();

	int written = 0;
	if (!saveAll(outPath.c_str(), navMesh, written))
	{
		fprintf(stderr, "error: could not write %s\n", outPath.c_str());
		dtFreeNavMesh(navMesh);
		return 1;
	}
	dtFreeNavMesh(navMesh);
	delete geom;

	double secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
	printf("%s: %d tiles written, %d empty, %d dropped, %.1fs", outPath.c_str(), written, empty, dropped, secs);
	if (g_recastWarnings > 0)
		printf(" (%d Recast warnings; -v lists them)", g_recastWarnings.load());
	printf("\n");
	if (dropped)
	{
		fprintf(stderr,
			"warning: %d tile(s) could not be added to the navmesh (maxTiles %d) and were left out.\n",
			dropped, maxTiles);
	}
	return 0;
}
