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

# Download and build Oboe main branch from source to ensure we have the latest
# features (like ADPF performance hints) regardless of the build environment.
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
