// TaoKeeperScan — C ABI 导出层：给 .NET P/Invoke（模块 M1 空间分析的 Core↔native 对齐）
// 只暴露平坦结构体与整型/指针参数，禁止 std::string 跨边界；
// 平坦结构布局与函数声明在 tk/interop_abi.h（两端共享契约）。
#include "tk/mft.hpp"
#include "tk/interop_abi.h"

#include <algorithm>
#include <cstring>
#include <new>
#include <utility>

namespace tk {
namespace {

constexpr size_t kNameBytes = TK_NAME_BYTES;

/// <summary>内部结构 → 平坦结构（名字按定长缓冲 NUL 结尾拷贝）。</summary>
void FillFlat(const MftFileName& src, TkMftFileName& dst) {
    dst.parent_ref = src.parent_ref;
    dst.created_100ns = src.created_100ns;
    dst.modified_100ns = src.modified_100ns;
    dst.access_100ns = src.access_100ns;
    dst.allocated_size = src.allocated_size;
    dst.real_size = src.real_size;
    dst.is_directory = src.is_directory ? 1u : 0u;
    dst.reserved = 0;
    std::memset(dst.name_utf8, 0, kNameBytes);
    const size_t n = std::min(src.name_utf8.size(), kNameBytes - 1);
    std::memcpy(dst.name_utf8, src.name_utf8.data(), n);
}

/// <summary>平坦条目 → 内部结构（聚合只需字段拷贝，名字按 NUL 截断还原）。</summary>
tk::MftEntry ToInternal(const TkMftEntry& flat) {
    tk::MftEntry e;
    e.record_no = flat.record_no;
    e.info.parent_ref = flat.info.parent_ref;
    e.info.real_size = flat.info.real_size;
    e.info.allocated_size = flat.info.allocated_size;
    e.info.is_directory = flat.info.is_directory != 0;
    e.info.created_100ns = flat.info.created_100ns;
    e.info.modified_100ns = flat.info.modified_100ns;
    e.info.access_100ns = flat.info.access_100ns;
    e.info.name_utf8.assign(flat.info.name_utf8); // string 构造遇 NUL 即止
    return e;
}

} // namespace
} // namespace tk

// ---- C 导出（extern "C"：稳定符号名，无名称修饰） ----

