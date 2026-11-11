// qsort with the Microsoft C runtime's ordering of equal elements.
//
// Recast and Detour sort with qsort, and some of their comparisons have ties (triangles or
// polygons with the same bounds). qsort isn't stable, so each C library leaves ties in a different
// order, and the navmesh comes out slightly different: the BV tree in each tile, and potentially
// which triangles Recast groups into a chunk. BuildNav's RecastDemo.exe uses the Microsoft C
// runtime, so to produce the same bytes on Linux and macOS the build routes Recast's and Detour's
// qsort calls here (see CMakeLists.txt).
//
// This is an independent implementation of the algorithm the Microsoft runtime's qsort uses
// (non-recursive quicksort with median-of-three pivoting, and a max-selection sort for runs of at
// most 8 elements). Whether it matches is checked by comparing gset2nav's output with
// RecastDemo.exe's on the same input (see ../README.md).

#include <stddef.h>
#include <string.h>

namespace
{
typedef int (*CompareFn)(const void*, const void*);

const size_t CUTOFF = 8;

inline void swapBytes(char* a, char* b, size_t width)
{
	if (a == b)
		return;
	while (width--)
	{
		char t = *a;
		*a++ = *b;
		*b++ = t;
	}
}

// Selection sort: move the (first) largest element to the end, repeat.
void shortSort(char* lo, char* hi, size_t width, CompareFn comp)
{
	while (hi > lo)
	{
		char* max = lo;
		for (char* p = lo + width; p <= hi; p += width)
		{
			if (comp(p, max) > 0)
				max = p;
		}
		swapBytes(max, hi, width);
		hi -= width;
	}
}
} // namespace

extern "C" void gset2nav_qsort(void* base, size_t num, size_t width, CompareFn comp)
{
	if (num < 2 || width == 0)
		return;

	char* loStack[8 * sizeof(void*) - 2];
	char* hiStack[8 * sizeof(void*) - 2];
	int stkptr = 0;

	char* lo = static_cast<char*>(base);
	char* hi = static_cast<char*>(base) + width * (num - 1);

recurse:
	{
		const size_t size = (size_t)(hi - lo) / width + 1;

		if (size <= CUTOFF)
		{
			shortSort(lo, hi, width, comp);
		}
		else
		{
			char* mid = lo + (size / 2) * width;

			// Median of three: order lo, mid, hi.
			if (comp(lo, mid) > 0)
				swapBytes(lo, mid, width);
			if (comp(lo, hi) > 0)
				swapBytes(lo, hi, width);
			if (comp(mid, hi) > 0)
				swapBytes(mid, hi, width);

			char* loguy = lo;
			char* higuy = hi;

			for (;;)
			{
				if (mid > loguy)
				{
					do
					{
						loguy += width;
					} while (loguy < mid && comp(loguy, mid) <= 0);
				}
				if (mid <= loguy)
				{
					do
					{
						loguy += width;
					} while (loguy <= hi && comp(loguy, mid) <= 0);
				}

				do
				{
					higuy -= width;
				} while (higuy > mid && comp(higuy, mid) > 0);

				if (higuy < loguy)
					break;

				swapBytes(loguy, higuy, width);

				// The pivot moved; follow it.
				if (mid == higuy)
					mid = loguy;
			}

			// Skip over elements equal to the pivot.
			higuy += width;
			if (mid < higuy)
			{
				do
				{
					higuy -= width;
				} while (higuy > mid && comp(higuy, mid) == 0);
			}
			if (mid >= higuy)
			{
				do
				{
					higuy -= width;
				} while (higuy > lo && comp(higuy, mid) == 0);
			}

			// Sort the smaller part next; save the larger one.
			if (higuy - lo >= hi - loguy)
			{
				if (lo < higuy)
				{
					loStack[stkptr] = lo;
					hiStack[stkptr] = higuy;
					++stkptr;
				}
				if (loguy < hi)
				{
					lo = loguy;
					goto recurse;
				}
			}
			else
			{
				if (loguy < hi)
				{
					loStack[stkptr] = loguy;
					hiStack[stkptr] = hi;
					++stkptr;
				}
				if (lo < higuy)
				{
					hi = higuy;
					goto recurse;
				}
			}
		}
	}

	--stkptr;
	if (stkptr >= 0)
	{
		lo = loStack[stkptr];
		hi = hiStack[stkptr];
		goto recurse;
	}
}
