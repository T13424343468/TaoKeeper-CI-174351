cmake_minimum_required(VERSION 3.20)
project(TaoKeeperScan CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(taokeeperscan src/scan.cpp src/mft.cpp)
target_include_directories(taokeeperscan PUBLIC include)

# 共享库：给 .NET P/Invoke 的 C ABI 导出层（沙箱互操作实测用；Windows 真机出 TaoKeeperScan.dll）
add_library(taokeeperscan_shared SHARED src/scan.cpp src/mft.cpp src/interop.cpp)
target_include_directories(taokeeperscan_shared PUBLIC include)
set_target_properties(taokeeperscan_shared PROPERTIES OUTPUT_NAME TaoKeeperScan)
# 2026-11-12 云真机修红：MSVC 需显式导出 C ABI 符号（LNK2019），构建 DLL 目标时开 dllexport
target_compile_definitions(taokeeperscan_shared PRIVATE TK_BUILD_SHARED)
if(MSVC)
  # 测试源码含中文字面量，统一按 UTF-8 编译（修 C4566 码页警告，非压制）
  add_compile_options(/utf-8)
endif()

enable_testing()
add_executable(test_scan tests/test_scan.cpp)
target_link_libraries(test_scan PRIVATE taokeeperscan)
add_test(NAME scan_core COMMAND test_scan)

add_executable(test_mft tests/test_mft.cpp)
target_link_libraries(test_mft PRIVATE taokeeperscan)
add_test(NAME mft_parser COMMAND test_mft)

add_executable(test_mft_tree tests/test_mft_tree.cpp)
target_link_libraries(test_mft_tree PRIVATE taokeeperscan)
add_test(NAME mft_tree COMMAND test_mft_tree)

add_executable(test_mft_stream tests/test_mft_stream.cpp)
target_link_libraries(test_mft_stream PRIVATE taokeeperscan_shared)
add_test(NAME mft_stream COMMAND test_mft_stream)

add_executable(test_mft_agg_incr tests/test_mft_agg_incr.cpp)
target_link_libraries(test_mft_agg_incr PRIVATE taokeeperscan_shared)
add_test(NAME mft_agg_incr COMMAND test_mft_agg_incr)
