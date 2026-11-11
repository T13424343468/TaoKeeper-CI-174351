// TaoKeeperScan — MFT 顺序遍历与路径构建测试（合成记录，沙箱实测）
// 覆盖：$MFT 字节块顺序遍历（坏记录跳过不中断）、父链路径构建、缺失父/成环拒绝、
// 记录号与序列号剥离（低 48 位对齐）。
#include "tk/mft.hpp"

#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

using tk::BuildFullPath;
using tk::MftEntry;
using tk::MftFileName;
using tk::MftRecordNumber;
using tk::ParseMftFileName;
using tk::WalkMftRecords;

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

// 与 test_mft.cpp 同布局的合成记录：$STANDARD_INFORMATION + $FILE_NAME + USA fixup。
constexpr size_t kRecSize = 1024;
constexpr size_t kAttr1Off = 0x40;
constexpr size_t kAttr2Off = 0x88;
constexpr size_t kFnValueOff = 0x18;

static std::vector<uint8_t> BuildRecord(
    const std::u16string& name, uint64_t parent,
    uint64_t real, uint64_t allocated, bool is_dir) {
    std::vector<uint8_t> rec(kRecSize, 0);
    std::memcpy(rec.data(), "FILE", 4);
    Put16(rec, 0x14, kAttr1Off);
    Put16(rec, 0x30, 0x38);
    Put16(rec, 0x32, 3);
    Put32(rec, kAttr1Off, 0x10);
    Put32(rec, kAttr1Off + 4, 0x48);
    rec[kAttr1Off + 8] = 0;
    Put32(rec, kAttr1Off + 16, 0x30);
    Put16(rec, kAttr1Off + 20, 0x18);
    const size_t name_bytes = name.size() * 2;
    Put32(rec, kAttr2Off, 0x30);
    Put32(rec, kAttr2Off + 4, static_cast<uint32_t>(0x18 + 66 + name_bytes));
    rec[kAttr2Off + 8] = 0;
    Put32(rec, kAttr2Off + 16, static_cast<uint32_t>(66 + name_bytes));
    Put16(rec, kAttr2Off + 20, kFnValueOff);
    const size_t fn = kAttr2Off + kFnValueOff;
    Put64(rec, fn, parent);
    Put64(rec, fn + 40, allocated);
    Put64(rec, fn + 48, real);
    Put32(rec, fn + 56, is_dir ? 1 : 0);
    rec[fn + 64] = static_cast<uint8_t>(name.size());
    rec[fn + 65] = 1;
    for (size_t i = 0; i < name.size(); ++i)
        Put16(rec, fn + 66 + i * 2, static_cast<uint16_t>(name[i]));
    Put32(rec, kAttr2Off + 0x18 + 66 + name_bytes, 0xFFFFFFFFu);
    const uint16_t check = 0x1A2B;
    Put16(rec, 0x38, check);
    Put16(rec, 0x3A, 0x1112);
    Put16(rec, 0x3C, 0x1113);
    Put16(rec, 510, check);
    Put16(rec, 1022, check);
    return rec;
}

