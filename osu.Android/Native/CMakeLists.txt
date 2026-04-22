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
#
# IMPORTANT: do NOT pass `-s` (or `-Wl,-s`) here.  Stripping the native library
# removes the symbol table, which means Android tombstones — and crash-report
# tools like Crash Log Viewer that read them — print only `pc=<hex>` with no
# function name.  That makes the SDLThread / audio-thread SIGSEGVs we have been
# chasing essentially unfixable from a tombstone alone, because we cannot tell
# whether the null indirect call originated in our `OboeBridge::onAudioReady`,
# in Oboe itself, in libvulkan, or in the renderer.  Keeping symbols costs only
# a few hundred KB in the on-device .so (and zero APK download size when LZ4
# assembly compression is on, since the .so is also compressed), and it is the
# single highest-leverage change we can make for diagnosing real-device crashes.
# `-Wl,--gc-sections` still removes unreferenced sections; only the symbol
# *names* are retained.
set(CMAKE_SHARED_LINKER_FLAGS_RELEASE "-Wl,--gc-sections -Wl,-z,max-page-size=16384")

# Disable Oboe's flowgraph module — we don't use any audio processing/conversion
# features (our bridge outputs silence for latency measurement only).
# This reduces the Oboe portion of the binary by ~50%.
set(OBOE_ENABLE_FLOWGRAPH OFF CACHE BOOL "Disable Oboe flowgraph to reduce binary size")
# Skip Oboe's tests/examples — we never ship them. Avoids pulling googletest
# and shaves several seconds off the cold CMake configure/build.
set(BUILD_TESTING OFF CACHE BOOL "Disable Oboe tests" FORCE)
set(OBOE_BUILD_TESTS OFF CACHE BOOL "Disable Oboe tests" FORCE)
set(OBOE_BUILD_EXAMPLES OFF CACHE BOOL "Disable Oboe examples" FORCE)
set(OBOE_BUILD_DOCS OFF CACHE BOOL "Disable Oboe docs" FORCE)

# Download and build Oboe main branch from source to ensure we have the latest
# features and fixes (ADPF performance hints, workload management, spatialization,
# API 36 compatibility) regardless of the build environment.
# main is preferred over pinned tags because Oboe releases infrequently (~yearly)
# and the main branch accumulates significant latency-critical improvements between tags.
# GIT_SHALLOW TRUE: only fetch the tip of main (no history) — significantly faster CI checkout.
include(FetchContent)
FetchContent_Declare(oboe
    GIT_REPOSITORY https://github.com/google/oboe.git
    GIT_TAG main
    GIT_SHALLOW TRUE
)
FetchContent_MakeAvailable(oboe)

# Patch Oboe's compile options:
#   1. Replace deprecated `-Ofast` with `-O3` — `-Ofast` was removed in Clang 21
#      and emits warnings on newer NDKs.
#   2. Strip `-ffast-math` from Oboe's compile options.  In a callback that only
#      does memset/store it is harmless functionally, but it injects libm
#      `__FINITE_MATH_ONLY__` symbol versions that can break linkage against the
#      system libm in rare NDK combos.  Our own bridge keeps `-ffast-math` (see
#      CMAKE_CXX_FLAGS_RELEASE above) because we link only against libc/log/Vulkan.
get_target_property(OBOE_OPTIONS oboe COMPILE_OPTIONS)
if(OBOE_OPTIONS)
    list(REMOVE_ITEM OBOE_OPTIONS "-Ofast")
    list(REMOVE_ITEM OBOE_OPTIONS "-ffast-math")
    list(APPEND OBOE_OPTIONS "-O3")
    set_target_properties(oboe PROPERTIES COMPILE_OPTIONS "${OBOE_OPTIONS}")
endif()

set(OBOE_LIB oboe)

find_library(vulkan-lib vulkan REQUIRED)
find_library(log-lib log REQUIRED)
find_library(android-lib android REQUIRED)
# `dl` is needed for dladdr() in crash_handler.cpp (symbol resolution from PCs).
# It is normally auto-linked on Android, but make the dependency explicit so we
# don't accidentally lose dladdr if the toolchain default ever changes.
find_library(dl-lib dl REQUIRED)

add_library(osu_native SHARED
    oboe_bridge.cpp
    vulkan_bridge.cpp
    crash_handler.cpp
)


target_link_libraries(osu_native
    ${OBOE_LIB}
    ${vulkan-lib}
    ${log-lib}
    ${android-lib}
    ${dl-lib}
)
