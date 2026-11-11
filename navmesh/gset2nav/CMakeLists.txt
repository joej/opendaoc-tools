# gset2nav: build an OpenDAoC navmesh (.nav) from a Recast geometry set (.gset + .obj).
# Replaces the RecastDemo.exe that OpenDAoC-BuildNav ships, on Linux, macOS and Windows.
#
#   cmake -S navmesh/gset2nav -B build/gset2nav -DCMAKE_BUILD_TYPE=Release
#   cmake --build build/gset2nav
#
# Fetches recastnavigation at the upstream commit BuildNav's RecastDemo.exe was built from, and
# applies patches/recast-detail-as-recastdemo-exe.patch so the output matches the exe byte for
# byte (see ../docs/recastdemo-settings.md).

cmake_minimum_required(VERSION 3.16)
project(gset2nav CXX)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

include(FetchContent)
FetchContent_Declare(recastnavigation
  GIT_REPOSITORY https://github.com/recastnavigation/recastnavigation.git
  GIT_TAG        f25a3c3384f27eafdb53f28434d38834bc813ef5  # main, 2025-05-06
  PATCH_COMMAND  git apply --whitespace=nowarn ${CMAKE_CURRENT_SOURCE_DIR}/patches/recast-detail-as-recastdemo-exe.patch
)
set(RECASTNAVIGATION_DEMO     OFF CACHE BOOL "" FORCE)
set(RECASTNAVIGATION_TESTS    OFF CACHE BOOL "" FORCE)
set(RECASTNAVIGATION_EXAMPLES OFF CACHE BOOL "" FORCE)
# The OpenDAoC server's Detour is built with 64-bit poly refs; the .nav tile refs must match.
set(RECASTNAVIGATION_DT_POLYREF64 ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(recastnavigation)

set(DEMO_SRC ${recastnavigation_SOURCE_DIR}/RecastDemo)

add_executable(gset2nav
  src/main.cpp
  src/GeomSet.cpp
  ${DEMO_SRC}/Source/MeshLoaderObj.cpp
  ${DEMO_SRC}/Source/ChunkyTriMesh.cpp
)
target_include_directories(gset2nav PRIVATE src ${DEMO_SRC}/Include)
target_link_libraries(gset2nav PRIVATE RecastNavigation::Recast RecastNavigation::Detour gset2nav_sort)

# Keep float results the same on every platform: no fused multiply-add contraction
# (clang on Apple Silicon fuses a*b+c by default, which changes the mesh).
foreach(t gset2nav Recast Detour)
  if(MSVC)
    target_compile_options(${t} PRIVATE /fp:precise)
  else()
    target_compile_options(${t} PRIVATE -ffp-contract=off)
  endif()
endforeach()

# Sort ties the way the Microsoft C runtime does, so the tiles match RecastDemo.exe's
# (see src/msvc_qsort.cpp). Recast's and Detour's qsort calls are renamed at compile time;
# msvc_qsort.cpp itself is compiled without the rename.
target_compile_definitions(Recast PRIVATE qsort=gset2nav_qsort)
target_compile_definitions(Detour PRIVATE qsort=gset2nav_qsort)
set_source_files_properties(${DEMO_SRC}/Source/ChunkyTriMesh.cpp
  PROPERTIES COMPILE_DEFINITIONS qsort=gset2nav_qsort)
add_library(gset2nav_sort STATIC src/msvc_qsort.cpp)
if(NOT MSVC)
  target_compile_options(gset2nav_sort PRIVATE -ffp-contract=off)
endif()
