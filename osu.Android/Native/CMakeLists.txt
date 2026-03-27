cmake_minimum_required(VERSION 3.18)
project(osu_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)

# Release build optimizations for low-latency performance.
set(CMAKE_CXX_FLAGS_RELEASE "-O2 -flto -fvisibility=hidden -DNDEBUG")
set(CMAKE_C_FLAGS_RELEASE "-O2 -flto -fvisibility=hidden -DNDEBUG")

# Try pre-installed Oboe first (e.g. via Android NDK prefab or local install).
# If not found, download and build from source for CI/hermetic builds.
find_package(oboe QUIET CONFIG)

if(oboe_FOUND)
    set(OBOE_LIB oboe::oboe)
else()
    include(FetchContent)
    FetchContent_Declare(oboe
        URL https://github.com/google/oboe/archive/refs/tags/1.9.0.tar.gz
        URL_HASH SHA256=e030276d25b8bdfaeb04f66646821b1ba4a2b6a580a7e84fb144a478eaecd663
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