extern "C" {

/// <summary>剥序：NTFS 引用值取记录号（低 48 位）。</summary>
uint64_t tk_mft_record_number(uint64_t ref_value) {
    return tk::MftRecordNumber(ref_value);
}

/// <summary>解析单条 MFT 记录；返回 1=成功 0=拒绝（坏魔数/损坏/无 $FILE_NAME）。</summary>
int tk_parse_mft_file_name(const uint8_t* data, size_t len, TkMftFileName* out) {
    if (data == nullptr || out == nullptr) return 0;
    tk::MftFileName parsed;
    if (!tk::ParseMftFileName(data, len, parsed)) return 0;
    tk::FillFlat(parsed, *out);
    return 1;
}

/// <summary>
/// 顺序遍历 $MFT 字节块。out_cap 为调用方缓冲容量；
/// 返回实际写入条数，needed_out 回填全部有效条数（>cap 时需扩大缓冲重试），skipped_out 回填坏块数。
/// </summary>
size_t tk_walk_mft_records(const uint8_t* data, size_t len, size_t record_size,
                           TkMftEntry* out, size_t out_cap,
                           size_t* needed_out, size_t* skipped_out) {
    if (needed_out) *needed_out = 0;
    if (skipped_out) *skipped_out = 0;
    if (data == nullptr) return 0;
    std::vector<tk::MftEntry> entries;
    size_t skipped = 0;
    tk::WalkMftRecords(data, len, record_size, entries, skipped);
    if (skipped_out) *skipped_out = skipped;
    if (needed_out) *needed_out = entries.size();
    const size_t n = std::min(entries.size(), out_cap);
    for (size_t i = 0; i < n; ++i) {
        out[i].record_no = entries[i].record_no;
        tk::FillFlat(entries[i].info, out[i].info);
    }
    return n;
}

/// <summary>
/// 空间分析聚合 C ABI：entries 为 tk_walk_mft_records 产出的同布局平坦数组（映射键=记录号）。
/// out_cap 为调用方聚合产出缓冲容量；needed_out 回填全部目录聚合条数（>cap 时需扩大缓冲重试，
/// 按 map 升序输出），orphan_size_out/orphan_files_out 回填父链缺失或成环部分的体积与文件数（可空）。
/// 返回 1=成功 0=参数无效。
/// </summary>
int tk_aggregate_dir_sizes(const TkMftEntry* entries, size_t count, uint64_t root_record,
                           TkMftAggItem* out, size_t out_cap,
                           size_t* needed_out, uint64_t* orphan_size_out, uint64_t* orphan_files_out) {
    if (needed_out) *needed_out = 0;
    if (orphan_size_out) *orphan_size_out = 0;
    if (orphan_files_out) *orphan_files_out = 0;
    if ((entries == nullptr && count > 0) || (out == nullptr && out_cap > 0)) return 0;

    std::map<uint64_t, tk::MftEntry> tree;
    for (size_t i = 0; i < count; ++i) {
        tk::MftEntry e = tk::ToInternal(entries[i]);
        tree[e.record_no] = std::move(e); // 同记录号重复时后者覆盖（与映射语义一致）
    }

    auto res = tk::AggregateDirSizes(tree, root_record);
    if (orphan_size_out) *orphan_size_out = res.orphan_size;
    if (orphan_files_out) *orphan_files_out = res.orphan_files;
    if (needed_out) *needed_out = res.per_record.size();

    size_t written = 0;
    for (const auto& [record_no, agg] : res.per_record) {
        if (written >= out_cap) break;
        out[written].record_no = record_no;
        out[written].recursive_size = agg.recursive_size;
        out[written].file_count = agg.file_count;
        ++written;
    }
    return 1;
}

/// <summary>
/// $MFT 连续读取流式解码 C ABI（真机分块直读）：create 返回不透明句柄（record_size=0 返回 null）。
/// feed 任意大小分块（skipped_out 回填本块坏记录数，可空），take 至多 out_cap 条到平坦数组
/// （返回实际取走条数），pending 查待取条数，destroy 释放。
/// </summary>
void* tk_mft_stream_create(size_t record_size) {
    if (record_size == 0) return nullptr;
    return new tk::MftStreamDecoder(record_size);
}

int tk_mft_stream_set_start_offset(void* handle, uint64_t bytes) {
    if (handle == nullptr) return 0;
    static_cast<tk::MftStreamDecoder*>(handle)->SetStartOffset(bytes);
    return 1;
}

size_t tk_mft_stream_feed(void* handle, const uint8_t* data, size_t len, size_t* skipped_out) {
    if (skipped_out) *skipped_out = 0;
    if (handle == nullptr || data == nullptr) return 0;
    size_t skipped = 0;
    const size_t pending = static_cast<tk::MftStreamDecoder*>(handle)->Feed(data, len, skipped);
    if (skipped_out) *skipped_out = skipped;
    return pending;
}

size_t tk_mft_stream_take(void* handle, TkMftEntry* out, size_t out_cap) {
    if (handle == nullptr || (out == nullptr && out_cap > 0)) return 0;
    auto* dec = static_cast<tk::MftStreamDecoder*>(handle);
    std::vector<tk::MftEntry> drained;
    dec->Take(drained, out_cap);
    for (size_t i = 0; i < drained.size(); ++i) {
        out[i].record_no = drained[i].record_no;
        tk::FillFlat(drained[i].info, out[i].info);
    }
    return drained.size();
}

uint64_t tk_mft_stream_pending(void* handle) {
    if (handle == nullptr) return 0;
    return static_cast<uint64_t>(static_cast<tk::MftStreamDecoder*>(handle)->PendingEntries());
}

/// <summary>下一条记录将获得的记录号（坏记录也占号，进度/回传用）；空句柄返回 0。</summary>
uint64_t tk_mft_stream_next_record_no(void* handle) {
    if (handle == nullptr) return 0;
    return static_cast<tk::MftStreamDecoder*>(handle)->NextRecordNo();
}

void tk_mft_stream_destroy(void* handle) {
    delete static_cast<tk::MftStreamDecoder*>(handle);
}
// ---- 分批增量聚合（大卷内存峰值优化）：会话句柄 + 批量喂入 + 非破坏快照 ----

void* tk_mft_agg_incr_create(uint64_t root_record) {
    return new (std::nothrow) tk::MftIncrementalAggregator(root_record);
}

int tk_mft_agg_incr_feed(void* handle, const TkMftEntry* entries, size_t count) {
    if (handle == nullptr || (entries == nullptr && count > 0)) return 0;
    auto* agg = static_cast<tk::MftIncrementalAggregator*>(handle);
    for (size_t i = 0; i < count; ++i) agg->Feed(tk::ToInternal(entries[i]));
    return 1;
}

size_t tk_mft_agg_incr_snapshot(void* handle, TkMftAggItem* out, size_t out_cap,
                                size_t* needed_out, uint64_t* orphan_size_out, uint64_t* orphan_files_out) {
    if (handle == nullptr || (out == nullptr && out_cap > 0)) return 0;
    if (orphan_size_out) *orphan_size_out = 0;
    if (orphan_files_out) *orphan_files_out = 0;
    if (needed_out) *needed_out = 0;
    auto* agg = static_cast<tk::MftIncrementalAggregator*>(handle);
    const auto snap = agg->TakeSnapshot();
    if (orphan_size_out) *orphan_size_out = snap.orphan_size;
    if (orphan_files_out) *orphan_files_out = snap.orphan_files;
    if (needed_out) *needed_out = snap.per_record.size();
    size_t written = 0;
    for (const auto& [record_no, agg_item] : snap.per_record) {
        if (written >= out_cap) break;
        out[written].record_no = record_no;
        out[written].recursive_size = agg_item.recursive_size;
        out[written].file_count = agg_item.file_count;
        ++written;
    }
    return written;
}

void tk_mft_agg_incr_destroy(void* handle) {
    delete static_cast<tk::MftIncrementalAggregator*>(handle);
}

} // extern "C"
