cmake_minimum_required(VERSION 3.18)
project(osu_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Maximum release optimizations for lowest-latency audio callback path.
# -O3:           aggressive inlining & vectorisation
# -flto=thin:    link-time optimisation with thin backend (20-40% faster link than full LTO,
#                equivalent binary quality for small TUs like this project)
# -ffast-math:   enable SIMD-friendly FP (safe — we only memset + store a double)
# -ffunction/data-sections + --gc-sections:  dead-code elimination
# -fvisibility=hidden:  only OSU_EXPORT symbols are visible
set(CMAKE_CXX_FLAGS_RELEASE "-O3 -flto=thin -ffast-math -ffunction-sections -fdata-sections -fvisibility=hidden -DNDEBUG")
set(CMAKE_C_FLAGS_RELEASE   "-O3 -flto=thin -ffast-math -ffunction-sections -fdata-sections -fvisibility=hidden -DNDEBUG")
# -Wl,-z,max-page-size=16384: align ELF LOAD segments to 16 KB for Android 15+
#   devices with 16 KB page sizes.  Without this, the .so will fail to load on
#   such devices.  NDK r28+ supports this flag.
set(CMAKE_SHARED_LINKER_FLAGS_RELEASE "-Wl,--gc-sections -Wl,-z,max-page-size=16384 -s")

# Disable Oboe's flowgraph module — we don't use any audio processing/conversion
# features (our bridge outputs silence for latency measurement only).
# This reduces the Oboe portion of the binary by ~50%.
set(OBOE_ENABLE_FLOWGRAPH OFF CACHE BOOL "Disable Oboe flowgraph to reduce binary size")

# Download and build Oboe main branch from source to ensure we have the latest
# features and fixes (ADPF performance hints, workload management, spatialization,
# API 36 compatibility) regardless of the build environment.
# main is preferred over pinned tags because Oboe releases infrequently (~yearly)
# and the main branch accumulates significant latency-critical improvements between tags.
include(FetchContent)
FetchContent_Declare(oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG main
)
FetchContent_MakeAvailable(oboe)

# Patch Oboe's deprecated -Ofast flag to avoid build warnings/errors
# and ensure we use the same optimized flags as the rest of the project.
get_target_property(OBOE_OPTIONS oboe COMPILE_OPTIONS)
if(OBOE_OPTIONS)
    string(REPLACE "-Ofast" "-O3;-ffast-math" OBOE_OPTIONS "${OBOE_OPTIONS}")
    set_target_properties(oboe PROPERTIES COMPILE_OPTIONS "${OBOE_OPTIONS}")
endif()

set(OBOE_LIB oboe)

find_library(vulkan-lib vulkan REQUIRED)
find_library(log-lib log REQUIRED)
find_library(android-lib android REQUIRED)

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
