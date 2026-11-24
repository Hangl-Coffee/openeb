cmake_minimum_required(VERSION 3.20)
project(mv_player CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Point CMAKE_PREFIX_PATH to the OpenEB install prefix (dist) so that find_package can locate MetavisionSDK.
find_package(MetavisionSDK COMPONENTS stream REQUIRED)

add_library(mv_player SHARED mv_player.cpp)
target_link_libraries(mv_player PRIVATE MetavisionSDK::stream)
if(WIN32)
    target_link_libraries(mv_player PRIVATE winmm)
endif()

add_executable(mvp_selftest mvp_selftest.cpp)
target_link_libraries(mvp_selftest PRIVATE mv_player)
