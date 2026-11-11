// TaoKeeperScan — $MFT 连续读取解码器测试（分块喂入、跨块拼接、起始跳过、坏记录占号、C ABI）
#include "tk/mft.hpp"
#include "tk/interop_abi.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using tk::MftEntry;
using tk::MftStreamDecoder;

static int g_pass = 0, g_fail = 0;
#define CHECK(cond, msg) do { \
    if (cond) { ++g_pass; } \
    else { ++g_fail; std::printf("[FAIL] %s\n", msg); } } while (0)

static void Put16(std::vector<uint8_t>& b, size_t off, uint16_t v) {
    b[off] = v & 0xFF; b[off + 1] = (v >> 8) & 0xFF;
}
static void Put32(std::vector<uint8_t>& b, size_t off, uint32_t v) {
    for (int i = 0; i < 4; ++i) b[off + i] = (v >> (8 * i)) & 0xFF;
}
static void Put64(std::vector<uint8_t>& b, size_t off, uint64_t v) {
    for (int i = 0; i < 8; ++i) b[off + i] = (v >> (8 * i)) & 0xFF;
}

constexpr size_t kRecSize = 1024;
constexpr size_t kAttr2Off = 0x88;
constexpr size_t kFnValueOff = 0x18;

/// 与 test_mft.cpp 同布局的合成记录（$FILE_NAME 名/父/大小可配）。
static std::vector<uint8_t> BuildRecord(
    const std::u16string& name, uint64_t parent,
    uint64_t real, uint64_t allocated, bool is_dir) {
    std::vector<uint8_t> rec(kRecSize, 0);
    std::memcpy(rec.data(), "FILE", 4);
    Put16(rec, 0x14, 0x40);
    Put16(rec, 0x30, 0x38);
    Put16(rec, 0x32, 3);
    Put32(rec, 0x40, 0x10);
    Put32(rec, 0x44, 0x48);
    Put32(rec, 0x50, 0x30);
    Put16(rec, 0x54, 0x18);

    const size_t name_bytes = name.size() * 2;
    Put32(rec, kAttr2Off, 0x30);
    Put32(rec, kAttr2Off + 4, static_cast<uint32_t>(0x18 + 66 + name_bytes));
    Put32(rec, kAttr2Off + 16, static_cast<uint32_t>(66 + name_bytes));
    Put16(rec, kAttr2Off + 20, kFnValueOff);
    const size_t fn = kAttr2Off + kFnValueOff;
    Put64(rec, fn, parent);
    Put64(rec, fn + 8, 132000000000000000ull);
    Put64(rec, fn + 16, 132000000001000000ull);
    Put64(rec, fn + 40, allocated);
    Put64(rec, fn + 48, real);
    Put32(rec, fn + 56, is_dir ? 1 : 0);
    rec[fn + 64] = static_cast<uint8_t>(name.size());
    rec[fn + 65] = 1;
    for (size_t i = 0; i < name.size(); ++i)
        Put16(rec, fn + 66 + i * 2, static_cast<uint16_t>(name[i]));
    Put32(rec, kAttr2Off + 0x18 + 66 + name_bytes, 0xFFFFFFFFu);

    Put16(rec, 0x38, 0x1A2B);
    Put16(rec, 0x3A, 0x1112);
    Put16(rec, 0x3C, 0x1113);
    Put16(rec, 510, 0x1A2B);
    Put16(rec, 1022, 0x1A2B);
    return rec;
}

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);

    // 素材四块：0=甲.txt(父5)、1=目录A(父5)、2=损坏版目录A（fixup 不符）、3=乙.bin(父1)
    std::vector<uint8_t> blob;
    auto append = [&](std::vector<uint8_t> rec) {
        blob.insert(blob.end(), rec.begin(), rec.end());
    };
    append(BuildRecord(u"甲.txt", 5, 100, 4096, false));
    append(BuildRecord(u"目录A", 5, 0, 8192, true));
    {
        auto bad = BuildRecord(u"目录A", 5, 0, 8192, true);
        bad[510] = 0x99; bad[511] = 0x99;
        append(bad);
    }
    append(BuildRecord(u"乙.bin", 1, 999999, 1048576, false));

    // 1. 分块喂入（每块 333 字节，跨块拼接残记录）应与整块遍历同结果
    {
        MftStreamDecoder dec(kRecSize);
        size_t total_skipped = 0;
        size_t off = 0;
        while (off < blob.size()) {
            const size_t n = blob.size() - off < 333 ? blob.size() - off : 333;
            size_t sk = 0;
            dec.Feed(blob.data() + off, n, sk);
            total_skipped += sk;
            off += n;
        }
        CHECK(dec.PendingEntries() == 3, "三条好记录应全部入队");
        CHECK(dec.PendingBytes() == 0, "整除块长不留残块");
        CHECK(dec.NextRecordNo() == 4, "坏记录也占号（0..3）");
        CHECK(total_skipped == 1, "损坏记录计一次 skipped");

        std::vector<MftEntry> taken;
        CHECK(dec.Take(taken) == 3, "Take 全部取走");
        CHECK(taken[0].record_no == 0 && taken[0].info.name_utf8 == "甲.txt", "记录0 内容");
        CHECK(taken[1].record_no == 1 && taken[1].info.is_directory, "记录1 目录A（好记录）");
        CHECK(taken[2].record_no == 3 && taken[2].info.name_utf8 == "乙.bin", "记录2 坏占号，记录3 乙.bin 不断档");
        CHECK(taken[2].info.parent_ref == 1, "父引用跨块还原");
        CHECK(dec.Take(taken) == 0, "取空后再取为 0");
    }

    // 2. 与整块遍历同口径对拍（解析不得改写输入：同一缓冲先后两种方式结果必须一致）
    {
        std::vector<MftEntry> walked;
        size_t walked_skipped = 0;
        tk::WalkMftRecords(blob.data(), blob.size(), kRecSize, walked, walked_skipped);

        MftStreamDecoder dec(kRecSize);
        size_t sk = 0;
        dec.Feed(blob.data(), blob.size(), sk);
        std::vector<MftEntry> streamed;
        dec.Take(streamed);
        CHECK(walked.size() == streamed.size(), "条数一致");
        CHECK(walked_skipped == 1 && sk == 1, "坏块数一致");
        bool same = walked.size() == streamed.size();
        for (size_t i = 0; i < walked.size() && same; ++i) {
            same = walked[i].record_no == streamed[i].record_no
                && walked[i].info.name_utf8 == streamed[i].info.name_utf8
                && walked[i].info.parent_ref == streamed[i].info.parent_ref;
        }
        CHECK(same, "逐条字段一致");

        // 回归：fixup 不得改写输入缓冲——先走一遍解析，再原样重跑同一缓冲，结果不变。
        std::vector<MftEntry> walked2;
        size_t walked2_skipped = 0;
        tk::WalkMftRecords(blob.data(), blob.size(), kRecSize, walked2, walked2_skipped);
        CHECK(walked2.size() == walked.size() && walked2_skipped == walked_skipped, "重复解析同一缓冲结果不变");
    }

    // 3. 起始偏移跳过（流前缀 17 字节非记录数据）
    {
        std::vector<uint8_t> prefixed(17, 0xAB);
        prefixed.insert(prefixed.end(), blob.begin(), blob.end());
        MftStreamDecoder dec(kRecSize);
        dec.SetStartOffset(17);
        size_t sk = 0;
        dec.Feed(prefixed.data(), prefixed.size(), sk);
        std::vector<MftEntry> taken;
        dec.Take(taken);
        CHECK(taken.size() == 3, "跳过前缀后条数不变");
        CHECK(taken[0].record_no == 0, "前缀不影响记录号从 0 起");
    }

    // 4. 单字节喂入极限（逐字节拼接）
    {
        MftStreamDecoder dec(kRecSize);
        for (size_t i = 0; i < blob.size(); ++i) {
            size_t sk = 0;
            dec.Feed(blob.data() + i, 1, sk);
        }
        CHECK(dec.PendingEntries() == 3, "逐字节喂入结果一致");
        std::vector<MftEntry> taken;
        dec.Take(taken);
        CHECK(taken[0].info.name_utf8 == "甲.txt", "逐字节拼出完整记录");
    }

    // 5. C ABI：create/feed/take/pending/destroy 与空参防护
    {
        void* h = tk_mft_stream_create(0);
        CHECK(h == nullptr, "record_size=0 拒绝建流");
        h = tk_mft_stream_create(kRecSize);
        CHECK(h != nullptr, "正常建流");
        CHECK(tk_mft_stream_next_record_no(h) == 0, "未喂入时下一条记录号为 0");
        CHECK(tk_mft_stream_next_record_no(nullptr) == 0, "空句柄记录号查询拒绝");
        std::vector<uint8_t> prefixed(17, 0xAB);
        prefixed.insert(prefixed.end(), blob.begin(), blob.end());
        tk_mft_stream_set_start_offset(h, 17);
        size_t sk = 0;
        const size_t pend = tk_mft_stream_feed(h, prefixed.data(), prefixed.size(), &sk);
        CHECK(pend == 3 && sk == 1, "ABI feed 产出与跳过计数");
        CHECK(tk_mft_stream_pending(h) == 3, "ABI pending");
        CHECK(tk_mft_stream_next_record_no(h) == 4, "喂完四条（含坏占号）后下一条记录号为 4");

        std::vector<TkMftEntry> flat(3);
        CHECK(tk_mft_stream_take(h, flat.data(), 2) == 2, "ABI take 容量截取");
        CHECK(tk_mft_stream_pending(h) == 1, "余量仍在队");
        CHECK(tk_mft_stream_take(h, flat.data() + 2, 2) == 1, "余下再取");
        CHECK(tk_mft_stream_take(h, flat.data(), 2) == 0, "取空");

        CHECK(std::string(flat[0].info.name_utf8) == "甲.txt", "ABI 平坦名还原");
        CHECK(flat[0].info.real_size == 100, "ABI 大小还原");
        CHECK(flat[2].info.parent_ref == 1, "ABI 父引用还原");

        CHECK(tk_mft_stream_feed(nullptr, blob.data(), 10, &sk) == 0, "空句柄拒绝");
        CHECK(tk_mft_stream_take(nullptr, flat.data(), 1) == 0, "空句柄 take 拒绝");
        tk_mft_stream_destroy(h);
        tk_mft_stream_destroy(nullptr); // 释放空句柄不炸
    }

    std::printf(g_fail == 0 ? "ALL MFT STREAM TESTS PASSED\n"
                            : "MFT STREAM TESTS FAILED\n");
    std::printf("[INFO] pass=%d fail=%d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
