// TaoKeeperScan 可移植实现（Linux 验证；Windows NTFS MFT 直读另接平台分支）
#include "tk/scan.hpp"

#include <filesystem>
#include <fstream>
#include <array>
#include <algorithm>
#include <cctype>
#include <mutex>
#include <atomic>
#include <vector>

namespace fs = std::filesystem;

namespace tk {

namespace {

bool NameMatchesSkip(const std::string& name, const std::vector<std::string>& skip) {
    for (const auto& s : skip) {
        if (s.size() != name.size()) continue;
        bool eq = true;
        for (size_t i = 0; i < s.size(); ++i)
            if (std::tolower(static_cast<unsigned char>(s[i])) !=
                std::tolower(static_cast<unsigned char>(name[i]))) { eq = false; break; }
        if (eq) return true;
    }
    return false;
}

std::string ToHex(const unsigned char* data, size_t len) {
    static const char* digits = "0123456789abcdef";
    std::string out;
    out.reserve(len * 2);
    for (size_t i = 0; i < len; ++i) {
        out.push_back(digits[data[i] >> 4]);
        out.push_back(digits[data[i] & 0x0F]);
    }
    return out;
}

} // anonymous namespace

std::string Sha256File(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return {};

    // SHA-256 实现（FIPS 180-4）
    static constexpr uint32_t K[64] = {
        0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
    };

    auto rotr = [](uint32_t x, int n) { return (x >> n) | (x << (32 - n)); };

    uint32_t h[8] = {0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,
                     0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};

    std::array<unsigned char, 64> block{};
    std::array<unsigned char, 64> pending{};
    size_t pending_len = 0;
    uint64_t total_len = 0;
    auto process = [&]() {
        for (int i = 0; i < 64; ++i)
            block[i] = pending[i];
        uint32_t w[64];
        for (int i = 0; i < 16; ++i)
            w[i] = (uint32_t(block[i*4]) << 24) | (uint32_t(block[i*4+1]) << 16) |
                   (uint32_t(block[i*4+2]) << 8) | uint32_t(block[i*4+3]);
        for (int i = 16; i < 64; ++i) {
            uint32_t s0 = rotr(w[i-15],7) ^ rotr(w[i-15],18) ^ (w[i-15] >> 3);
            uint32_t s1 = rotr(w[i-2],17) ^ rotr(w[i-2],19) ^ (w[i-2] >> 10);
            w[i] = w[i-16] + s0 + w[i-7] + s1;
        }
        uint32_t a=h[0],b=h[1],c=h[2],d=h[3],e=h[4],f=h[5],g=h[6],hh=h[7];
        for (int i = 0; i < 64; ++i) {
            uint32_t S1 = rotr(e,6) ^ rotr(e,11) ^ rotr(e,25);
            uint32_t ch = (e & f) ^ (~e & g);
            uint32_t t1 = hh + S1 + ch + K[i] + w[i];
            uint32_t S0 = rotr(a,2) ^ rotr(a,13) ^ rotr(a,22);
            uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
            uint32_t t2 = S0 + maj;
            hh=g; g=f; f=e; e=d+t1; d=c; c=b; b=a; a=t1+t2;
        }
        h[0]+=a; h[1]+=b; h[2]+=c; h[3]+=d; h[4]+=e; h[5]+=f; h[6]+=g; h[7]+=hh;
    };

    // 2026-11-12 r6 修红：1MB 缓冲原为栈上 std::array，Windows 默认栈 1MB 直接爆栈（SEGFAULT，
    // 沙箱 Linux 8MB 栈掩盖）；改堆分配。
    std::vector<char> buf(1024 * 1024, 0);
    while (in.read(buf.data(), buf.size()) || in.gcount() > 0) {
        std::streamsize got = in.gcount();
        total_len += static_cast<uint64_t>(got);
        size_t off = 0;
        while (off < static_cast<size_t>(got)) {
            size_t take = std::min(64 - pending_len, static_cast<size_t>(got) - off);
            std::copy_n(buf.data() + off, take, pending.data() + pending_len);
            pending_len += take; off += take;
            if (pending_len == 64) { process(); pending_len = 0; }
        }
    }
    // padding
    unsigned char tail[72] = {};
    uint64_t bit_len = total_len * 8;
    tail[0] = 0x80;
    size_t tail_len = (pending_len < 56 ? 56 - pending_len : 120 - pending_len);
    for (size_t i = 0; i < 8; ++i)
        tail[tail_len + i] = static_cast<unsigned char>(bit_len >> (56 - 8*i));
    size_t total_tail = tail_len + 8;
    // feed tail through pending
    size_t off = 0;
    while (off < total_tail) {
        size_t take = std::min(64 - pending_len, total_tail - off);
        std::copy_n(tail + off, take, pending.data() + pending_len);
        pending_len += take; off += take;
        if (pending_len == 64) { process(); pending_len = 0; }
    }

    unsigned char digest[32];
    for (int i = 0; i < 8; ++i) {
        digest[i*4]   = static_cast<unsigned char>(h[i] >> 24);
        digest[i*4+1] = static_cast<unsigned char>(h[i] >> 16);
        digest[i*4+2] = static_cast<unsigned char>(h[i] >> 8);
        digest[i*4+3] = static_cast<unsigned char>(h[i]);
    }
    return ToHex(digest, 32);
}

ScanSummary ScanDirectory(const std::string& root,
                          const ScanOptions& options,
                          const ProgressFn& progress) {
    ScanSummary summary;
    std::atomic<uintmax_t> bytes_seen{0};
    std::atomic<size_t>    files_seen{0};
    std::error_code ec;

    auto walk = [&](auto&& self, const fs::path& dir) -> void {
        if (!NameMatchesSkip(dir.filename().string(), options.skip_dirs)) {
            fs::directory_iterator it(dir, fs::directory_options::skip_permission_denied, ec);
            if (ec) return;
            for (const auto& entry : it) {
                if (entry.is_directory(ec)) {
                    self(self, entry.path());
                } else if (entry.is_regular_file(ec)) {
                    auto sz = entry.file_size(ec);
                    if (ec) continue;
                    FileRecord rec;
                    rec.full_path = entry.path().string();
                    rec.name = entry.path().filename().string();
                    rec.size_bytes = sz;
                    rec.modified = decltype(rec.modified)(
                        std::filesystem::last_write_time(entry.path(), ec).time_since_epoch().count());
                    if (options.compute_hashes &&
                        (options.max_hash_size == 0 || sz <= options.max_hash_size)) {
                        rec.sha256 = Sha256File(rec.full_path);
                    }
                    bytes_seen += sz;
                    if (sz >= options.large_threshold) summary.large_files.push_back(rec);
                    summary.files.push_back(std::move(rec));
                    ++summary.total_files;
                    if (progress && !progress(files_seen.fetch_add(1) + 1, bytes_seen.load(), dir.string()))
                        return; // 取消
                }
            }
        }
    };

    walk(walk, fs::path(root));
    summary.total_bytes = bytes_seen.load();
    std::sort(summary.large_files.begin(), summary.large_files.end(),
              [](const FileRecord& a, const FileRecord& b) { return a.size_bytes > b.size_bytes; });
    return summary;
}

} // namespace tk
