// TaoKeeperScan — MFT 记录解析器测试（合成记录，沙箱实测解析正确性）
// 布局对齐真实 NTFS 记录：头 0x00–0x37，USA 阵列 0x38–0x3D，属性自 0x40 起。
#include "tk/mft.hpp"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using tk::MftFileName;
using tk::ParseMftFileName;
using tk::Utf16LeToUtf8;

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

// 记录布局常量：头到 USA 阵列固定，属性区从 0x40 开始。
constexpr size_t kRecSize = 1024;
constexpr size_t kAttr1Off = 0x40;
constexpr size_t kAttr2Off = 0x88;   // attr1 长 0x48
constexpr size_t kFnValueOff = 0x18; // $FILE_NAME 值相对属性头

/// <summary>构造带 USA fixup 的有效记录：$STANDARD_INFORMATION + $FILE_NAME + 结束标记。
/// USA：扇区尾两字节 = 校验值 0x1A2B，阵列中存真值（fixup 应用后被还原）。</summary>
static std::vector<uint8_t> BuildRecord(
    const std::u16string& name, uint64_t parent,
    uint64_t real, uint64_t allocated, bool is_dir) {
    std::vector<uint8_t> rec(kRecSize, 0);
    std::memcpy(rec.data(), "FILE", 4);
    Put16(rec, 0x14, kAttr1Off);   // 首属性偏移：USA 阵列之后
    Put16(rec, 0x30, 0x38);        // USA 偏移
    Put16(rec, 0x32, 3);           // USA 值数 = 1 校验 + 2 扇区

    // 属性 1：$STANDARD_INFORMATION（遍历应跳过）
    Put32(rec, kAttr1Off, 0x10);
    Put32(rec, kAttr1Off + 4, 0x48);
    rec[kAttr1Off + 8] = 0;        // resident
    Put32(rec, kAttr1Off + 16, 0x30);
    Put16(rec, kAttr1Off + 20, 0x18);

    // 属性 2：$FILE_NAME（resident）
    const size_t name_bytes = name.size() * 2;
    Put32(rec, kAttr2Off, 0x30);
    Put32(rec, kAttr2Off + 4, static_cast<uint32_t>(0x18 + 66 + name_bytes));
    rec[kAttr2Off + 8] = 0;
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
    rec[fn + 65] = 1; // Win32 命名空间
    for (size_t i = 0; i < name.size(); ++i)
        Put16(rec, fn + 66 + i * 2, static_cast<uint16_t>(name[i]));
    Put32(rec, kAttr2Off + 0x18 + 66 + name_bytes, 0xFFFFFFFFu); // 属性链结束

    // USA：阵列偏移 0x38 = 校验值，后跟两个扇区真值；扇区尾写入校验值。
    const uint16_t check = 0x1A2B;
    Put16(rec, 0x38, check);
    Put16(rec, 0x3A, 0x1112);
    Put16(rec, 0x3C, 0x1113);
    Put16(rec, 510, check);
    Put16(rec, 1022, check);
    return rec;
}

int main() {
    // 1. 中文名与各字段解析
    {
        auto rec = BuildRecord(u"说明书.txt", 5, 123456, 131072, false);
        MftFileName out;
        CHECK(ParseMftFileName(rec.data(), rec.size(), out), "中文名记录应解析成功");
        CHECK(out.name_utf8 == "说明书.txt", "UTF-16LE 名应转 UTF-8");
        CHECK(out.parent_ref == 5, "父目录引用");
        CHECK(out.real_size == 123456, "实际大小");
        CHECK(out.allocated_size == 131072, "分配大小");
        CHECK(!out.is_directory, "非目录标志");
        CHECK(out.modified_100ns == 132000000001000000ull, "修改时间");
    }

    // 2. fixup 生效：文件名最后一字的两字节恰好落在第 1 扇区尾，
    //    记录中是校验值，正确实现应按 USA 阵列真值还原。
    {
        const std::u16string name = u"甲乙丙"; // 丙 = U+4E19
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

        // $FILE_NAME 值位移 304：名字区最后两字节落在 510–511。
        constexpr size_t kCrossValueOff = 304;
        const size_t attr2 = kAttr2Off;
        const size_t attr_len = 0x18 + 66 + 6;
        Put32(rec, attr2, 0x30);
        Put32(rec, attr2 + 4, static_cast<uint32_t>(attr_len));
        rec[attr2 + 8] = 0;
        Put32(rec, attr2 + 16, 72);
        Put16(rec, attr2 + 20, kCrossValueOff);
        const size_t fn = attr2 + kCrossValueOff; // = 440
        Put64(rec, fn, 5);
        rec[fn + 64] = 3;
        rec[fn + 65] = 1;
        Put16(rec, fn + 66, 0x7532); // 甲
        Put16(rec, fn + 68, 0x4E59); // 乙
        Put32(rec, attr2 + attr_len, 0xFFFFFFFFu); // 属性链结束

        // USA：名字最后一字 丙 = 0x4E19 作为真值；扇区尾当前是校验值。
        Put16(rec, 0x38, 0x1A2B);
        Put16(rec, 0x3A, 0x4E19); // 丙
        Put16(rec, 0x3C, 0x1113);
        Put16(rec, 510, 0x1A2B);  // 校验值占位（覆盖名字最后一字）
        Put16(rec, 1022, 0x1A2B);

        MftFileName out;
        CHECK(ParseMftFileName(rec.data(), rec.size(), out), "跨扇区名应解析成功");
        CHECK(out.name_utf8 == "甲乙丙", "fixup 应还原被校验值覆盖的字节");
    }

    // 3. 目录标志
    {
        auto rec = BuildRecord(u"目录", 2, 0, 4096, true);
        MftFileName out;
        CHECK(ParseMftFileName(rec.data(), rec.size(), out), "目录记录应解析成功");
        CHECK(out.is_directory, "目录标志位");
        CHECK(out.name_utf8 == "目录", "中文目录名");
    }

    // 4. 坏魔数拒绝
    {
        auto rec = BuildRecord(u"x.txt", 5, 1, 1, false);
        rec[0] = 'F'; rec[1] = 'A'; rec[2] = 'K'; rec[3] = 'E';
        MftFileName out;
        CHECK(!ParseMftFileName(rec.data(), rec.size(), out), "坏魔数必须拒绝");
    }

    // 5. fixup 校验不符拒绝（扇区尾占位与校验值不一致 = 记录损坏）
    {
        auto rec = BuildRecord(u"x.txt", 5, 1, 1, false);
        rec[510] = 0x99; rec[511] = 0x99;
        MftFileName out;
        CHECK(!ParseMftFileName(rec.data(), rec.size(), out), "损坏记录必须拒绝");
    }

    // 6. 长度不足拒绝
    {
        auto rec = BuildRecord(u"x.txt", 5, 1, 1, false);
        MftFileName out;
        CHECK(!ParseMftFileName(rec.data(), 8, out), "截断记录必须拒绝");
    }

    // 7. UTF-8 转换：增补平面字符（代理对）
    {
        const uint8_t surrogate[4] = { 0x3D, 0xD8, 0x00, 0xDE }; // U+1F600
        CHECK(Utf16LeToUtf8(surrogate, 4) == "\xF0\x9F\x98\x80", "代理对应转 4 字节 UTF-8");
    }

    std::printf(g_fail == 0 ? "ALL MFT PARSER TESTS PASSED\n"
                            : "MFT PARSER TESTS FAILED\n");
    std::printf("[INFO] pass=%d fail=%d\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
