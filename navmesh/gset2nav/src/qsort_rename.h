// Force-included (see CMakeLists.txt) into Recast, Detour and ChunkyTriMesh.cpp, so their qsort
// calls go to gset2nav_qsort (msvc_qsort.cpp).
//
// The C library headers are included first, before the rename, so their own declarations and
// libstdc++'s `using ::qsort;` lines are left alone. Their include guards keep later includes
// from expanding the macro inside them.

#pragma once

#include <cstdlib>
#include <stdlib.h>

extern "C" void gset2nav_qsort(void* base, size_t num, size_t width,
                               int (*comp)(const void*, const void*));

#define qsort gset2nav_qsort
