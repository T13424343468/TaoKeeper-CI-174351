// TaoKeeperScan — 分批增量聚合器测试：与一次性聚合对拍（同数据同口径）、乱序/成环/父缺失/快照稳定
// 运行环境：沙箱与 Windows 真机均可（纯逻辑，无文件系统依赖）。
#include "tk/mft.hpp"
#include "tk/interop_abi.h"

#include <algorithm>
#include <cstdio>
#include <map>
#include <random>
#include <vector>

namespace {

int g_fail = 0;
void Check(bool ok, const char* what) {
    if (!ok) {
        std::printf("[FAIL] %s\n", what);
        ++g_fail;
    }
}

tk::MftEntry MakeEntry(uint64_t record, uint64_t parent, uint64_t size, bool is_dir,
                       uint64_t seq = 0) {
    tk::MftEntry e;
    e.record_no = record;
    e.info.parent_ref = parent | (seq << 48); // NTFS 引用：低 48 位记录号，高 16 位序列号
    e.info.real_size = size;
    e.info.is_directory = is_dir;
    e.info.name_utf8 = "n" + std::to_string(seq);
    return e;
}

/// <summary>构造一棵正常树：root(5) 下 dir10/dir11，dir10 下 dir20 与两个文件，dir11 一个文件…</summary>
std::vector<tk::MftEntry> MakeNormalTree() {
    return {
        MakeEntry(5, 5, 0, true, 0),        // 卷根（父自指）
        MakeEntry(10, 5, 0, true, 1),
        MakeEntry(11, 5, 0, true, 2),
        MakeEntry(20, 10, 0, true, 3),
        MakeEntry(30, 20, 100, false, 4),   // 100
        MakeEntry(31, 20, 200, false, 5),   // 200
        MakeEntry(32, 10, 50, false, 6),    // 50 → dir10 直接
        MakeEntry(33, 5, 70, false, 7),     // 70 → 卷根直接
        MakeEntry(34, 11, 30, false, 8),    // 30
    };
}

tk::MftAggregateResult OneShot(const std::vector<tk::MftEntry>& entries) {
    std::map<uint64_t, tk::MftEntry> map;
    for (const auto& e : entries) map[e.record_no] = e;
    return tk::AggregateDirSizes(map, 5);
}

bool SameAggregates(const tk::MftAggregateResult& a, const tk::MftAggregateResult& b) {
    if (a.per_record.size() != b.per_record.size()) return false;
    if (a.orphan_size != b.orphan_size || a.orphan_files != b.orphan_files) return false;
    auto ia = a.per_record.begin();
    auto ib = b.per_record.begin();
    for (; ia != a.per_record.end(); ++ia, ++ib) {
        if (ia->first != ib->first) return false;
        if (ia->second.recursive_size != ib->second.recursive_size) return false;
        if (ia->second.file_count != ib->second.file_count) return false;
    }
    return true;
}

/// <summary>用随机打乱的批次（乱序父后于子）喂入增量聚合器，与一次性聚合对拍。</summary>
void CheckParity(const std::vector<tk::MftEntry>& entries, unsigned seed) {
    std::vector<tk::MftEntry> shuffled = entries;
    std::mt19937 rng(seed);
    std::shuffle(shuffled.begin(), shuffled.end(), rng);

    tk::MftIncrementalAggregator agg(5);
    const size_t batch = 3; // 小批次多批次喂入
    for (size_t i = 0; i < shuffled.size(); i += batch) {
        std::vector<tk::MftEntry> slice(shuffled.begin() + static_cast<long>(i),
                                        shuffled.begin() + static_cast<long>(std::min(i + batch, shuffled.size())));
        agg.Feed(slice);
    }
    Check(SameAggregates(agg.TakeSnapshot(), OneShot(entries)), "对拍：随机乱序批次与一次性聚合一致");
}

} // namespace

