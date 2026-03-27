cmake_minimum_required(VERSION 3.18)
project(osu_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)

# Maximum release optimizations for lowest-latency audio callback path.
# -O3:           aggressive inlining & vectorisation
# -flto:         link-time optimisation across translation units
# -ffast-math:   enable SIMD-friendly FP (safe — we only memset + store a double)
# -ffunction/data-sections + --gc-sections:  dead-code elimination
# -fvisibility=hidden:  only OSU_EXPORT symbols are visible
set(CMAKE_CXX_FLAGS_RELEASE "-O3 -flto -ffast-math -ffunction-sections -fdata-sections -fvisibility=hidden -DNDEBUG")
set(CMAKE_C_FLAGS_RELEASE   "-O3 -flto -ffast-math -ffunction-sections -fdata-sections -fvisibility=hidden -DNDEBUG")
set(CMAKE_SHARED_LINKER_FLAGS_RELEASE "-Wl,--gc-sections -s")

# Disable Oboe's flowgraph module — we don't use any audio processing/conversion
# features (our bridge outputs silence for latency measurement only).
# This reduces the Oboe portion of the binary by ~50%.
set(OBOE_ENABLE_FLOWGRAPH OFF CACHE BOOL "Disable Oboe flowgraph to reduce binary size")

# Try pre-installed Oboe first (e.g. via Android NDK prefab or local install).
# If not found, download and build from source for CI/hermetic builds.
find_package(oboe QUIET CONFIG)

if(oboe_FOUND)
    set(OBOE_LIB oboe::oboe)
else()
    include(FetchContent)
    FetchContent_Declare(oboe
        URL https://github.com/google/oboe/archive/refs/tags/1.10.0.tar.gz
        URL_HASH SHA256=0e4245f8860c4287040a5d76501c588490bcc9cb57614c486c0c201a5dde3e9f
    )
    FetchContent_MakeAvailable(oboe)
    set(OBOE_LIB oboe)
endif()

find_library(vulkan-lib vulkan)
find_library(log-lib log)
find_library(android-lib android)

add_library(osu_native SHARED
    oboe_bridge.cpp
    vulkan_bridge.cpp
)

target_link_libraries(osu_native
    ${OBOE_LIB}
    ${vulkan-lib}
    ${log-lib}
    ${android-lib}
)
