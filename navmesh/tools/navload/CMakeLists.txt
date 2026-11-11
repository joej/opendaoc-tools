# navload: load a .nav with the OpenDAoC server's own Detour code.
#
#   cmake -S navmesh/tools/navload -B build/navload -DOPENDAOC_CORE=/path/to/OpenDAoC-Core
#   cmake --build build/navload

cmake_minimum_required(VERSION 3.16)
project(navload CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

set(OPENDAOC_CORE "" CACHE PATH "OpenDAoC-Core checkout")
set(DETOUR_DIR ${OPENDAOC_CORE}/Pathing/Detour)
if(NOT EXISTS ${DETOUR_DIR}/Source/DetourInterface.cpp)
  message(FATAL_ERROR "Set -DOPENDAOC_CORE to an OpenDAoC-Core checkout (needs Pathing/Detour)")
endif()

# Compile the server's Detour sources into this tool (nothing is written into the Core checkout).
file(GLOB DETOUR_SOURCES ${DETOUR_DIR}/Source/*.cpp)
add_executable(navload navload.cpp ${DETOUR_SOURCES})
target_include_directories(navload PRIVATE ${DETOUR_DIR}/Include)
target_compile_definitions(navload PRIVATE DT_POLYREF64)