int main() {
    // 1) 正常树：乱序批次对拍多次（不同随机种子）
    for (unsigned seed : {1u, 7u, 42u, 99u}) CheckParity(MakeNormalTree(), seed);

    // 2) 父后到（子文件先喂、目录后喂）：待父桶归并后与一次性聚合一致
    {
        auto entries = MakeNormalTree();
        tk::MftIncrementalAggregator agg(5);
        for (const auto& e : entries) agg.Feed(e); // 顺序喂入但文件先于父目录（先 30/31 后 20/10/11/5）
        auto snap = agg.TakeSnapshot();
        auto expect = OneShot(entries);
        Check(SameAggregates(snap, expect), "父后到：待父桶归并与一次性聚合一致");
        Check(snap.per_record.count(20) && snap.per_record[20].recursive_size == 300, "dir20 递归 300");
        Check(snap.per_record[5].recursive_size == 450, "卷根递归 450（100+200+50+70+30）");
        Check(snap.orphan_size == 0 && snap.orphan_files == 0, "正常树无孤儿");
    }

    // 3) 快照非破坏、可重复：连取两次结果一致，继续喂入后快照推进
    {
        tk::MftIncrementalAggregator agg(5);
        auto half = MakeNormalTree();
        half.resize(5); // 前 5 条：root/10/11/20/30
        agg.Feed(half);
        auto s1 = agg.TakeSnapshot();
        auto s2 = agg.TakeSnapshot();
        Check(SameAggregates(s1, s2), "快照非破坏：连取两次一致");
        Check(s1.per_record[10].recursive_size == 100, "喂一半：dir10 递归 100（30 已挂 20）");
        for (size_t i = 5; i < MakeNormalTree().size(); ++i) agg.Feed(MakeNormalTree()[i]);
        auto s3 = agg.TakeSnapshot();
        Check(SameAggregates(s3, OneShot(MakeNormalTree())), "继续喂入后快照与一次性聚合一致");
    }

    // 4) 父缺失：孤儿记账（体积与文件数）；对应目录断开不进产出
    {
        std::vector<tk::MftEntry> entries = {
            MakeEntry(5, 5, 0, true, 0),
            MakeEntry(10, 5, 0, true, 1),
            MakeEntry(30, 10, 100, false, 2),
            MakeEntry(31, 40, 60, false, 3),   // 父 40 不存在 → 孤儿
            MakeEntry(50, 60, 10, true, 4),    // 目录 50 父 60 缺失 → 断开子树
            MakeEntry(51, 50, 20, false, 5),   // 51 的体积经 50 计孤儿
        };
        tk::MftIncrementalAggregator agg(5);
        agg.Feed(entries);
        auto snap = agg.TakeSnapshot();
        auto expect = OneShot(entries);
        Check(SameAggregates(snap, expect), "父缺失/断开子树：孤儿记账与一次性聚合一致");
        Check(snap.orphan_size == 60 + 20, "孤儿体积 80（60 直挂 + 20 断开子树）");
        Check(snap.orphan_files == 2, "孤儿文件数 2");
        Check(!snap.per_record.count(50), "断开目录不进产出");
        Check(snap.per_record[10].recursive_size == 100, "连通目录照常聚合");
    }

    // 5) 目录成环：环上目录断开计孤儿，环外照常
    {
        std::vector<tk::MftEntry> entries = {
            MakeEntry(5, 5, 0, true, 0),
            MakeEntry(10, 5, 0, true, 1),
            MakeEntry(30, 10, 100, false, 2),
            MakeEntry(70, 80, 0, true, 3),
            MakeEntry(80, 70, 0, true, 4),     // 70↔80 成环
            MakeEntry(81, 70, 40, false, 5),   // 环内文件 → 孤儿
        };
        tk::MftIncrementalAggregator agg(5);
        agg.Feed(entries);
        auto snap = agg.TakeSnapshot();
        auto expect = OneShot(entries);
        Check(SameAggregates(snap, expect), "成环：孤儿记账与一次性聚合一致");
        Check(snap.orphan_size == 40 && snap.orphan_files == 1, "环内文件计孤儿");
        Check(snap.per_record[10].recursive_size == 100, "环外目录照常聚合");
        Check(snap.per_record.count(5) == 1, "卷根（映射内）也计入产出");
    }

    // 6) 根不存在于条目中：父链到 root 记录号即连通（与一次性口径一致）
    {
        std::vector<tk::MftEntry> entries = {
            MakeEntry(10, 5, 0, true, 1),
            MakeEntry(30, 10, 100, false, 2),
        };
        tk::MftIncrementalAggregator agg(5);
        agg.Feed(entries);
        auto snap = agg.TakeSnapshot();
        Check(snap.per_record.count(10) == 1 && snap.per_record[10].recursive_size == 100, "无根条目：父链到卷根即连通");
        Check(snap.orphan_size == 0, "无根条目：无孤儿");
    }

    // 7) 空目录排除：无文件贡献的连通目录不进产出（与一次性聚合同口径）
    {
        std::vector<tk::MftEntry> entries = {
            MakeEntry(5, 5, 0, true, 0),
            MakeEntry(10, 5, 0, true, 1),
            MakeEntry(11, 5, 0, true, 2),      // 空目录：无文件贡献
            MakeEntry(30, 10, 100, false, 3),
        };
        tk::MftIncrementalAggregator agg(5);
        agg.Feed(entries);
        auto snap = agg.TakeSnapshot();
        Check(SameAggregates(snap, OneShot(entries)), "空目录排除：与一次性聚合对拍一致");
        Check(!snap.per_record.count(11), "空目录不进产出");
        Check(snap.per_record.count(10) == 1 && snap.per_record[10].recursive_size == 100, "有贡献目录照常输出");
    }

    // ---- C ABI：创建/喂入/快照（缓冲重试）/销毁、空句柄防护 ----
    {
        // 构造同布局平坦数组（父引用带序列号）
        auto entries = MakeNormalTree();
        std::vector<TkMftEntry> flat;
        for (const auto& e : entries) {
            TkMftEntry f{};
            f.record_no = e.record_no;
            f.info.parent_ref = e.info.parent_ref;
            f.info.real_size = e.info.real_size;
            f.info.is_directory = e.info.is_directory ? 1u : 0u;
            flat.push_back(f);
        }

        void* h = tk_mft_agg_incr_create(5);
        Check(h != nullptr, "ABI：create 返回句柄");
        Check(tk_mft_agg_incr_feed(nullptr, flat.data(), flat.size()) == 0, "ABI：空句柄 feed 拒绝");
        Check(tk_mft_agg_incr_feed(h, nullptr, 3) == 0, "ABI：空指针批次拒绝");
        Check(tk_mft_agg_incr_feed(h, flat.data(), flat.size()) == 1, "ABI：整批喂入成功");

        // 缓冲不足：返回 0 条 + needed 回填全量，扩缓冲重试得全量
        TkMftAggItem small[2];
        size_t needed = 0;
        uint64_t orphan_s = 0, orphan_f = 0;
        size_t written = tk_mft_agg_incr_snapshot(h, small, 2, &needed, &orphan_s, &orphan_f);
        Check(written == 2 && needed == 4, "ABI：小缓冲返回 2 条并回填 needed=4"); // 目录 4 个：5/10/11/20
        std::vector<TkMftAggItem> full(needed);
        written = tk_mft_agg_incr_snapshot(h, full.data(), full.size(), &needed, &orphan_s, &orphan_f);
        Check(written == 4 && needed == 4, "ABI：扩缓冲后全量取回");
        Check(orphan_s == 0 && orphan_f == 0, "ABI：正常树孤儿为零");
        bool sorted_asc = true;
        for (size_t i = 1; i < written; ++i) sorted_asc &= full[i - 1].record_no < full[i].record_no;
        Check(sorted_asc, "ABI：产出按记录号升序");
        bool root_ok = full[0].record_no == 5 && full[0].recursive_size == 450 && full[0].file_count == 5;
        Check(root_ok, "ABI：卷根递归 450 / 5 文件");
        tk_mft_agg_incr_destroy(h);
        tk_mft_agg_incr_destroy(nullptr); // 空句柄销毁安全
    }

    if (g_fail == 0) {
        std::printf("[PASS] test_mft_agg_incr all checks passed\n");
        return 0;
    }
    std::printf("[FAIL] test_mft_agg_incr: %d failure(s)\n", g_fail);
    return 1;
}