int main() {
    // 1. 顺序遍历：块 0–5 坏记录、块 6 目录、块 7 文件、块 8 坏记录、块 9 根下文件，
    //    加尾部残块。坏块跳过不中断，记录号必须等于块序号（父引用才能对上）。
    std::vector<uint8_t> blob;
    auto append = [&blob](const std::vector<uint8_t>& rec) {
        blob.insert(blob.end(), rec.begin(), rec.end());
    };
    auto bad = BuildRecord(u"损坏", 5, 1, 1, false);
    std::memcpy(bad.data(), "BAAD", 4);                         // 坏魔数 → 跳过
    auto r6 = BuildRecord(u"目录", 5, 0, 4096, true);            // 块 6：目录
    auto r7 = BuildRecord(u"说明书.txt", 6, 123456, 131072, false); // 块 7：父=记录 6
    auto r9 = BuildRecord(u"readme.md", 5, 2048, 4096, false);   // 块 9：根下文件
    for (int i = 0; i < 6; ++i) append(bad);
    append(r6); append(r7); append(bad); append(r9);
    blob.resize(blob.size() + 100); // 尾部残块应被忽略

    std::vector<MftEntry> entries;
    size_t skipped = 0;
    const size_t walked = WalkMftRecords(blob.data(), blob.size(), kRecSize, entries, skipped);
    CHECK(walked == 3, "三条有效记录应全部解析");
    CHECK(skipped == 7, "坏记录跳过并计数（含残块前整块）");
    CHECK(entries[0].record_no == 6 && entries[0].info.name_utf8 == "目录", "块序号即记录号");
    CHECK(entries[1].record_no == 7 && entries[1].info.real_size == 123456, "文件记录字段随行带出");
    CHECK(entries[2].record_no == 9 && entries[2].info.name_utf8 == "readme.md", "坏块之后记录继续解析");

    // 2. 路径构建：父链上溯拼出完整路径，根（记录 5）不拼入。
    std::map<uint64_t, MftEntry> tree;
    for (const auto& e : entries) tree[e.record_no] = e;
    std::string path;
    CHECK(BuildFullPath(tree, 7, path), "两级路径应构建成功");
    CHECK(path == "目录\\说明书.txt", "路径=父目录\\文件名");
    CHECK(BuildFullPath(tree, 9, path), "根下单级路径");
    CHECK(path == "readme.md", "根下文件名即为路径");

    // 3. 父引用带序列号（高 16 位非零）：按低 48 位对齐仍可上溯。
    std::map<uint64_t, MftEntry> seq_tree;
    for (auto e : entries) { // 拷贝以便改写测试数据
        if (e.record_no == 7) e.info.parent_ref = 6 | (0x0005ull << 48); // 序列号 5
        seq_tree[e.record_no] = e;
    }
    CHECK(BuildFullPath(seq_tree, 7, path), "带序列号的父引用应可上溯");
    CHECK(path == "目录\\说明书.txt", "序列号剥离后路径一致");

    // 4. 父链缺失（父记录不在映射内）：拒绝而非瞎拼。
    std::map<uint64_t, MftEntry> orphan;
    {
        auto e = entries[1];
        e.info.parent_ref = 99;
        orphan[7] = e;
    }
    CHECK(!BuildFullPath(orphan, 7, path), "缺失父必须拒绝");

    // 5. 父子互指成环：检测即拒绝，绝不死循环。
    std::map<uint64_t, MftEntry> loop;
    {
        auto a = BuildRecord(u"a", 11, 1, 1, false);
        auto b = BuildRecord(u"b", 10, 1, 1, false);
        MftFileName pa, pb;
        (void)ParseMftFileName(a.data(), a.size(), pa);
        (void)ParseMftFileName(b.data(), b.size(), pb);
        loop[10] = MftEntry{10, pa};
        loop[11] = MftEntry{11, pb};
    }
    CHECK(!BuildFullPath(loop, 10, path), "成环必须拒绝");

    // 6. 记录号剥序：MftRecordNumber 只留低 48 位。
    CHECK(MftRecordNumber(0x0005000000000042ull) == 0x42, "低 48 位为记录号");

    // 7. 目录体积聚合：嵌套子目录逐级上卷，根不在映射内时根直属文件不计孤儿。
    {
        std::map<uint64_t, MftEntry> agg;
        auto mk = [&agg](uint64_t no, const char16_t* name, uint64_t parent,
                         uint64_t real, bool dir) {
            auto r = BuildRecord(name, parent, real, 0, dir);
            MftFileName p;
            (void)ParseMftFileName(r.data(), r.size(), p);
            agg[no] = MftEntry{no, p};
        };
        mk(6, u"目录A", 5, 0, true);
        mk(7, u"子目录B", 6, 0, true);
        mk(8, u"f1.bin", 7, 100, false);
        mk(9, u"f2.bin", 6, 200, false);
        mk(10, u"readme.md", 5, 50, false); // 根直属，根（记录 5）不在映射内

        auto res = tk::AggregateDirSizes(agg);
        CHECK(res.per_record.at(6).recursive_size == 300, "父目录聚合=子目录+直属文件");
        CHECK(res.per_record.at(6).file_count == 2, "父目录文件计数");
        CHECK(res.per_record.at(7).recursive_size == 100, "子目录聚合只含自己链上文件");
        CHECK(res.per_record.count(10) == 0, "文件记录自身不出聚合");
        CHECK(res.orphan_size == 0 && res.orphan_files == 0, "走到根的部分不算孤儿");

        // 根记录在映射内：根直属文件也该卷到根
        mk(5, u"$卷根", 5, 0, true);
        auto res2 = tk::AggregateDirSizes(agg);
        CHECK(res2.per_record.at(5).recursive_size == 350, "根聚合=全卷可达文件");
        CHECK(res2.per_record.at(5).file_count == 3, "根文件计数");
        CHECK(res2.orphan_size == 0, "仍无孤儿");
    }

    // 8. 聚合孤儿：父链缺失与成环都进孤儿桶，绝不死循环。
    {
        std::map<uint64_t, MftEntry> weird;
        auto mk2 = [&weird](uint64_t no, const char16_t* name, uint64_t parent,
                            uint64_t real, bool dir) {
            auto r = BuildRecord(name, parent, real, 0, dir);
            MftFileName p;
            (void)ParseMftFileName(r.data(), r.size(), p);
            weird[no] = MftEntry{no, p};
        };
        mk2(30, u"孤儿", 99, 777, false);          // 父缺失
        mk2(40, u"环A", 41, 0, true);              // 环：40↔41
        mk2(41, u"环B", 40, 0, true);
        mk2(42, u"环内文件", 40, 888, false);      // 挂在环上的文件
        mk2(43, u"正常", 5, 10, false);            // 正常到根

        auto res = tk::AggregateDirSizes(weird);
        CHECK(res.orphan_size == 777 + 888, "孤儿=父缺失+成环两笔");
        CHECK(res.orphan_files == 2, "孤儿文件计数");
        CHECK(res.per_record.count(43) == 0, "正常到根且根缺席：无目录聚合可挂");
        CHECK(res.per_record.size() == 0, "无目录链成立时聚合为空");
    }

    std::printf(g_fail == 0 ? "ALL MFT TREE TESTS PASSED\n"
                            : "MFT TREE TESTS FAILED\n");
    std::printf("[INFO] pass=%d fail=%d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
