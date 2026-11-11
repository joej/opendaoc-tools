// Geometry set (.gset) loader. See GeomSet.h for origin and changes.
//
// Copyright (c) 2009-2010 Mikko Mononen memon@inside.org
//
// This software is provided 'as-is', without any express or implied
// warranty.  In no event will the authors be held liable for any damages
// arising from the use of this software.
// Permission is granted to anyone to use this software for any purpose,
// including commercial applications, and to alter it and redistribute it
// freely, subject to the following restrictions:
// 1. The origin of this software must not be misrepresented; you must not
//    claim that you wrote the original software. If you use this software
//    in a product, an acknowledgment in the product documentation would be
//    appreciated but is not required.
// 2. Altered source versions must be plainly marked as such, and must not be
//    misrepresented as being the original software.
// 3. This notice may not be removed or altered from any source distribution.
//

#include <ctype.h>
#include <stdio.h>
#include <string.h>
#include <algorithm>

#include "Recast.h"
#include "GeomSet.h"
#include "ChunkyTriMesh.h"
#include "MeshLoaderObj.h"

static char* parseRow(char* buf, char* bufEnd, char* row, int len)
{
	bool start = true;
	bool done = false;
	int n = 0;
	while (!done && buf < bufEnd)
	{
		char c = *buf;
		buf++;
		switch (c)
		{
			case '\n':
				if (start) break;
				done = true;
				break;
			case '\r':
				break;
			case '\t':
			case ' ':
				if (start) break;
				// else falls through
			default:
				start = false;
				row[n++] = c;
				if (n >= len - 1)
					done = true;
				break;
		}
	}
	row[n] = '\0';
	return buf;
}

static bool fileExists(const std::string& path)
{
	FILE* fp = fopen(path.c_str(), "rb");
	if (!fp) return false;
	fclose(fp);
	return true;
}

GeomSet::GeomSet() :
	m_chunkyMesh(0),
	m_mesh(0),
	m_hasBuildSettings(false),
	m_offMeshConCount(0),
	m_volumeCount(0),
	m_droppedOffMesh(0),
	m_droppedVolumes(0),
	m_truncatedVolumes(0)
{
	memset(m_offMeshConId, 0, sizeof(m_offMeshConId));
	memset(m_volumes, 0, sizeof(m_volumes));
}

GeomSet::~GeomSet()
{
	delete m_chunkyMesh;
	delete m_mesh;
}

bool GeomSet::loadMesh(rcContext* ctx, const std::string& filepath)
{
	if (m_mesh)
	{
		delete m_chunkyMesh;
		m_chunkyMesh = 0;
		delete m_mesh;
		m_mesh = 0;
	}
	m_offMeshConCount = 0;
	m_volumeCount = 0;

	m_mesh = new rcMeshLoaderObj;
	if (!m_mesh->load(filepath))
	{
		ctx->log(RC_LOG_ERROR, "loadMesh: Could not load '%s'", filepath.c_str());
		return false;
	}

	rcCalcBounds(m_mesh->getVerts(), m_mesh->getVertCount(), m_meshBMin, m_meshBMax);

	m_chunkyMesh = new rcChunkyTriMesh;
	if (!rcCreateChunkyTriMesh(m_mesh->getVerts(), m_mesh->getTris(), m_mesh->getTriCount(), 256, m_chunkyMesh))
	{
		ctx->log(RC_LOG_ERROR, "loadMesh: Failed to build chunky mesh.");
		return false;
	}
	return true;
}

