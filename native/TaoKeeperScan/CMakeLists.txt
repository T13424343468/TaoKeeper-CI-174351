cmake_minimum_required(VERSION 3.20)
project(TaoKeeperScan CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(taokeeperscan src/scan.cpp src/mft.cpp)
target_include_directories(taokeeperscan PUBLIC include)
# 2026-11-12 r5 修红：Windows 大小写不敏感文件系统上 taokeepsscan.lib 与共享库导入库
# TaoKeeperScan.lib 同名互相覆盖（导入库后建，覆盖静态库），全部静态测试 LNK2019；
# 静态库改名，与导入库（跟随 DLL 名 TaoKeeperScan）区分。
set_target_properties(taokeeperscan PROPERTIES OUTPUT_NAME TaoKeeperScanStatic)

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
# 2026-11-12 r4 修红：测试同时用 tk:: C++ 封装类（静态库）与 tk_mft_stream_* C ABI（DLL 导入库）；
# TK_BUILD_SHARED 仅 PRIVATE 给 DLL，C++ 类不进导入库，需双链接。运行期把 DLL 目录加进 PATH。
target_link_libraries(test_mft_stream PRIVATE taokeeperscan_shared taokeeperscan)
add_test(NAME mft_stream COMMAND test_mft_stream)
set_tests_properties(mft_stream PROPERTIES ENVIRONMENT "PATH=$<TARGET_FILE_DIR:taokeeperscan_shared>\;$ENV{PATH}")

add_executable(test_mft_agg_incr tests/test_mft_agg_incr.cpp)
target_link_libraries(test_mft_agg_incr PRIVATE taokeeperscan_shared taokeeperscan)
add_test(NAME mft_agg_incr COMMAND test_mft_agg_incr)
set_tests_properties(mft_agg_incr PROPERTIES ENVIRONMENT "PATH=$<TARGET_FILE_DIR:taokeeperscan_shared>\;$ENV{PATH}")
