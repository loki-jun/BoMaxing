cmake_minimum_required(VERSION 3.20)
project(bomaxing_driver_host LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(
    bomaxing-driver-host
    main.cpp
    shared_frame_pool.cpp)
target_include_directories(bomaxing-driver-host PRIVATE "${CMAKE_CURRENT_LIST_DIR}/../include")

if (MSVC)
    target_compile_options(bomaxing-driver-host PRIVATE /W4 /permissive-)
else()
    target_compile_options(bomaxing-driver-host PRIVATE -Wall -Wextra -Wpedantic)
endif()
