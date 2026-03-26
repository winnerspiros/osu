cmake_minimum_required(VERSION 3.18)
project(osu_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)

find_package(oboe REQUIRED CONFIG)
find_library(vulkan-lib vulkan)
find_library(log-lib log)
find_library(android-lib android)

add_library(osu_native SHARED
    oboe_bridge.cpp
    vulkan_bridge.cpp
)

target_link_libraries(osu_native
    oboe::oboe
    ${vulkan-lib}
    ${log-lib}
    ${android-lib}
)
