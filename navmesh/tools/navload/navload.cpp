// navload: load a .nav file with the OpenDAoC server's own Detour code and query it.
//
//   navload zone000.nav [x y z]
//
// Uses LoadNavMesh from OpenDAoC-Core's Pathing/Detour (the code the server runs), so it answers
// "will my server load this file, and how much of it?". The server's loader does not check
// whether each tile was added; this tool does, and reports tiles that were lost.
//
// With x y z (game coordinates), also asks for the closest point on the mesh and a random point
// within 512 units, as NPC roaming does.

#include <cstdio>
#include <cstdlib>
#include <cstring>

#include "DetourInterface.hpp"
#include "DetourNavMesh.h"
#include "DetourNavMeshQuery.h"

static const float FACTOR = 1.0f / 32.0f; // game units -> Recast units

// Game (x, y, z with z up) -> Recast (x, z, y), as Pathing/Detour.Managed/RecastCoords.cs does.
static void toRecast(float gx, float gy, float gz, float* out)
{
	out[0] = gx * FACTOR;
	out[1] = gz * FACTOR;
	out[2] = gy * FACTOR;
}

int main(int argc, char** argv)
{
	if (argc != 2 && argc != 5)
	{
		fprintf(stderr, "usage: navload <file.nav> [x y z]\n");
		return 2;
	}

	int declared = 0;
	if (FILE* fp = fopen(argv[1], "rb"))
	{
		int hdr[3] = {0, 0, 0};
		if (fread(hdr, sizeof(hdr), 1, fp) == 1)
			declared = hdr[2];
		fclose(fp);
	}

	dtNavMesh* mesh = nullptr;
	if (!LoadNavMesh(argv[1], &mesh) || !mesh)
	{
		printf("%s: the server's LoadNavMesh REJECTED the file\n", argv[1]);
		return 1;
	}

	int loaded = 0, polys = 0;
	for (int i = 0; i < mesh->getMaxTiles(); ++i)
	{
		const dtMeshTile* t = ((const dtNavMesh*)mesh)->getTile(i);
		if (t && t->header)
		{
			loaded++;
			polys += t->header->polyCount;
		}
	}
	printf("%s: loaded %d of %d tiles (%d polygons) with the server's LoadNavMesh\n", argv[1], loaded, declared, polys);
	int rc = loaded == declared && loaded > 0 ? 0 : 1;
	if (loaded != declared)
		printf("  PROBLEM: %d tile(s) in the file were not loaded\n", declared - loaded);

	if (argc == 5)
	{
		dtNavMeshQuery* query = nullptr;
		if (!CreateNavMeshQuery(mesh, &query))
		{
			printf("  PROBLEM: could not create a query\n");
			FreeNavMesh(mesh);
			return 1;
		}
		dtPolyFlags filter[] = {(dtPolyFlags)(dtPolyFlags::ALL ^ dtPolyFlags::DISABLED), (dtPolyFlags)0};
		float c[3], ext[3], out[3];
		toRecast((float)atof(argv[2]), (float)atof(argv[3]), (float)atof(argv[4]), c);
		toRecast(32, 32, 64, ext); // the server's default query extents

		dtStatus st = FindClosestPoint(query, c, ext, filter, out);
		if (dtStatusSucceed(st))
			printf("  closest point on mesh: %.0f %.0f %.0f\n", out[0] / FACTOR, out[2] / FACTOR, out[1] / FACTOR);
		else
		{
			printf("  no mesh within (32, 32, 64) of that point\n");
			rc = 1;
		}

		st = FindRandomPointAroundCircle(query, c, 512 * FACTOR, ext, filter, out);
		if (dtStatusSucceed(st))
			printf("  random point within 512: %.0f %.0f %.0f\n", out[0] / FACTOR, out[2] / FACTOR, out[1] / FACTOR);
		else
			printf("  no random point found within 512\n");
		FreeNavMeshQuery(query);
	}

	FreeNavMesh(mesh);
	return rc;
}