bool GeomSet::loadGeomSet(rcContext* ctx, const std::string& filepath)
{
	FILE* fp = fopen(filepath.c_str(), "rb");
	if (!fp)
	{
		ctx->log(RC_LOG_ERROR, "Could not open '%s'", filepath.c_str());
		return false;
	}
	if (fseek(fp, 0, SEEK_END) != 0) { fclose(fp); return false; }
	long bufSize = ftell(fp);
	if (bufSize < 0 || fseek(fp, 0, SEEK_SET) != 0) { fclose(fp); return false; }
	char* buf = new char[bufSize > 0 ? bufSize : 1];
	size_t readLen = bufSize > 0 ? fread(buf, bufSize, 1, fp) : 1;
	fclose(fp);
	if (readLen != 1)
	{
		delete[] buf;
		return false;
	}

	// The .gset's "f" line names the .obj. RecastDemo resolves it against its working directory;
	// fall back to the .gset's own folder so the tool can be run from anywhere.
	std::string gsetDir;
	size_t slash = filepath.find_last_of("/\\");
	if (slash != std::string::npos)
		gsetDir = filepath.substr(0, slash + 1);

	m_offMeshConCount = 0;
	m_volumeCount = 0;
	delete m_mesh;
	m_mesh = 0;

	char* src = buf;
	char* srcEnd = buf + bufSize;
	char row[512];
	while (src < srcEnd)
	{
		row[0] = '\0';
		src = parseRow(src, srcEnd, row, sizeof(row) / sizeof(char));
		if (row[0] == 'f')
		{
			const char* name = row + 1;
			while (*name && isspace(*name))
				name++;
			if (*name)
			{
				std::string path = name;
				if (!fileExists(path) && !gsetDir.empty())
				{
					std::string alt = gsetDir + path;
					if (!fileExists(alt))
					{
						size_t s2 = path.find_last_of("/\\");
						alt = gsetDir + (s2 == std::string::npos ? path : path.substr(s2 + 1));
					}
					if (fileExists(alt))
						path = alt;
				}
				if (!loadMesh(ctx, path))
				{
					delete[] buf;
					return false;
				}
			}
		}
		else if (row[0] == 'c')
		{
			if (m_offMeshConCount < MAX_OFFMESH_CONNECTIONS)
			{
				float* v = &m_offMeshConVerts[m_offMeshConCount * 3 * 2];
				int bidir = 0, area = 0, flags = 0;
				float rad = 0;
				sscanf(row + 1, "%f %f %f  %f %f %f %f %d %d %d",
					   &v[0], &v[1], &v[2], &v[3], &v[4], &v[5], &rad, &bidir, &area, &flags);
				m_offMeshConRads[m_offMeshConCount] = rad;
				m_offMeshConDirs[m_offMeshConCount] = (unsigned char)bidir;
				m_offMeshConAreas[m_offMeshConCount] = (unsigned char)area;
				m_offMeshConFlags[m_offMeshConCount] = (unsigned short)flags;
				m_offMeshConCount++;
			}
			else
			{
				m_droppedOffMesh++;
			}
		}
		else if (row[0] == 'v')
		{
			int nverts = 0, area = 0;
			float hmin = 0, hmax = 0;
			sscanf(row + 1, "%d %d %f %f", &nverts, &area, &hmin, &hmax);
			ConvexVolume* vol = 0;
			if (m_volumeCount < MAX_VOLUMES)
			{
				vol = &m_volumes[m_volumeCount++];
				vol->area = area;
				vol->hmin = hmin;
				vol->hmax = hmax;
				// RecastDemo writes past the end of verts[] when a volume has more than 12 points.
				// Keep the first 12 and report it instead.
				vol->nverts = nverts > MAX_CONVEXVOL_PTS ? MAX_CONVEXVOL_PTS : nverts;
				if (nverts > MAX_CONVEXVOL_PTS)
					m_truncatedVolumes++;
			}
			else
			{
				m_droppedVolumes++;
			}
			for (int i = 0; i < nverts; ++i)
			{
				row[0] = '\0';
				src = parseRow(src, srcEnd, row, sizeof(row) / sizeof(char));
				if (vol && i < MAX_CONVEXVOL_PTS)
					sscanf(row, "%f %f %f", &vol->verts[i * 3 + 0], &vol->verts[i * 3 + 1], &vol->verts[i * 3 + 2]);
			}
		}
		else if (row[0] == 's')
		{
			m_hasBuildSettings = true;
			sscanf(row + 1, "%f %f %f %f %f %f %f %f %f %f %f %f %f %d %f %f %f %f %f %f %f",
				   &m_buildSettings.cellSize,
				   &m_buildSettings.cellHeight,
				   &m_buildSettings.agentHeight,
				   &m_buildSettings.agentRadius,
				   &m_buildSettings.agentMaxClimb,
				   &m_buildSettings.agentMaxSlope,
				   &m_buildSettings.regionMinSize,
				   &m_buildSettings.regionMergeSize,
				   &m_buildSettings.edgeMaxLen,
				   &m_buildSettings.edgeMaxError,
				   &m_buildSettings.vertsPerPoly,
				   &m_buildSettings.detailSampleDist,
				   &m_buildSettings.detailSampleMaxError,
				   &m_buildSettings.partitionType,
				   &m_buildSettings.navMeshBMin[0],
				   &m_buildSettings.navMeshBMin[1],
				   &m_buildSettings.navMeshBMin[2],
				   &m_buildSettings.navMeshBMax[0],
				   &m_buildSettings.navMeshBMax[1],
				   &m_buildSettings.navMeshBMax[2],
				   &m_buildSettings.tileSize);
		}
	}

	delete[] buf;
	return true;
}

bool GeomSet::load(rcContext* ctx, const std::string& filepath)
{
	size_t extensionPos = filepath.find_last_of('.');
	if (extensionPos == std::string::npos)
		return false;

	std::string extension = filepath.substr(extensionPos);
	std::transform(extension.begin(), extension.end(), extension.begin(), ::tolower);

	if (extension == ".gset")
		return loadGeomSet(ctx, filepath);
	if (extension == ".obj")
		return loadMesh(ctx, filepath);

	ctx->log(RC_LOG_ERROR, "Unknown input type '%s' (expected .gset or .obj)", extension.c_str());
	return false;
}
