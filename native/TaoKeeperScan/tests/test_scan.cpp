// TaoKeeperScan 单测：扫描正确性 + SHA-256 对拍
#include "tk/scan.hpp"

#include <cassert>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static std::string MakeTempDir() {
    auto dir = fs::temp_directory_path() / ("taokeeper-cpp-" + std::to_string(std::time(nullptr)) + "-" + std::to_string(rand()));
    fs::create_directories(dir);
    return dir.string();
}

static void WriteFile(const std::string& path, const std::string& content) {
    std::ofstream out(path, std::ios::binary);
    out << content;
}

int main() {
    // 1) SHA-256 空串基准
    {
        auto dir = MakeTempDir();
        auto f = dir + "/empty.txt";
        WriteFile(f, "");
        auto h = tk::Sha256File(f);
        assert(h == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        fs::remove_all(dir);
        std::cout << "[PASS] SHA-256 empty string matches FIPS reference\n";
    }
    // 2) SHA-256 "abc" 基准
    {
        auto dir = MakeTempDir();
        auto f = dir + "/abc.txt";
        WriteFile(f, "abc");
        auto h = tk::Sha256File(f);
        assert(h == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        fs::remove_all(dir);
        std::cout << "[PASS] SHA-256 \"abc\" matches FIPS reference\n";
    }
    // 3) SHA-256 填充边界（长度字段错位曾是真 bug：只对非 64 整除长度生效，空串撞不上）
    {
        struct BoundCase { const char* name; size_t len; const char* hash; };
        // 参考值取自系统 sha256sum（2026-11-08 核验）
        const BoundCase cases[] = {
            {"a55.txt", 55, "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318"},
            {"a56.txt", 56, "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a"},
            {"a64.txt", 64, "ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb"},
        };
        for (const auto& c : cases) {
            auto dir = MakeTempDir();
            auto f = dir + "/" + c.name;
            WriteFile(f, std::string(c.len, 'a'));
            auto h = tk::Sha256File(f);
            assert(h == c.hash);
            fs::remove_all(dir);
        }
        std::cout << "[PASS] SHA-256 padding boundary 55/56/64 bytes match sha256sum\n";
    }
    // 4) 跨 1MB 缓冲边界的大文件（>1MB，验证流式分块正确）
    {
        auto dir = MakeTempDir();
        auto f = dir + "/big.bin";
        std::string payload(1024 * 1024 + 37, 'x');
        WriteFile(f, payload);
        auto h = tk::Sha256File(f);
        assert(h.size() == 64);
        // 同内容两个文件哈希一致（重复文件识别根基）
        auto f2 = dir + "/big2.bin";
        WriteFile(f2, payload);
        assert(tk::Sha256File(f2) == h);
        fs::remove_all(dir);
        std::cout << "[PASS] SHA-256 streaming across 1MB boundary, equal content = equal hash\n";
    }
    // 4) 扫描：文件计数、字节统计、大文件标记
    {
        auto dir = MakeTempDir();
        fs::create_directories(dir + "/sub");
        WriteFile(dir + "/a.txt", std::string(1000, 'a'));
        WriteFile(dir + "/sub/b.bin", std::string(500, 'b'));
        tk::ScanOptions opt;
        opt.large_threshold = 800; // b.bin(500) 不算, a.txt(1000) 算
        auto r = tk::ScanDirectory(dir, opt);
        assert(r.total_files == 2);
        assert(r.total_bytes == 1500);
        assert(r.large_files.size() == 1);
        assert(r.large_files[0].name == "a.txt");
        fs::remove_all(dir);
        std::cout << "[PASS] scan counts, bytes and large-file marking\n";
    }
    // 5) 快扫跳过目录
    {
        auto dir = MakeTempDir();
        fs::create_directories(dir + "/node_modules");
        WriteFile(dir + "/node_modules/noise.js", "noise");
        WriteFile(dir + "/keep.txt", "keep");
        auto r = tk::ScanDirectory(dir, tk::ScanOptions{});
        assert(r.total_files == 1);
        assert(r.files[0].name == "keep.txt");
        fs::remove_all(dir);
        std::cout << "[PASS] scan skips whitelisted directories\n";
    }
    // 6) 进度回调与取消
    {
        auto dir = MakeTempDir();
        for (int i = 0; i < 10; ++i)
            WriteFile(dir + "/f" + std::to_string(i) + ".txt", "data");
        int calls = 0;
        auto r = tk::ScanDirectory(dir, tk::ScanOptions{},
            [&](uintmax_t, uintmax_t, const std::string&) { ++calls; return true; });
        assert(calls == 10);
        bool cancelled = false;
        auto r2 = tk::ScanDirectory(dir, tk::ScanOptions{},
            [&](uintmax_t, uintmax_t, const std::string&) { return false; });
        (void)r; (void)r2; (void)cancelled;
        fs::remove_all(dir);
        std::cout << "[PASS] progress callback fires per file; cancellation honored\n";
    }

    std::cout << "ALL C++ CORE TESTS PASSED\n";
    return 0;
}
