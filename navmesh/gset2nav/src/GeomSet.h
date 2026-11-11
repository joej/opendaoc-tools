// Geometry set (.gset) loader.
//
// Trimmed copy of InputGeom from recastnavigation v1.6.0 RecastDemo (zlib licence,
// Copyright (c) 2009-2010 Mikko Mononen memon@inside.org). Altered from the original:
//   - MAX_OFFMESH_CONNECTIONS raised from 256 to 4096, as in OpenDAoC-BuildNav's RecastDemo.exe;
//   - rendering, raycast and editing code removed; no dependency on the demo's GUI;
//   - off-mesh connection user IDs are zeroed (the original leaves them uninitialised);
//   - counts of entries dropped at the limits are kept so the caller can report them.

#ifndef GSET2NAV_GEOMSET_H
#define GSET2NAV_GEOMSET_H

#include <string>

class rcContext;
class rcMeshLoaderObj;
struct rcChunkyTriMesh;

static const int MAX_CONVEXVOL_PTS = 12;
struct ConvexVolume
{
	float verts[MAX_CONVEXVOL_PTS * 3];
	float hmin, hmax;
	int nverts;
	int area;
};

struct BuildSettings
{
	float cellSize;
	float cellHeight;
	float agentHeight;
	float agentRadius;
	float agentMaxClimb;
	float agentMaxSlope;
	float regionMinSize;
	float regionMergeSize;
	float edgeMaxLen;
	float edgeMaxError;
	float vertsPerPoly;
	float detailSampleDist;
	float detailSampleMaxError;
	int partitionType;
	float navMeshBMin[3];
	float navMeshBMax[3];
	float tileSize;
};

class GeomSet
{
public:
	static const int MAX_OFFMESH_CONNECTIONS = 4096;
	static const int MAX_VOLUMES = 256;

	GeomSet();
	~GeomSet();

	bool load(rcContext* ctx, const std::string& filepath);

	const rcMeshLoaderObj* getMesh() const { return m_mesh; }
	const rcChunkyTriMesh* getChunkyMesh() const { return m_chunkyMesh; }
	const float* getNavMeshBoundsMin() const { return m_hasBuildSettings ? m_buildSettings.navMeshBMin : m_meshBMin; }
	const float* getNavMeshBoundsMax() const { return m_hasBuildSettings ? m_buildSettings.navMeshBMax : m_meshBMax; }
	const BuildSettings* getBuildSettings() const { return m_hasBuildSettings ? &m_buildSettings : 0; }

	int getOffMeshConnectionCount() const { return m_offMeshConCount; }
	const float* getOffMeshConnectionVerts() const { return m_offMeshConVerts; }
	const float* getOffMeshConnectionRads() const { return m_offMeshConRads; }
	const unsigned char* getOffMeshConnectionDirs() const { return m_offMeshConDirs; }
	const unsigned char* getOffMeshConnectionAreas() const { return m_offMeshConAreas; }
	const unsigned short* getOffMeshConnectionFlags() const { return m_offMeshConFlags; }
	const unsigned int* getOffMeshConnectionId() const { return m_offMeshConId; }

	int getConvexVolumeCount() const { return m_volumeCount; }
	const ConvexVolume* getConvexVolumes() const { return m_volumes; }

	// Entries the file had beyond the limits above (ignored, as RecastDemo does).
	int droppedOffMeshConnections() const { return m_droppedOffMesh; }
	int droppedVolumes() const { return m_droppedVolumes; }
	int truncatedVolumes() const { return m_truncatedVolumes; }

private:
	bool loadMesh(rcContext* ctx, const std::string& filepath);
	bool loadGeomSet(rcContext* ctx, const std::string& filepath);

	rcChunkyTriMesh* m_chunkyMesh;
	rcMeshLoaderObj* m_mesh;
	float m_meshBMin[3], m_meshBMax[3];
	BuildSettings m_buildSettings;
	bool m_hasBuildSettings;

	float m_offMeshConVerts[MAX_OFFMESH_CONNECTIONS * 3 * 2];
	float m_offMeshConRads[MAX_OFFMESH_CONNECTIONS];
	unsigned char m_offMeshConDirs[MAX_OFFMESH_CONNECTIONS];
	unsigned char m_offMeshConAreas[MAX_OFFMESH_CONNECTIONS];
	unsigned short m_offMeshConFlags[MAX_OFFMESH_CONNECTIONS];
	unsigned int m_offMeshConId[MAX_OFFMESH_CONNECTIONS];
	int m_offMeshConCount;

	ConvexVolume m_volumes[MAX_VOLUMES];
	int m_volumeCount;

	int m_droppedOffMesh;
	int m_droppedVolumes;
	int m_truncatedVolumes;

	GeomSet(const GeomSet&);
	GeomSet& operator=(const GeomSet&);
};

#endif
