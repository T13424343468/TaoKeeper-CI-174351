// TaoKeeperScan — C++ 扫描核心（M1 磁盘模块的本地引擎）
// 平台抽象：Windows 真机可接 NTFS MFT 直读（tk::win 主分支，待真机）；
// 本沙箱用可移植 std::filesystem 路径实测算法正确性。
#pragma once

#include <string>
#include <vector>
#include <cstdint>
#include <ctime>
#include <functional>

namespace tk {

struct FileRecord {
    std::string full_path;
    std::string name;
    uintmax_t   size_bytes = 0;
    std::time_t modified = 0;
    std::string sha256;      // 仅 ComputeHashes=true 时填充
};

struct ScanOptions {
    bool        compute_hashes      = false;
    uintmax_t   large_threshold     = 512ull * 1024 * 1024;
    uintmax_t   max_hash_size       = 2ull * 1024 * 1024 * 1024; // 0 = 无上限
    std::vector<std::string> skip_dirs = {
        "Windows", "$RECYCLE.BIN", "System Volume Information",
        "node_modules", ".git", ".svn", "__pycache__"
    };
};

struct ScanSummary {
    std::vector<FileRecord> files;
    std::vector<FileRecord> large_files;   // 按 size 降序
    uintmax_t total_bytes = 0;
    size_t    total_files = 0;
};

// 进度回调：files_seen, bytes_seen, current_dir；返回 false 表示请求取消
using ProgressFn = std::function<bool(uintmax_t, uintmax_t, const std::string&)>;

/// <summary>只读目录遍历扫描；不修改、不移动、不删除任何文件。</summary>
ScanSummary ScanDirectory(const std::string& root,
                          const ScanOptions& options,
                          const ProgressFn& progress = {});

/// <summary>SHA-256 流式哈希（1MB 缓冲，不整读进内存）；读失败返回空串。</summary>
std::string Sha256File(const std::string& path);

} // namespace tk
